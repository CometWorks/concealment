using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Concealment.Accounting;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Shared.Config;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace Shared.Concealment;

internal sealed class StandaloneAccounting : IDisposable
{
    private sealed class Record
    {
        public int Schema = 2;
        public string World, Epoch, StockHash, Signature, Rejected;
        public long Root, AppliedThrough, CheckpointTicks;
        public long[] Members = [], Projectors = [];
        public double[][] Poses;
        public bool Concealed, MechanicalPaid;
        public OfflineProfile[] Profiles;
        public Dictionary<long, MechanicalPhysics.State> Mechanical;
    }
    private readonly ConcealmentRuntime plugin;
    // MyEntities.Load initializes native grids concurrently before the session is ready.
    private readonly ConcurrentDictionary<long, Record> records = new();
    private readonly ConcurrentDictionary<long, string> opaque = new();
    private readonly ConcurrentDictionary<long, (MyCubeGrid Grid, Record Record, MyObjectBuilder_CubeGrid Builder)> restored = new();
    private readonly List<MechanicalPhysics> physics = new();
    private readonly ConcurrentDictionary<long, (MyMechanicalConnectionBlockBase Block, long? Target, MechanicalPhysics.State State)> paidPhysics = new();
    private readonly HashSet<long> initializingPaidPhysics = new();
    private readonly HashSet<ConcealGroup> releasingPhysics = new(ReferenceEqualityComparer.Instance);
    private bool recovered, disposed, stopped, applying, ownSnapshot;
    internal bool HasSavedState => !records.IsEmpty || !opaque.IsEmpty;
    private long nextCaptureWarning;
    private readonly Harmony harmony;
    private readonly List<(System.Reflection.MethodBase Target, System.Reflection.MethodInfo Patch)> patches = new();
    internal bool Enabled => plugin.ConfigData.ProductionConcealment == ProductionConcealmentMode.Approximate;
    internal bool Ready => recovered && plugin.Manager.Production.Ready;
    internal static long Frame => (long)Math.Floor(MySession.Static.ElapsedGameTime.TotalMilliseconds / 16);

