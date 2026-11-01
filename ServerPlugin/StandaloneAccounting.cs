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
        public int Schema = 1;
        public string World, Epoch, StockHash, Signature, Rejected;
        public long Root, AppliedThrough, CheckpointTicks;
        public long[] Members = [], Projectors = [];
        public double[][] Poses;
        public bool Concealed;
        public OfflineProfile[] Profiles;
    }
    private readonly ConcealmentRuntime plugin;
    // MyEntities.Load initializes native grids concurrently before the session is ready.
    private readonly ConcurrentDictionary<long, Record> records = new();
    private readonly ConcurrentDictionary<long, string> opaque = new();
    private readonly ConcurrentDictionary<long, (MyCubeGrid Grid, Record Record, MyObjectBuilder_CubeGrid Builder)> restored = new();
    private bool recovered, disposed, stopped, applying, ownSnapshot;
    internal bool HasSavedState => !records.IsEmpty || !opaque.IsEmpty;
    private long nextCaptureWarning;
    private readonly Harmony harmony;
    private readonly List<(System.Reflection.MethodInfo Target, System.Reflection.MethodInfo Patch)> patches = new();
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
        // Native measurements are invoked only by this standalone host. Cluster
        // installs its own thin forwarding patches when it explicitly owns the core.
        Patch(AccessTools.Method(typeof(MyRefinery), "UpdateProduction", new[] { typeof(uint) }), nameof(MeasureRefinery), true, nameof(Measured));
        Patch(AccessTools.Method(typeof(MyAssembler), "UpdateProduction", new[] { typeof(uint), typeof(bool) }), nameof(MeasureAssembler), true, nameof(Measured));
        Patch(AccessTools.Method(typeof(MyReactor), "ConsumeFuel", new[] { typeof(float) }), nameof(ObserveFuel), true, nameof(FuelObserved));
    }
    private void Patch(System.Reflection.MethodInfo target, string handler, bool prefix, string finalizer = null)
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
    private static void SnapshotAllowed() { if (Current?.applying == true) throw new InvalidOperationException("Accounting transaction is committing; retry the world snapshot after this frame"); }
    private static StandaloneAccounting Current => ConcealmentRuntime.StandaloneOwner ? ConcealmentRuntime.Instance.Manager.Accounting : null;
    private static void Initialized(MyCubeGrid __instance, MyObjectBuilder_EntityBase objectBuilder) => Current?.Restore(__instance, (MyObjectBuilder_CubeGrid)objectBuilder);
    private static void Saving(MyCubeGrid __instance, MyObjectBuilder_EntityBase __result) => Current?.Save(__instance, (MyObjectBuilder_CubeGrid)__result);
    private static void MeasureRefinery(MyRefinery __instance, uint framesFromLastTrigger, out object __state) => __state = !ConcealmentRuntime.StandaloneActive ? null : AccountingApi.BeginMeasurement(__instance, framesFromLastTrigger);
    private static void MeasureAssembler(MyAssembler __instance, uint framesFromLastTrigger, bool forceUpdate, out object __state) => __state = !ConcealmentRuntime.StandaloneActive || forceUpdate ? null : AccountingApi.BeginMeasurement(__instance, framesFromLastTrigger);
    private static void Measured(object __state, Exception __exception) { if (ConcealmentRuntime.StandaloneActive) AccountingApi.FinishMeasurement(__state, __exception == null); }
    private static void ObserveFuel(MyReactor __instance, float timeDeltaMilliseconds, out Action __state) => __state = !ConcealmentRuntime.StandaloneActive ? null : AccountingApi.ObserveReactorFuel(__instance, timeDeltaMilliseconds);
    private static void FuelObserved(Action __state, Exception __exception) { if (ConcealmentRuntime.StandaloneActive && __exception == null) __state?.Invoke(); }

    private void Restore(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        plugin.Manager.Production.Restored(grid, builder);
        string raw = BlockBuilderEntry.Read(builder, AccountingMetadata.AccountingKey);
        if (raw == null) return;
        try
        {
            if (raw.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Oversized standalone claim");
            var record = JsonSerializer.Deserialize<Record>(raw, OfflineProfile.Json) ?? throw new InvalidDataException("Missing standalone claim");
            if (record.CheckpointTicks != MySession.Static.ElapsedGameTime.Ticks) throw new InvalidDataException("Accounting stocks and world clock are from different checkpoints");
            if (record.Schema != 1 || record.Root == 0 || !record.Members.Contains(grid.EntityId) || string.IsNullOrEmpty(record.Epoch)) throw new InvalidDataException("Invalid standalone claim schema/membership");
            records[grid.EntityId] = record;
            if (record.Concealed || record.Rejected != null)
            {
                restored[grid.EntityId] = (grid, record, (MyObjectBuilder_CubeGrid)builder.Clone());
                ConcealGroup.SuppressUpdates(grid, null);
            }
        }
        catch (Exception e)
        {
            opaque[grid.EntityId] = raw;
            ConcealGroup.SuppressUpdates(grid, null);
            plugin.Log.Error(e, "Standalone accounting state for {0} is quarantined; original metadata retained", grid.EntityId);
        }
        grid.Storage?.RemoveValue(AccountingMetadata.AccountingKey);
    }
    private void Save(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        if (applying && !ownSnapshot) throw new InvalidOperationException("Cannot snapshot a partial standalone accounting transaction");
        plugin.Manager.Production.Saving(grid, builder);
        if (opaque.TryGetValue(grid.EntityId, out string raw)) { BlockBuilderEntry.Write(builder, AccountingMetadata.AccountingKey, raw); return; }
        if (!records.TryGetValue(grid.EntityId, out var record)) return;
        // Bind every serialized stock builder to the same native checkpoint clock.
        // A crash during native multi-file promotion may mix checkpoint and sector;
        // reject that combination on load instead of granting an interval twice.
        var stored = new Record { World = record.World, Epoch = record.Epoch, Root = record.Root,
            Members = record.Members, Projectors = record.Projectors, Concealed = record.Concealed,
            AppliedThrough = record.AppliedThrough, Rejected = record.Rejected,
            CheckpointTicks = MySession.Static.ElapsedGameTime.Ticks,
            StockHash = grid.EntityId == record.Root ? record.StockHash : null,
            Signature = grid.EntityId == record.Root ? record.Signature : null,
            Poses = grid.EntityId == record.Root ? record.Poses : null,
            Profiles = grid.EntityId == record.Root ? record.Profiles : null };
        string json = JsonSerializer.Serialize(stored, OfflineProfile.Json);
        if (json.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Standalone accounting claim exceeds storage limit");
        BlockBuilderEntry.Write(builder, AccountingMetadata.AccountingKey, json);
    }
    internal void Update()
    {
        if (disposed || stopped || MySession.Static?.Ready != true) return;
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
            try
            {
                if (root.Record?.Profiles == null || root.Record.World != AccountingStock.World || root.Record.Profiles.Length == 0
                    || !root.Record.Members.Order().SequenceEqual(group.Select(g => g.Grid.EntityId).Order())
                    || group.Any(g => g.Record.Rejected != null || g.Record.Epoch != root.Record.Epoch || g.Record.World != root.Record.World || !g.Record.Members.SequenceEqual(root.Record.Members)))
                    throw new InvalidDataException("Incomplete/mismatched standalone checkpoint");
                RestoreMechanicalLinks(group.ToArray());
                var physical = MyCubeGridGroups.Static.Physical.GetGroup(root.Grid) ?? throw new InvalidDataException("Missing physical group");
                var concealed = new ConcealGroup(physical);
                if (!concealed.Grids.Select(g => g.EntityId).Order().SequenceEqual(root.Record.Members.Order())) throw new InvalidDataException("Saved accounting topology changed");
                var logical = Groups(concealed);
                if (logical.Length != root.Record.Profiles.Length || logical.Any(g => !root.Record.Profiles.Any(p => p.Members.Order().SequenceEqual(g.Select(m => m.EntityId).Order()))))
                    throw new InvalidDataException("Saved logical accounting topology changed");
                AccountingStock.RestoreLoadedEngines(concealed.Grids, group.Select(g => g.Builder).ToArray(), root.Record.StockHash);
                concealed.HoldMechanicalPhysics();
                foreach (var member in concealed.Grids) records[member.EntityId] = root.Record;
                concealed.RestoreProjectors(root.Record.Projectors);
                plugin.Manager.RestoreConcealed(concealed);
            }
            catch (Exception e)
            {
                RejectRecovery(group.ToArray(), e.Message);
                plugin.Log.Error(e, "Accounting group {0} remains suspended; entitlement preserved", group.Key);
            }
        }
        restored.Clear();
    }
    private void RestoreMechanicalLinks((MyCubeGrid Grid, Record Record, MyObjectBuilder_CubeGrid Builder)[] group)
    {
        var session = MySession.Static;
        var grids = group.ToDictionary(g => g.Grid.EntityId, g => g.Grid);
        var claim = group.Single(g => g.Grid.EntityId == g.Record.Root).Record;
        void ValidateOwner()
        {
            if (!ReferenceEquals(Current, this) || session == null || session != MySession.Static || session.IsUnloading
                || MySandboxGame.Static == null || Thread.CurrentThread != MySandboxGame.Static.UpdateThread
                || group.Any(g => g.Grid.MarkedForClose || g.Grid.IsPreview || !ReferenceEquals(MyEntities.GetEntityById(g.Grid.EntityId), g.Grid)
                    || g.Builder.EntityId != g.Grid.EntityId || g.Record.World != AccountingStock.World
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
        foreach (var entry in saved.Values)
        {
            if (entry.Block is MyObjectBuilder_MechanicalConnectionBlock mechanical)
            {
                if (!blocks.TryGetValue(mechanical.EntityId, out var block) || block is not MyMechanicalConnectionBlockBase connection
                    || connection.CubeGrid != entry.Grid || connection.MarkedForClose
                    || ((MyObjectBuilder_MechanicalConnectionBlock)connection.GetObjectBuilderCubeBlock()).TopBlockId != mechanical.TopBlockId)
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
        foreach (var link in links)
        {
            ValidateOwner();
            ValidateLink(link.Base, link.Top);
            if (link.Base.TopBlock == null) link.Base.UpdateAttachState();
            ValidateOwner();
            ValidateLink(link.Base, link.Top);
            if (link.Base.TopBlock != link.Top || link.Top.Stator != link.Base || !link.Base.IsAttached)
                throw new InvalidDataException("Native saved mechanical attachment did not complete");
        }
        if (AccountingStock.Fingerprint(AccountingStock.Builders(grids.Values).Values.Cast<MyObjectBuilder_CubeGrid>()) != stocks)
            throw new InvalidDataException("Native structural recovery changed accounting stocks");
        if (links.Count > 0)
        {
            if (!AccountingStock.SamePoses(loadedPoses, AccountingStock.Poses(grids.Values.OrderBy(g => g.EntityId))))
                throw new InvalidDataException("Native mechanical attachment moved the saved group");
        }

        void ValidateLink(MyMechanicalConnectionBlockBase connection, MyAttachableTopBlockBase top)
        {
            if (connection.MarkedForClose || top.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(connection.EntityId), connection)
                || !ReferenceEquals(MyEntities.GetEntityById(top.EntityId), top) || connection.CubeGrid != saved[connection.EntityId].Grid
                || top.CubeGrid != saved[top.EntityId].Grid || ((MyObjectBuilder_MechanicalConnectionBlock)connection.GetObjectBuilderCubeBlock()).TopBlockId != top.EntityId
                || connection.TopBlock != null && connection.TopBlock != top || top.Stator != null && top.Stator != connection)
                throw new InvalidDataException("Loaded mechanical link identity changed during recovery");
        }
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
        plugin.Manager.Dynamic.GuardConcealedUpdates(loaded.Select(g => g.GetType()).Concat(loaded.SelectMany(g => g.GetFatBlocks()).Select(b => b.GetType())));
    }
    private bool HasClaim(long id) => opaque.ContainsKey(id) || records.TryGetValue(id, out var record) && (record.Concealed || record.Rejected != null);
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
        if (group.Grids.Any(g => opaque.ContainsKey(g.EntityId) || records.TryGetValue(g.EntityId, out var r) && r.Rejected != null)) return false;
        if (!records.TryGetValue(group.Grids[0].EntityId, out var record) || !record.Concealed) return true; // explicit Pause
        if (record.Rejected != null) return false;
        Dictionary<long, MyObjectBuilder_EntityBase> original = null;
        AccountingStockCommit.Prepared rollback = null;
        bool mutated = false;
        try
        {
            if (record.World != AccountingStock.World || group.Grids.Any(g => g.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(g.EntityId), g))
                || !record.Members.Order().SequenceEqual(group.Grids.Select(g => g.EntityId).Order())
                || AccountingEngine.Signature(group.Grids.OrderBy(g => g.EntityId).ToArray(), record.Poses == null) != record.Signature
                || record.Poses != null && !(group.HoldsMechanicalPhysics
                    ? AccountingStock.SameLoadedPoses(group.Grids.OrderBy(g => g.EntityId).ToArray(), record.Poses, AccountingStock.Poses(group.Grids.OrderBy(g => g.EntityId)))
                    : AccountingStock.SamePoses(record.Poses, AccountingStock.Poses(group.Grids.OrderBy(g => g.EntityId))))
                || Groups(group).SelectMany(g => g).Count() != record.Members.Length) throw new InvalidDataException("Accounting identity/topology/settings changed while concealed");
            original = AccountingStock.Builders(group.Grids);
            if (AccountingStock.Fingerprint(original.Values.Cast<MyObjectBuilder_CubeGrid>()) != record.StockHash) throw new InvalidDataException("Live stocks/queues/content changed while concealed");
            var desired = original.ToDictionary(p => p.Key, p => (MyObjectBuilder_EntityBase)p.Value.Clone());
            var modeled = record.Profiles.Select(p => AccountingEngine.ModelForHost(p, Frame)).ToArray();
            for (int i = 0; i < modeled.Length; i++) AccountingEngine.Commit(record.Profiles[i], modeled[i].Model, desired);
            var prepared = AccountingStockCommit.Prepare(group.Grids.ToArray(), desired, modeled.Select(m => m.Model).ToArray());
            rollback = AccountingStockCommit.Prepare(group.Grids.ToArray(), original, record.Profiles);
            applying = true; mutated = true;
            foreach (var action in prepared.Write) action();
            // No hook may snapshot partial stocks. Reentrant external changes are
            // refused before the durable application marker moves.
            ownSnapshot = true;
            string actualHash;
            try { actualHash = AccountingStock.Fingerprint(AccountingStock.Builders(group.Grids).Values.Cast<MyObjectBuilder_CubeGrid>()); }
            finally { ownSnapshot = false; }
            if (actualHash != AccountingStock.Fingerprint(desired.Values.Cast<MyObjectBuilder_CubeGrid>())) throw new InvalidDataException("Native stock commit did not match the prepared result");
            record.AppliedThrough = Frame;
            record.Concealed = false;
            record.Profiles = null; mutated = false; applying = false; // excess beyond the host cap is forfeited, never banked
            foreach (var action in prepared.Notify)
                try { action(); } catch (Exception e) { plugin.Log.Error(e, "Accounting committed; native notification failed for {0}", record.Root); }
            foreach (var value in modeled) plugin.Log.Info("Standalone compensation {0}", JsonSerializer.Serialize(value.Result, OfflineProfile.Json));
            return true;
        }
        catch (Exception e)
        {
            if (rollback != null && mutated)
            {
                try { foreach (var action in rollback.Write) action(); }
                catch (Exception restore) { plugin.Log.Critical(restore, "Accounting rollback failed; group remains concealed"); }
            }
            record.Rejected = e.Message;
            plugin.Log.Error(e, "Accounting group {0} quarantined; no reveal or second claim is allowed", record.Root);
            return false;
        }
        finally { applying = false; ownSnapshot = false; }
    }
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