    internal StandaloneAccounting(ConcealmentRuntime plugin, Harmony harmony)
    {
        if (ConcealmentRuntime.ClusterMode) throw new InvalidOperationException("Cluster owns grid lifecycle.");
        this.plugin = plugin; this.harmony = harmony;
        AccountingApi.Configure("Standalone", new Dictionary<string, object>
        {
            ["clockFrameSeconds"] = 1.0 / 60,
            ["clock"] = (Func<double>)(() => MySession.Static.ElapsedGameTime.TotalMilliseconds),
            ["setting"] = (Func<string, string>)Setting,
            ["admit"] = (Func<ulong, uint, bool>)((scope, generation) => !disposed && !stopped && recovered && MySession.Static?.Ready == true && !HasClaim(unchecked((long)scope))),
            ["generation"] = (Func<ulong, uint>)(_ => 1),
            ["observe"] = (Func<bool>)(() => !disposed && !stopped && MySession.Static?.Ready == true),
            ["frozen"] = (Func<long, bool>)(id => records.TryGetValue(id, out var r) && r.Concealed),
            ["measure"] = (Func<MyCubeGrid[], bool>)(grids => !disposed && !stopped && grids.All(g => !g.MarkedForClose && (!records.TryGetValue(g.EntityId, out var r) || !r.Concealed))),
            ["zonePending"] = (Func<MySafeZoneBlock, bool>)(_ => false),
        });
        Patch(AccessTools.Method(typeof(MySession), nameof(MySession.GetSector), new[] { typeof(bool) }), nameof(SnapshotAllowed), true);
        Patch(AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.Init), new[] { typeof(MyObjectBuilder_EntityBase) }), nameof(Initialized), false);
        Patch(AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.GetObjectBuilder), new[] { typeof(bool) }), nameof(Saving), false);
        Patch(AccessTools.PropertyGetter(typeof(MyMechanicalConnectionBlockBase), "SafeConstraint"), nameof(ConstraintReading), true);
        Patch(AccessTools.PropertySetter(typeof(MyMechanicalConnectionBlockBase), "Constraint"), nameof(ConstraintChanging), true);
        Patch(AccessTools.Constructor(typeof(Havok.HkConstraint), new[] { typeof(Havok.HkRigidBody), typeof(Havok.HkRigidBody), typeof(Havok.HkConstraintData), typeof(Havok.HkConstraintPriority) }), nameof(ConstraintConstructed), false);
        Patch(AccessTools.Method(typeof(Havok.HkConstraint), "OnAddedToWorld"), nameof(ConstraintAdded), true, nameof(ConstraintAdmissionFinished));
        Patch(AccessTools.Method(typeof(Havok.HkConstraint), "OnRemovedFromWorld"), nameof(ConstraintRemoved), true);
        Patch(AccessTools.PropertySetter(typeof(Havok.HkConstraint), "Enabled"), nameof(ConstraintEnabled), true);
        Patch(AccessTools.PropertySetter(typeof(Havok.HkConstraint), "ForceDisabled"), nameof(ConstraintForced), true);
        foreach (var type in typeof(MyMechanicalConnectionBlockBase).Assembly.GetTypes().Where(t => typeof(MyMechanicalConnectionBlockBase).IsAssignableFrom(t)))
        {
            foreach (string name in new[] { "UpdateOnceBeforeFrame", "UpdateBeforeSimulation" })
            {
                var initialize = type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                if (initialize != null) Patch(initialize, nameof(BeforePaidPhysics), true, nameof(AfterPaidPhysics));
            }
        }
        // Native measurements are invoked only by this standalone host. Cluster
        // installs its own thin forwarding patches when it explicitly owns the core.
        Patch(AccessTools.Method(typeof(MyRefinery), "UpdateProduction", new[] { typeof(uint) }), nameof(MeasureRefinery), true, nameof(Measured));
        Patch(AccessTools.Method(typeof(MyAssembler), "UpdateProduction", new[] { typeof(uint), typeof(bool) }), nameof(MeasureAssembler), true, nameof(Measured));
        Patch(AccessTools.Method(typeof(MyReactor), "ConsumeFuel", new[] { typeof(float) }), nameof(ObserveFuel), true, nameof(FuelObserved));
    }
    private void Patch(System.Reflection.MethodBase target, string handler, bool prefix, string finalizer = null)
    {
        if (target == null) throw new MissingMethodException("Standalone accounting native hook is unavailable: " + handler);
        var patch = AccessTools.Method(typeof(StandaloneAccounting), handler); patches.Add((target, patch));
        var finish = finalizer == null ? null : AccessTools.Method(typeof(StandaloneAccounting), finalizer);
        if (finish != null) patches.Add((target, finish));
        harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch), finalizer: finish == null ? null : new HarmonyMethod(finish));
    }
    private string Setting(string key) => key switch
    {
        "offline.compensate" => "true",
        "offline.compensateMaxHours" => plugin.ConfigData.OfflineCompensationMaxHours.ToString(CultureInfo.InvariantCulture),
        "offline.settleFrames" => "300", "offline.observeSeconds" => "180",
        "offline.rotateProduction" => "true", "offline.solarSteps" => "72",
        "offline.cacheTolerance" => "0.02", "offline.unknownBlocks" => "Freeze",
        "offline.longObservation" => "Skip",
        "offline.quietMeters" => Math.Max(0, plugin.ConfigData.ConcealDistance).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown accounting setting " + key),
    };
    private static void SnapshotAllowed()
    {
        var owner = Current;
        if (owner == null) return;
        owner.PrunePaidPhysics();
        if (owner.applying || owner.initializingPaidPhysics.Count != 0 || owner.releasingPhysics.Count != 0 || !owner.paidPhysics.IsEmpty)
            throw new InvalidOperationException("Accounting or mechanical state is committing; retry the world snapshot after this frame");
    }
    private void PrunePaidPhysics()
    {
        if (MySession.Static?.Ready != true) return;
        foreach (var pending in paidPhysics.ToArray())
            if (pending.Value.Block.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(pending.Key), pending.Value.Block)) paidPhysics.TryRemove(pending.Key, out _);
    }
    private static StandaloneAccounting Current => ConcealmentRuntime.StandaloneOwner ? ConcealmentRuntime.Instance.Manager.Accounting : null;
    private static void Initialized(MyCubeGrid __instance, MyObjectBuilder_EntityBase objectBuilder) => Current?.Restore(__instance, (MyObjectBuilder_CubeGrid)objectBuilder);
    private static void Saving(MyCubeGrid __instance, MyObjectBuilder_EntityBase __result) => Current?.Save(__instance, (MyObjectBuilder_CubeGrid)__result);
    private static void MeasureRefinery(MyRefinery __instance, uint framesFromLastTrigger, out object __state) => __state = !ConcealmentRuntime.StandaloneActive ? null : AccountingApi.BeginMeasurement(__instance, framesFromLastTrigger);
    private static void MeasureAssembler(MyAssembler __instance, uint framesFromLastTrigger, bool forceUpdate, out object __state) => __state = !ConcealmentRuntime.StandaloneActive || forceUpdate ? null : AccountingApi.BeginMeasurement(__instance, framesFromLastTrigger);
    private static void Measured(object __state, Exception __exception) { if (ConcealmentRuntime.StandaloneActive) AccountingApi.FinishMeasurement(__state, __exception == null); }
    private static void ObserveFuel(MyReactor __instance, float timeDeltaMilliseconds, out Action __state) => __state = !ConcealmentRuntime.StandaloneActive ? null : AccountingApi.ObserveReactorFuel(__instance, timeDeltaMilliseconds);
    private static void FuelObserved(Action __state, Exception __exception) { if (ConcealmentRuntime.StandaloneActive && __exception == null) __state?.Invoke(); }

    private static bool ConstraintReading(MyMechanicalConnectionBlockBase __instance, ref Havok.HkConstraint __result)
    {
        var owners = Current?.physics.Where(o => !o.Released && o.Contains(__instance)).ToArray() ?? [];
        if (owners.Length == 0) return true;
        // Native builder serialization reads SafeConstraint, which otherwise
        // disposes/recreates removed objects. Unpaid observation must not admit
        // physics as a side effect of cleanup, inspection or a world save.
        var constraint = __instance.Constraint;
        __result = constraint != null && !constraint.IsDisposed && constraint.InWorld && owners.All(o => o.CanSet(constraint)) ? constraint : null;
        return false;
    }
    private static void ConstraintChanging(MyMechanicalConnectionBlockBase __instance, Havok.HkConstraint __0)
    {
        foreach (var owner in Current?.physics.ToArray() ?? [])
            if (owner.Contains(__instance)) owner.Assign(__instance, __0);
    }
    private static void ConstraintConstructed(Havok.HkConstraint __instance)
    {
        foreach (var owner in Current?.physics.ToArray() ?? []) owner.Constructed(__instance);
    }
    private static void ConstraintAdded(Havok.HkConstraint __instance, out MechanicalPhysics[] __state)
    {
        __state = Current?.physics.ToArray() ?? [];
        var current = Current;
        if (current == null) return;
        var owners = __state;
        bool unpaid = owners.Any(o => o.UnpaidOwns(__instance));
        foreach (var owner in owners)
        {
            owner.Added(__instance, !unpaid);
            if (owner.ReleaseComplete) current.physics.Remove(owner);
        }
    }
    private static void ConstraintAdmissionFinished(Havok.HkConstraint __instance, MechanicalPhysics[] __state)
    {
        foreach (var owner in __state ?? []) owner.AdmissionFinished(__instance);
    }
    private static void ConstraintRemoved(Havok.HkConstraint __instance)
    {
        foreach (var owner in Current?.physics.ToArray() ?? []) owner.Removed(__instance);
    }
    private static bool ConstraintEnabled(Havok.HkConstraint __instance, ref bool __0) => ConstraintSetting(__instance, ref __0, false);
    private static bool ConstraintForced(Havok.HkConstraint __instance, ref bool __0) => ConstraintSetting(__instance, ref __0, true);
    private static bool ConstraintSetting(Havok.HkConstraint constraint, ref bool value, bool force)
    {
        bool nativeReady = true;
        foreach (var owner in Current?.physics.ToArray() ?? [])
        {
            owner.Setting(constraint, value, force, ref value);
            nativeReady &= owner.CanSet(constraint);
        }
        return nativeReady;
    }
    private MechanicalPhysics Protect(IEnumerable<MyCubeGrid> grids)
    {
        var owner = new MechanicalPhysics(grids);
        physics.Add(owner);
        return owner;
    }

    private static void BeforePaidPhysics(MyMechanicalConnectionBlockBase __instance, out bool __state)
    {
        var owner = Current;
        __state = owner != null && owner.paidPhysics.TryGetValue(__instance.EntityId, out var pending)
            && ReferenceEquals(pending.Block, __instance) && owner.initializingPaidPhysics.Add(__instance.EntityId);
    }
    private static void AfterPaidPhysics(MyMechanicalConnectionBlockBase __instance, bool __state, Exception __exception)
    {
        var owner = Current;
        if (!__state || owner == null) return;
        try
        {
            if (__exception != null)
            {
                __instance.NeedsUpdate |= VRage.ModAPI.MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                return;
            }
            if (!owner.paidPhysics.TryGetValue(__instance.EntityId, out var pending)) return;
            bool sameTarget = ReferenceEquals(pending.Block, __instance)
                && MechanicalPhysics.Target(__instance) == pending.Target;
            if (sameTarget)
            {
                bool present = pending.State.Present == (__instance.Constraint != null) && __instance.Constraint?.IsDisposed != true;
                if (!present && pending.State.Present && pending.Target is > 0 && MyEntities.GetEntityById(pending.Target.Value) != null) return;
                if (__instance is MyPistonBase piston && pending.State.Subparts.Present && piston.m_subpartsConstraint == null) return;
                if (present && (__instance is not MyPistonBase parts || pending.State.Subparts.Present == (parts.m_subpartsConstraint != null) && parts.m_subpartsConstraint?.IsDisposed != true)
                    && !owner.physics.Any(p => p.UnpaidOwns(__instance.Constraint) || __instance is MyPistonBase part && p.UnpaidOwns(part.m_subpartsConstraint)))
                {
                    var deferred = MechanicalPhysics.RestorePaid(__instance, pending.State);
                    if (deferred != null) owner.physics.Add(deferred);
                }
            }
            owner.paidPhysics.TryRemove(__instance.EntityId, out _);
        }
        catch (Exception e) { owner.plugin.Log.Error(e, "Native paid mechanical state restoration failed for {0}", __instance.EntityId); }
        finally { owner.initializingPaidPhysics.Remove(__instance.EntityId); }
    }

    private static string MigrateMechanicalHistory(string raw)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw);
        if (root?["Schema"]?.GetValue<int>() != 1 || root["Mechanical"] is not System.Text.Json.Nodes.JsonObject history) return raw;
        void Upgrade(System.Text.Json.Nodes.JsonNode state)
        {
            if (state == null) return;
            if (state["IntentVersion"] != null) throw new InvalidDataException("Unexpected intent version in legacy mechanical history");
            if (state["ForceDisabled"]?.GetValue<bool>() != false)
                throw new InvalidDataException("Legacy forced mechanical history has ambiguous requested Enabled intent");
            state["IntentVersion"] = 1;
            Upgrade(state["Subparts"]);
        }
        foreach (var entry in history) Upgrade(entry.Value);
        return root.ToJsonString();
    }
    private void Restore(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        plugin.Manager.Production.Restored(grid, builder);
        string raw = BlockBuilderEntry.Read(builder, AccountingMetadata.AccountingKey);
        if (raw == null) return;
        try
        {
            if (raw.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Oversized standalone claim");
            var record = JsonSerializer.Deserialize<Record>(MigrateMechanicalHistory(raw), OfflineProfile.Json) ?? throw new InvalidDataException("Missing standalone claim");
            if (record.CheckpointTicks != MySession.Static.ElapsedGameTime.Ticks) throw new InvalidDataException("Accounting stocks and world clock are from different checkpoints");
            if (record.Schema is not (1 or 2) || record.Root == 0 || !record.Members.Contains(grid.EntityId) || string.IsNullOrEmpty(record.Epoch)) throw new InvalidDataException("Invalid standalone claim schema/membership");
            records[grid.EntityId] = record;
            if (record.MechanicalPaid && !record.Concealed && record.Rejected == null)
            {
                var blocks = grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>().ToArray();
                if (record.Profiles != null || record.Mechanical == null || !record.Mechanical.Keys.Order().SequenceEqual(blocks.Select(b => b.EntityId).Order())
                    || record.Mechanical.Values.Any(s => s == null)) throw new InvalidDataException("Incomplete paid mechanical state");
                var saved = builder.CubeBlocks.OfType<MyObjectBuilder_MechanicalConnectionBlock>().ToDictionary(b => b.EntityId);
                foreach (var block in blocks)
                {
                    var state = record.Mechanical[block.EntityId];
                    if (state.IntentVersion != 1 || state.Subparts != null && state.Subparts.IntentVersion != 1)
                        throw new InvalidDataException("Unknown paid mechanical intent representation");
                    if (!state.Present && (state.Enabled || state.ForceDisabled || state.InWorld == true)
                        || block is MyPistonBase && (state.Subparts == null || state.Subparts.InWorld == null)
                        || block is not MyPistonBase && state.Subparts != null)
                        throw new InvalidDataException("Invalid paid mechanical state");
                    paidPhysics[block.EntityId] = (block, saved[block.EntityId].TopBlockId, state);
                }
            }
            if (record.Concealed || record.Rejected != null)
            {
                restored[grid.EntityId] = (grid, record, (MyObjectBuilder_CubeGrid)builder.Clone());
                ConcealGroup.SuppressUpdates(grid, null);
            }
        }
        catch (Exception e)
        {
            foreach (var pending in paidPhysics.ToArray())
                if (ReferenceEquals(pending.Value.Block.CubeGrid, grid)) paidPhysics.TryRemove(pending.Key, out _);
            opaque[grid.EntityId] = raw;
            ConcealGroup.SuppressUpdates(grid, null);
            plugin.Log.Error(e, "Standalone accounting state for {0} is quarantined; original metadata retained", grid.EntityId);
        }
        grid.Storage?.RemoveValue(AccountingMetadata.AccountingKey);
    }
    private void Save(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        if (!ownSnapshot && (applying || paidPhysics.Values.Any(p => ReferenceEquals(p.Block.CubeGrid, grid))
            || releasingPhysics.Any(g => g.Grids.Contains(grid)))) throw new InvalidOperationException("Cannot snapshot a partial standalone accounting transaction");
        plugin.Manager.Production.Saving(grid, builder);
        if (opaque.TryGetValue(grid.EntityId, out string raw)) { BlockBuilderEntry.Write(builder, AccountingMetadata.AccountingKey, raw); return; }
        if (!records.TryGetValue(grid.EntityId, out var record)) return;
        // Bind every serialized stock builder to the same native checkpoint clock.
        // A crash during native multi-file promotion may mix checkpoint and sector;
        // reject that combination on load instead of granting an interval twice.
        bool paidMechanical = !record.Concealed && record.Rejected == null;
        var mechanical = paidMechanical ? MechanicalPhysics.SnapshotLive(grid) : record.Mechanical;
        if (paidMechanical)
        {
            var savedTargets = builder.CubeBlocks.OfType<MyObjectBuilder_MechanicalConnectionBlock>().ToDictionary(b => b.EntityId, b => b.TopBlockId);
            if (!savedTargets.Keys.Order().SequenceEqual(mechanical.Keys.Order()) || grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()
                .Any(b => MechanicalPhysics.Target(b) != savedTargets[b.EntityId]))
                throw new InvalidOperationException("Mechanical state changed during world snapshot; retry after this frame");
        }
        var stored = new Record { World = record.World, Epoch = record.Epoch, Root = record.Root,
            Members = record.Members, Projectors = record.Projectors, Concealed = record.Concealed,
            AppliedThrough = record.AppliedThrough, Rejected = record.Rejected,
            CheckpointTicks = MySession.Static.ElapsedGameTime.Ticks,
            StockHash = grid.EntityId == record.Root ? record.StockHash : null,
            Signature = grid.EntityId == record.Root ? record.Signature : null,
            Poses = grid.EntityId == record.Root ? record.Poses : null,
            Profiles = grid.EntityId == record.Root ? record.Profiles : null,
            MechanicalPaid = paidMechanical, Mechanical = paidMechanical || grid.EntityId == record.Root ? mechanical : null };
        string json = JsonSerializer.Serialize(stored, OfflineProfile.Json);
        if (json.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Standalone accounting claim exceeds storage limit");
        BlockBuilderEntry.Write(builder, AccountingMetadata.AccountingKey, json);
    }
    internal void Update()
    {
        if (disposed || stopped || MySession.Static?.Ready != true) return;
        PrunePaidPhysics();
        if (!recovered)
        {
            recovered = true;
            try
            {
                string cache = CachePath;
                if (File.Exists(cache) && new FileInfo(cache).Length <= AccountingMetadata.MaximumBytes) AccountingApi.CacheRestore(File.ReadAllText(cache));
            }
            catch (Exception e) { plugin.Log.Warning(e, "Rate cache unavailable; native profiling will rebuild it"); }
            RecoverPending();
        }
        AccountingApi.UpdateObservations();
    }
    private void RecoverPending()
    {
        foreach (var group in restored.Values.GroupBy(r => r.Record.Root))
        {
            var root = group.FirstOrDefault(r => r.Grid.EntityId == group.Key);
            var owner = Protect(group.Select(g => g.Grid));
            bool missingHistory = root.Record?.Schema == 2 && root.Record.Mechanical == null;
            try
            {
                if (missingHistory) throw new InvalidDataException("Missing modern root mechanical history");
                owner.RestoreStates(root.Record?.Mechanical);
                if (root.Record?.Profiles == null || root.Record.World != AccountingStock.World || root.Record.Profiles.Length == 0
                    || !root.Record.Members.Order().SequenceEqual(group.Select(g => g.Grid.EntityId).Order())
                    || group.Any(g => g.Record.Rejected != null || g.Record.Epoch != root.Record.Epoch || g.Record.World != root.Record.World || !g.Record.Members.SequenceEqual(root.Record.Members)))
                    throw new InvalidDataException("Incomplete/mismatched standalone checkpoint");
                RestoreMechanicalLinks(group.ToArray(), owner);
                owner.ValidateComplete();
                root.Record.Mechanical ??= owner.Snapshot();
                var physical = MyCubeGridGroups.Static.Physical.GetGroup(root.Grid) ?? throw new InvalidDataException("Missing physical group");
                var concealed = new ConcealGroup(physical);
                if (!concealed.Grids.Select(g => g.EntityId).Order().SequenceEqual(root.Record.Members.Order())) throw new InvalidDataException("Saved accounting topology changed");
                var logical = Groups(concealed);
                if (logical.Length != root.Record.Profiles.Length || logical.Any(g => !root.Record.Profiles.Any(p => p.Members.Order().SequenceEqual(g.Select(m => m.EntityId).Order()))))
                    throw new InvalidDataException("Saved logical accounting topology changed");
                AccountingStock.RestoreLoadedEngines(concealed.Grids, group.Select(g => g.Builder).ToArray(), root.Record.StockHash);
                concealed.Physics = owner;
                foreach (var member in concealed.Grids) records[member.EntityId] = root.Record;
                concealed.RestoreProjectors(root.Record.Projectors);
                plugin.Manager.RestoreConcealed(concealed);
            }
            catch (Exception e)
            {
                owner.Hold();
                if (root.Record?.Schema == 1) root.Record.Mechanical ??= owner.Snapshot();
                if (missingHistory) opaque.TryAdd(root.Grid.EntityId, BlockBuilderEntry.Read(root.Builder, AccountingMetadata.AccountingKey));
                RejectRecovery(group.ToArray(), e.Message);
                plugin.Log.Error(e, "Accounting group {0} remains suspended; entitlement preserved", group.Key);
            }
        }
        restored.Clear();
    }
    private void RestoreMechanicalLinks((MyCubeGrid Grid, Record Record, MyObjectBuilder_CubeGrid Builder)[] group, MechanicalPhysics owner)
    {
        var session = MySession.Static;
        var grids = group.ToDictionary(g => g.Grid.EntityId, g => g.Grid);
        var claim = group.Single(g => g.Grid.EntityId == g.Record.Root).Record;
        string epoch = claim.Epoch, world = claim.World;
        long root = claim.Root;
        var members = claim.Members.ToArray();
        void ValidateOwner()
        {
            if (!ReferenceEquals(Current, this) || session == null || session != MySession.Static || session.IsUnloading
                || MySandboxGame.Static == null || Thread.CurrentThread != MySandboxGame.Static.UpdateThread
                || group.Any(g => g.Grid.MarkedForClose || g.Grid.IsPreview || !ReferenceEquals(MyEntities.GetEntityById(g.Grid.EntityId), g.Grid)
                    || g.Builder.EntityId != g.Grid.EntityId || g.Record.World != AccountingStock.World || g.Record.World != world
                    || g.Record.Epoch != epoch || g.Record.Root != root || g.Record.Rejected != null || !g.Record.Members.SequenceEqual(members)
                    || !records.TryGetValue(g.Grid.EntityId, out var record) || !ReferenceEquals(record, g.Record) || opaque.ContainsKey(g.Grid.EntityId)))
                throw new InvalidDataException("Mechanical recovery no longer owns the loaded checkpoint");
            foreach (var grid in grids.Values)
            {
                var physical = MyCubeGridGroups.Static.Physical.GetGroup(grid);
                var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
                if (physical != null && physical.Nodes.Any(n => !grids.TryGetValue(n.NodeData.EntityId, out var member) || !ReferenceEquals(member, n.NodeData))
                    || logical != null && logical.Nodes.Any(n => !grids.TryGetValue(n.NodeData.EntityId, out var member) || !ReferenceEquals(member, n.NodeData)))
                    throw new InvalidDataException("Loaded accounting closure crosses saved membership");
            }
        }
        ValidateOwner();
        foreach (var grid in grids.Values) ConcealGroup.SuppressUpdates(grid, null);
        plugin.Manager.Dynamic.GuardConcealedUpdates(grids.Values.Select(g => g.GetType()).Concat(grids.Values.SelectMany(g => g.GetFatBlocks()).Select(b => b.GetType())));
        var saved = group.SelectMany(g => g.Builder.CubeBlocks.Select(b => (Grid: g.Grid, Block: b))).Where(b => b.Block.EntityId != 0).ToDictionary(b => b.Block.EntityId);
        var blocks = grids.Values.SelectMany(g => g.GetFatBlocks()).ToDictionary(b => b.EntityId);
        var links = new List<(MyMechanicalConnectionBlockBase Base, MyAttachableTopBlockBase Top)>();
        var completedLinks = new HashSet<long>();
        void ValidateBlocks()
        {
            var current = grids.Values.SelectMany(g => g.GetFatBlocks()).ToArray();
            if (current.Length != blocks.Count || current.Any(b => !blocks.TryGetValue(b.EntityId, out var original) || !ReferenceEquals(original, b)
                || b.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(b.EntityId), b)
                || !saved.TryGetValue(b.EntityId, out var entry) || !ReferenceEquals(b.CubeGrid, entry.Grid)))
                throw new InvalidDataException("Loaded block identity changed during recovery");
            foreach (var entry in saved.Values)
            {
                if (entry.Block is MyObjectBuilder_MechanicalConnectionBlock mechanical)
                {
                    if (!blocks.TryGetValue(mechanical.EntityId, out var block) || block is not MyMechanicalConnectionBlockBase connection
                        || connection.CubeGrid != entry.Grid
                        || MechanicalPhysics.Target(connection) != mechanical.TopBlockId
                        || mechanical.TopBlockId is not > 0 && connection.TopBlock != null)
                        throw new InvalidDataException("Saved mechanical base identity/target changed");
                    if (mechanical.TopBlockId is > 0)
                    {
                        if (!blocks.TryGetValue(mechanical.TopBlockId.Value, out var target) || target is not MyAttachableTopBlockBase top
                            || connection.TopBlock != null && (!ReferenceEquals(connection.TopBlock, top) || !ReferenceEquals(top.Stator, connection) || !connection.IsAttached)
                            || completedLinks.Contains(connection.EntityId) && !ReferenceEquals(connection.TopBlock, top)
                            || top.Stator != null && !ReferenceEquals(top.Stator, connection))
                            throw new InvalidDataException("Loaded mechanical reciprocal link changed");
                    }
                }
                if (entry.Block is MyObjectBuilder_AttachableTopBlockBase attached)
                {
                    if (!blocks.TryGetValue(attached.EntityId, out var block) || block is not MyAttachableTopBlockBase top || top.CubeGrid != entry.Grid
                        || top.Stator != null && top.Stator.EntityId != attached.ParentEntityId)
                        throw new InvalidDataException("Loaded mechanical top identity/parent changed");
                }
            }
        }
        ValidateBlocks();
        foreach (var entry in saved.Values)
        {
            if (entry.Block is MyObjectBuilder_MechanicalConnectionBlock mechanical)
            {
                if (!blocks.TryGetValue(mechanical.EntityId, out var block) || block is not MyMechanicalConnectionBlockBase connection
                    || connection.CubeGrid != entry.Grid || connection.MarkedForClose
                    || MechanicalPhysics.Target(connection) != mechanical.TopBlockId)
                    throw new InvalidDataException("Saved mechanical base identity/target changed");
                if (mechanical.TopBlockId is not > 0)
                {
                    if (connection.TopBlock != null) throw new InvalidDataException("Unexpected loaded mechanical attachment");
                    continue;
                }
                if (!saved.TryGetValue(mechanical.TopBlockId.Value, out var target) || target.Block is not MyObjectBuilder_AttachableTopBlockBase topBuilder
                    || topBuilder.ParentEntityId != mechanical.EntityId || !blocks.TryGetValue(topBuilder.EntityId, out var topBlock)
                    || topBlock is not MyAttachableTopBlockBase top || top.CubeGrid != target.Grid || top.MarkedForClose
                    || connection.TopBlock != null && connection.TopBlock != top || top.Stator != null && top.Stator != connection)
                    throw new InvalidDataException("Saved mechanical top/parent identity changed");
                links.Add((connection, top));
            }
            if (entry.Block is MyObjectBuilder_AttachableTopBlockBase attached && attached.ParentEntityId != 0
                && (!saved.TryGetValue(attached.ParentEntityId, out var parent) || parent.Block is not MyObjectBuilder_MechanicalConnectionBlock basis || basis.TopBlockId != attached.EntityId))
                throw new InvalidDataException("Saved mechanical link is not reciprocal");
        }
        var profileMembers = claim.Profiles.SelectMany(p => p.Members).Order().ToArray();
        if (!profileMembers.SequenceEqual(grids.Keys.Order()) || claim.Profiles.Any(p => p.Members.Length == 0)
            || links.Any(l => !claim.Profiles.Any(p => p.Members.Contains(l.Base.CubeGrid.EntityId) && p.Members.Contains(l.Top.CubeGrid.EntityId))))
            throw new InvalidDataException("Saved logical accounting profile membership changed");
        var poses = group.OrderBy(g => g.Grid.EntityId).Select(g => g.Builder.PositionAndOrientation?.GetMatrix()
            ?? throw new InvalidDataException("Saved mechanical grid pose is missing")).ToArray();
        if (links.Count > 0)
        {
            var ordered = grids.Values.OrderBy(g => g.EntityId).ToArray();
            if (!AccountingStock.SameLoadedPoses(ordered, claim.Poses, poses.Select(AccountingStock.Pose).ToArray()))
                throw new InvalidDataException("Saved mechanical checkpoint pose changed");
            if (!AccountingStock.SameLoadedPoses(ordered, claim.Poses, AccountingStock.Poses(ordered)))
                throw new InvalidDataException("Loaded mechanical checkpoint pose changed");
        }
        // Native BEFORE_NEXT_FRAME also runs unrelated block work. Only complete
        // its saved-ID structural branch; production guards never open.
        AccountingStock.RestoreLoadedEngines(grids.Values, group.Select(g => g.Builder).ToArray(), claim.StockHash);
        string stocks = AccountingStock.Fingerprint(AccountingStock.Builders(grids.Values).Values.Cast<MyObjectBuilder_CubeGrid>());
        var loadedPoses = AccountingStock.Poses(grids.Values.OrderBy(g => g.EntityId));
        void ValidateBasis()
        {
            ValidateOwner();
            ValidateBlocks();
            if (!owner.Intact) throw new InvalidDataException("Mechanical physics ownership changed during recovery");
            if (AccountingStock.Fingerprint(AccountingStock.Builders(grids.Values).Values.Cast<MyObjectBuilder_CubeGrid>()) != stocks)
                throw new InvalidDataException("Native structural recovery changed accounting stocks");
            if (!AccountingStock.SamePoses(loadedPoses, AccountingStock.Poses(grids.Values.OrderBy(g => g.EntityId))))
                throw new InvalidDataException("Native mechanical attachment moved the saved group");
        }
        void Structural(MyMechanicalConnectionBlockBase block, Action call)
        {
            ValidateBasis();
            applying = true;
            try { owner.Structural(block, call); }
            finally
            {
                applying = false;
                foreach (var grid in grids.Values) ConcealGroup.SuppressUpdates(grid, null);
            }
            ValidateBasis();
        }
        ValidateBasis();
        foreach (var link in links)
        {
            if (link.Base.TopBlock == null)
            {
                if (link.Base is MyPistonBase piston && piston.Subpart3 == null)
                    Structural(piston, piston.LoadSubparts);
                Structural(link.Base, link.Base.UpdateAttachState);
            }
            ValidateBasis();
            if (link.Base.TopBlock != link.Top || link.Top.Stator != link.Base || !link.Base.IsAttached)
                throw new InvalidDataException("Native saved mechanical attachment did not complete");
            completedLinks.Add(link.Base.EntityId);
        }
        ValidateBasis();
    }

    private void RejectRecovery((MyCubeGrid Grid, Record Record, MyObjectBuilder_CubeGrid Builder)[] group, string reason)
    {
        // Keep each surviving row, including conflicting epochs. Uncovered members
        // carry opaque copies of the evidence, never a reconstructed root profile.
        string evidence = BlockBuilderEntry.Read(group[0].Builder, AccountingMetadata.AccountingKey);
        var pending = new Queue<long>(group.SelectMany(g => g.Record.Members.Append(g.Record.Root)).Concat(group.Select(g => g.Grid.EntityId)));
        var affected = new HashSet<long>();
        var loaded = new List<MyCubeGrid>();
        while (pending.TryDequeue(out long id))
        {
            if (!affected.Add(id)) continue;
            if (records.TryGetValue(id, out var record))
            {
                record.Rejected ??= reason;
                foreach (long member in record.Members.Append(record.Root)) pending.Enqueue(member);
            }
            else opaque.TryAdd(id, evidence);
            if (MyEntities.GetEntityById(id) is not MyCubeGrid grid) continue;
            loaded.Add(grid);
            var physical = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            if (physical != null) foreach (var member in physical.Nodes) pending.Enqueue(member.NodeData.EntityId);
            var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
            if (logical != null) foreach (var member in logical.Nodes) pending.Enqueue(member.NodeData.EntityId);
            ConcealGroup.SuppressUpdates(grid, null);
        }
        Protect(loaded).Hold();
        plugin.Manager.Dynamic.GuardConcealedUpdates(loaded.Select(g => g.GetType()).Concat(loaded.SelectMany(g => g.GetFatBlocks()).Select(b => b.GetType())));
    }
    private bool HasClaim(long id) => opaque.ContainsKey(id) || paidPhysics.Values.Any(p => p.Block.CubeGrid.EntityId == id)
        || records.TryGetValue(id, out var record) && (record.Concealed || record.Rejected != null);
    private static MyCubeGrid[][] Groups(ConcealGroup group)
    {
        var remaining = group.Grids.OrderBy(g => g.EntityId).ToDictionary(g => g.EntityId);
        var result = new List<MyCubeGrid[]>();
        foreach (var grid in remaining.Values.ToArray())
        {
            if (!remaining.ContainsKey(grid.EntityId)) continue;
            var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
            var grids = logical == null ? new[] { grid } : logical.Nodes.Select(n => n.NodeData).OrderBy(g => g.EntityId).ToArray();
            if (grids.Any(g => !group.Grids.Contains(g))) throw new InvalidOperationException("Logical accounting group crosses concealment boundary");
            foreach (var member in grids) remaining.Remove(member.EntityId);
            result.Add(grids);
        }
        return result.ToArray();
    }
    internal void Cancel(ConcealGroup group) => AccountingApi.CancelScope(unchecked((ulong)group.Grids.Min(g => g.EntityId)));
    internal bool Blocks(ConcealGroup group) => group.Grids.Any(g => opaque.ContainsKey(g.EntityId) || records.TryGetValue(g.EntityId, out var r) && r.Rejected != null)
        || Enabled && plugin.Manager.Production.Blocks(group.Grids);
    internal bool TryBegin(ConcealGroup group, Action beforeFreeze)
    {
        if (stopped || group.Grids.Any(g => HasClaim(g.EntityId))) return false;
        if (!Enabled) { beforeFreeze(); return true; }
        if (!Ready || Blocks(group) || group.Grids.Any(g => AccountingEngine.Readiness(g) != null || g.MarkedForClose || g.IsPreview)) return false;
        ulong scope = unchecked((ulong)group.Grids.Min(g => g.EntityId));
        MyCubeGrid[][] profiles;
        OfflineProfile[] captured;
        Record record;
        try
        {
            profiles = Groups(group);
            if (!AccountingApi.Prepare(scope, profiles, group.Grids.ToArray(), false)) return false;
            captured = AccountingApi.Read(AccountingApi.Capture(scope, profiles, group.Grids.ToArray(), false));
            if (captured.Length != profiles.Length) return false;
            record = new Record { World = AccountingStock.World, Epoch = Guid.NewGuid().ToString("N"), Root = group.Grids.Min(g => g.EntityId), Members = group.Grids.Select(g => g.EntityId).Order().ToArray(), Concealed = true, Profiles = captured };
            if (JsonSerializer.Serialize(record, OfflineProfile.Json).Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Accounting profile too large");
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            AccountingApi.CancelScope(scope);
            if (Frame >= nextCaptureWarning)
            {
                nextCaptureWarning = Frame + 3600;
                plugin.Log.Warning(e, "Accounting capture refused for group {0}; native simulation continues", group.Id);
            }
            return false;
        }
        beforeFreeze();
        foreach (var grid in group.Grids) records[grid.EntityId] = record;
        // Protection is conservative while no live costs are settled. Keep native
        // settings/paid credit; reveal restores through the native power lifecycle.
        foreach (var zone in group.Grids.SelectMany(g => g.GetFatBlocks()).OfType<MySafeZoneBlock>()) zone.Components.Get<MySafeZoneComponent>()?.SafeZoneRemove_Server();
        return true;
    }
    internal void Frozen(ConcealGroup group)
    {
        if (!records.TryGetValue(group.Grids[0].EntityId, out var record) || !record.Concealed) return;
        group.Physics = Protect(group.Grids);
        group.Physics.ValidateComplete();
        record.Mechanical = group.Physics.Snapshot();
        record.Projectors = group.Projectors.ToArray();
        var ordered = group.Grids.OrderBy(g => g.EntityId).ToArray();
        record.Signature = AccountingEngine.Signature(ordered, false);
        record.Poses = AccountingStock.Poses(ordered);
        var builders = AccountingStock.Builders(group.Grids);
        record.StockHash = AccountingStock.Fingerprint(builders.Values.Cast<MyObjectBuilder_CubeGrid>());
        var blocks = builders.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        foreach (var store in record.Profiles.SelectMany(p => p.Storage))
            store.Amount = blocks[store.Block] switch
            {
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock b => b.CurrentStoredPower,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_GasTank t => t.FilledRatio * store.Capacity,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_FueledPowerProducer e => e.Capacity,
                Sandbox.Common.ObjectBuilders.MyObjectBuilder_JumpDrive j => j.StoredPower,
                _ => store.Amount,
            };
    }
    internal bool Settle(ConcealGroup group)
    {
        if (applying) return false;
        if (group.Grids.Any(g => opaque.ContainsKey(g.EntityId) || records.TryGetValue(g.EntityId, out var r) && r.Rejected != null)) return false;
        if (!records.TryGetValue(group.Grids[0].EntityId, out var record) || !record.Concealed) return true; // explicit Pause
        if (record.Rejected != null) return false;
        Dictionary<long, MyObjectBuilder_EntityBase> original = null;
        AccountingStockCommit.Prepared rollback = null;
        bool mutated = false;
        string settingsSignature = null;
        var lockedInventories = new List<MyInventory>();
        var beforeContents = typeof(MyInventoryBase).GetEvent("BeforeContentsChanged");
        Action<MyInventoryBase> refuseStockMutation = _ => throw new InvalidOperationException("Native inventory mutation during accounting commit; retry after settlement");
        try
        {
            void ValidateIdentity()
            {
                if (record.World != AccountingStock.World || !group.MechanicalPhysicsIntact || group.Grids.Any(g => g.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(g.EntityId), g))
                || !record.Members.Order().SequenceEqual(group.Grids.Select(g => g.EntityId).Order())
                || AccountingEngine.Signature(group.Grids.OrderBy(g => g.EntityId).ToArray(), record.Poses == null, settingsSignature == null) != (settingsSignature ?? record.Signature)
                || record.Poses != null && !(group.HoldsMechanicalPhysics
                    ? AccountingStock.SameLoadedPoses(group.Grids.OrderBy(g => g.EntityId).ToArray(), record.Poses, AccountingStock.Poses(group.Grids.OrderBy(g => g.EntityId)))
                    : AccountingStock.SamePoses(record.Poses, AccountingStock.Poses(group.Grids.OrderBy(g => g.EntityId))))
                || Groups(group).SelectMany(g => g).Count() != record.Members.Length) throw new InvalidDataException("Accounting identity/topology/settings changed while concealed");
                if (!records.TryGetValue(record.Root, out var current) || !ReferenceEquals(current, record))
                    throw new InvalidDataException("Accounting claim owner changed during commit");
            }
            void ValidateResult(Dictionary<long, MyObjectBuilder_EntityBase> expected)
            {
                ValidateIdentity();
                ownSnapshot = true;
                try
                {
                    if (AccountingStock.Fingerprint(AccountingStock.Builders(group.Grids).Values.Cast<MyObjectBuilder_CubeGrid>())
                        != AccountingStock.Fingerprint(expected.Values.Cast<MyObjectBuilder_CubeGrid>()))
                        throw new InvalidDataException("Native stock commit did not match the prepared result");
                }
                finally { ownSnapshot = false; }
            }
            ValidateIdentity();
            original = AccountingStock.Builders(group.Grids);
            if (AccountingStock.Fingerprint(original.Values.Cast<MyObjectBuilder_CubeGrid>()) != record.StockHash) throw new InvalidDataException("Live stocks/queues/content changed while concealed");
            var desired = original.ToDictionary(p => p.Key, p => (MyObjectBuilder_EntityBase)p.Value.Clone());
            var modeled = record.Profiles.Select(p => AccountingEngine.ModelForHost(p, Frame)).ToArray();
            for (int i = 0; i < modeled.Length; i++) AccountingEngine.Commit(record.Profiles[i], modeled[i].Model, desired);
            var stage = original.ToDictionary(p => p.Key, p => (MyObjectBuilder_EntityBase)p.Value.Clone());
            var prepared = AccountingStockCommit.Prepare(group.Grids.ToArray(), desired, modeled.Select(m => m.Model).ToArray(), stage: stage);
            rollback = AccountingStockCommit.Prepare(group.Grids.ToArray(), original, record.Profiles, restore: true);
            foreach (var inventory in group.Grids.SelectMany(g => g.GetFatBlocks()).SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(b.GetInventory)).Where(i => i != null).Distinct())
            {
                beforeContents.AddEventHandler(inventory, refuseStockMutation);
                lockedInventories.Add(inventory);
            }
            applying = true; mutated = true;
            ValidateIdentity(); // Preparation/snapshot callbacks must still match the original recipe-bearing basis.
            settingsSignature = AccountingEngine.Signature(group.Grids.OrderBy(g => g.EntityId).ToArray(), record.Poses == null, false);
            for (int i = 0; i < prepared.Write.Length; i++)
            {
                ValidateResult(stage);
                prepared.Advance[i]();
                prepared.Write[i]();
                ValidateResult(stage);
            }
            ValidateResult(desired);
            foreach (var action in prepared.Notify) { ValidateIdentity(); action(); ValidateResult(desired); }
            ValidateResult(desired);
            record.AppliedThrough = Frame;
            record.Concealed = false;
            record.Profiles = null; mutated = false;
            group.Physics?.AuthorizeRelease();
            if (group.Physics?.DeferredRelease == true)
            {
                var states = group.Physics.Snapshot();
                foreach (var piston in group.Grids.SelectMany(g => g.GetFatBlocks()).OfType<MyPistonBase>())
                    if (states[piston.EntityId].Subparts?.InWorld == true)
                        paidPhysics[piston.EntityId] = (piston, MechanicalPhysics.Target(piston), states[piston.EntityId]);
            }
            releasingPhysics.Add(group); // excess beyond the host cap is forfeited, never banked
            if (group.Physics?.DeferredRelease != true) physics.Remove(group.Physics);
            foreach (var value in modeled) plugin.Log.Info("Standalone compensation {0}", JsonSerializer.Serialize(value.Result, OfflineProfile.Json));
            return true;
        }
        catch (Exception e)
        {
            if (rollback != null && mutated)
            {
                foreach (var action in rollback.Write)
                    try { action(); }
                    catch (Exception restore) { plugin.Log.Critical(restore, "Accounting rollback write failed; group remains concealed"); }
                ownSnapshot = true;
                try
                {
                    if (AccountingStock.Fingerprint(AccountingStock.Builders(group.Grids).Values.Cast<MyObjectBuilder_CubeGrid>())
                        != AccountingStock.Fingerprint(original.Values.Cast<MyObjectBuilder_CubeGrid>()))
                        throw new InvalidDataException("Accounting rollback did not restore original stocks");
                }
                catch (Exception restore) { plugin.Log.Critical(restore, "Accounting rollback verification failed; group remains concealed"); }
                finally { ownSnapshot = false; }
            }
            group.Physics?.Hold();
            record.Rejected = e.Message;
            plugin.Log.Error(e, "Accounting group {0} quarantined; no reveal or second claim is allowed", record.Root);
            return false;
        }
        finally
        {
            foreach (var inventory in lockedInventories) beforeContents.RemoveEventHandler(inventory, refuseStockMutation);
            applying = false; ownSnapshot = false;
        }
    }
    internal void Revealed(ConcealGroup group) => releasingPhysics.Remove(group);
    private string CachePath => Path.Combine(VRage.FileSystem.MyFileSystem.UserDataPath, "ConcealmentRates." + AccountingStock.Hash(System.Text.Encoding.UTF8.GetBytes(AccountingStock.World)) + ".json");
    internal void SaveCache()
    {
        if (disposed || stopped || MySession.Static == null) return;
        try
        {
            string value = AccountingApi.CacheSnapshot();
            if (value.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Rate cache too large");
            File.WriteAllText(CachePath + ".tmp", value);
            File.Move(CachePath + ".tmp", CachePath, true);
        }
        catch (Exception e) { plugin.Log.Warning(e, "Rate cache save failed; stocks and claims remain in native world builders"); }
    }

    internal void Stop()
    {
        if (stopped) return;
        stopped = true;
        // The first manager update can fail before recovery. Protect the whole
        // restored group before leaving only its serializer/guards running.
        try { RecoverPending(); }
        finally { AccountingApi.EndStandaloneSession(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        foreach (var method in patches.Distinct()) harmony.Unpatch(method.Target, method.Patch);
    }
}
