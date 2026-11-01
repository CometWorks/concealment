using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Logging;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Components.Session;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Groups;
using VRage.ModAPI;
using VRageMath;

namespace Shared.Concealment;

public sealed class ConcealmentManager : IDisposable
{
    private static readonly MethodInfo OnEntityClosingMethod =
        typeof(MyEntityComponentUpdater).GetMethod(
            "OnEntityClosing",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(MyEntity) },
            null
        );

    private readonly ConcealmentRuntime plugin;
    private MySession session;
    private MyMultiplayerBase multiplayer;
    private readonly IPluginLogger log;
    private readonly List<ConcealGroup> intersectGroups = new List<ConcealGroup>();
    private readonly Dictionary<long, Timer> keepAliveTimers = new Dictionary<long, Timer>();

    private readonly MyDynamicAABBTreeD concealedAabbTree = new MyDynamicAABBTreeD(
        MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION
    );

    private Action<MyEntity> onEntityClosingAction;
    private GridBackups gridBackups;
    private Action<MyObjectBuilder_Checkpoint> onSavingCheckpoint;
    private Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction keepAliveAction;
    private ulong counter;
    private bool initialized;
    private bool ready;
    private bool configChanged;
    private long readyTick;
    private bool disposed, stopped;
    private bool enabledLastTick;

    public List<ConcealGroup> ConcealedGroups { get; } = new List<ConcealGroup>();
    public DynamicConcealmentManager Dynamic { get; }
#if MAGNETAR
    public LegacyBoostMigration Production { get; }
    internal StandaloneAccounting Accounting { get; }
    private readonly Dictionary<long, (ConcealGroup Group, double Distance)> pendingConceals = new();
    private bool modeChanged;

#else
    public ProductionBoostManager Production { get; }
#endif

    internal bool RequiresWorldRetention =>
#if MAGNETAR
        Accounting.HasSavedState || Production.HasSavedState;
#else
        false;
#endif

    private PluginConfig Config => plugin.ConfigData;

    public ConcealmentManager(ConcealmentRuntime plugin, HarmonyLib.Harmony harmony)
    {
        if (ConcealmentRuntime.ClusterMode)
            throw new InvalidOperationException("Cluster owns grid lifecycle.");

        this.plugin = plugin;
        session = MySession.Static;
        log = plugin.Log;
        enabledLastTick = Config.Enabled;
        harmony.Patch(
            AccessTools.Method(
                typeof(MyEntities),
                nameof(MyEntities.RegisterForUpdate),
                new[] { typeof(MyEntity) }
            ),
            prefix: new HarmonyMethod(
                AccessTools.Method(
                    typeof(Shared.Concealment.ConcealGroup),
                    nameof(Shared.Concealment.ConcealGroup.PrefixRegisterForUpdate)
                )
            )
        );
        harmony.Patch(
            AccessTools.Method(
                typeof(MyProjectorBase),
                nameof(MyProjectorBase.GetObjectBuilderCubeBlock)
            ),
            postfix: new HarmonyMethod(
                AccessTools.Method(
                    typeof(ConcealmentManager),
                    nameof(PostfixProjectorObjectBuilder)
                )
            )
        );
        harmony.Patch(AccessTools.Method(typeof(MyEntityComponentUpdater), nameof(MyEntityComponentUpdater.AddEntityComponents)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Shared.Concealment.ConcealGroup), nameof(Shared.Concealment.ConcealGroup.PrefixRegisterForUpdate))));
        harmony.Patch(AccessTools.Method(typeof(MyGameLogic), nameof(MyGameLogic.RegisterForUpdate)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Shared.Concealment.ConcealGroup), nameof(Shared.Concealment.ConcealGroup.PrefixGameLogic))));
        Dynamic = new DynamicConcealmentManager(plugin, harmony);
#if MAGNETAR
        // Install before native loading; saved concealed resource blocks must not
        // execute their pending 100-frame update before the first host tick.
        Dynamic.GuardConcealedUpdates(new[] { typeof(MyCubeGrid), typeof(MyBatteryBlock),
            typeof(MyReactor), typeof(MyRefinery), typeof(MyAssembler), typeof(MyGasTank),
            typeof(MyGasGenerator), typeof(MyJumpDrive), typeof(MyGasFueledPowerProducer),
            typeof(SpaceEngineers.Game.Entities.Blocks.SafeZone.MySafeZoneBlock) });
        Production = new LegacyBoostMigration(plugin, harmony);
        Accounting = new StandaloneAccounting(plugin, harmony);
#else
        Production = new ProductionBoostManager(plugin, harmony, plugin.DataDirectory);
#endif
        Config.PropertyChanged += ConfigOnPropertyChanged;
    }

    public void Update()
    {
        if (stopped || MyAPIGateway.Session == null)
            return;

        InitializeSessionFeatures();
        if (enabledLastTick && !Config.Enabled) RevealAll();
        enabledLastTick = Config.Enabled;
        Production.Update(plugin.Tick);
#if MAGNETAR
        Accounting.Update();
#endif
        Dynamic.Update();
#if MAGNETAR
        if (modeChanged) { modeChanged = false; RevealAll(); }
        if (plugin.Tick % 100 == 0) AdvancePending();
#endif

        if (enabledLastTick && !Config.Enabled)
            RevealAll();
        enabledLastTick = Config.Enabled;

        if (initialized && !ready && plugin.Tick >= readyTick)
            ready = true;

        if (!Config.Enabled || !ready)
            return;

        if (counter % (ulong)Math.Max(1, Config.ConcealInterval) == 0)
            ConcealGrids(Config.ConcealDistance);

        if (counter % (ulong)Math.Max(1, Config.RevealInterval) == 0)
            RevealGrids(Config.RevealDistance);

        counter++;
    }

    public int ConcealGrids(double distanceFromPlayers = 0)
    {
        if (stopped) return 0;
        // An explicit scan is allowed while periodic concealment is disabled.
        enabledLastTick = Config.Enabled;
        log.Debug("Concealing grids");

        var concealed = 0;
        var playerSpheres = GetPlayerViewSpheres(distanceFromPlayers);
        var groups = new ConcurrentBag<ConcealGroup>();
        var stopwatch = Stopwatch.StartNew();

        System.Threading.Tasks.Parallel.ForEach(
            MyCubeGridGroups.Static.Physical.Groups,
            group =>
            {
                var concealGroup = new ConcealGroup(group);

                if (distanceFromPlayers != 0)
                {
                    var volume = GetGroupWorldAabb(group);
                    if (playerSpheres.Any(s => s.Contains(volume) != ContainmentType.Disjoint))
                        return;
                }

                if (!IsExcluded(concealGroup))
                    groups.Add(concealGroup);
            }
        );

        log.Debug("Scanned grids in {0}ms.", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();

        var removeUpdatingComponents = GetRemoveUpdatingComponentsAction(MySession.Static);

        foreach (var group in groups)
            concealed += ConcealGroup(group, removeUpdatingComponents, distanceFromPlayers);

        log.Debug("Concealed grids in {0}ms.", stopwatch.ElapsedMilliseconds);

        var concealedCount = ConcealedGroups.SelectMany(x => x.Grids).Count();
        var totalCount = MyEntities.GetEntities().Count(x => x is MyCubeGrid);

        if (concealed > 0 && totalCount > 0)
            log.Info(
                "{0}/{1} grids are concealed ({2:P}), {3} new.",
                concealedCount,
                totalCount,
                concealedCount / (float)totalCount,
                concealed
            );

        return concealed;
    }

    public int RevealGrids(double distanceFromPlayers)
    {
        if (stopped) return 0;
        var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();
        var revealed = 0;

        UpdateMovedGroups();

        foreach (var sphere in GetPlayerViewSpheres(distanceFromPlayers))
            revealed += RevealGridsInSphere(sphere, componentUpdater);

        if (configChanged)
        {
            for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                if (IsExcluded(ConcealedGroups[i]))
                    revealed += RevealGroup(ConcealedGroups[i], componentUpdater);

            configChanged = false;
        }

        if (revealed != 0)
            log.Info("Revealed {0} grids near players.", revealed);

        return revealed;
    }

    public int RevealGridsInSphere(BoundingSphereD sphere)
    {
        if (stopped) return 0;
        UpdateMovedGroups();
        return RevealGridsInSphere(
            sphere,
            MySession.Static?.GetComponent<MyEntityComponentUpdater>()
        );
    }

    // Concealment switches projectors off while their grid is hidden. A world saved
    // meanwhile saves them switched on, or they stay off after a restart.
    private static void PostfixProjectorObjectBuilder(
        MyProjectorBase __instance,
        MyObjectBuilder_CubeBlock __result
    )
    {
        var manager = ConcealmentRuntime.Instance?.Manager;
        if (
            manager != null
            && __result is MyObjectBuilder_FunctionalBlock builder
            && manager.ConcealedGroups.Any(g => g.SwitchedOffProjector(__instance.EntityId))
        )
        {
            builder.Enabled = true;
        }
    }

    public int RevealAll()
    {
        if (stopped) return 0;
        log.Debug("Revealing all grids");

        var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();
        var revealed = 0;

        for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
            revealed += RevealGroup(ConcealedGroups[i], componentUpdater);

        return revealed;
    }

    public bool IsExcluded(ConcealGroup group)
    {
#if MAGNETAR
        if (Accounting.Blocks(group)) return true;
#endif
        var pirateId = MyPirateAntennas.GetPiratesId();
        var keepActiveProductionRevealed =
            Config.ProductionConcealment == ProductionConcealmentMode.Off;

        foreach (var grid in group.Grids)
        {
            lock (keepAliveTimers)
            {
                if (keepAliveTimers.ContainsKey(grid.EntityId))
                {
                    log.Debug("{0} is kept alive by remote-control action", group.GridNames);
                    return true;
                }
            }

            if (!Config.ConcealPirates && grid.BigOwners.Contains(pirateId))
            {
                log.Debug("{0} is kept alive by pirate ownership", group.GridNames);
                return true;
            }
        }

        var excludedSubtypes = Config.ExcludedSubtypes;
        var exclude = false;

        System.Threading.Tasks.Parallel.ForEach(
            group.Grids,
            (grid, loopState) =>
            {
                foreach (var block in grid.GetFatBlocks())
                {
                    if (
                        block is MyRefinery refinery
                        && keepActiveProductionRevealed
                        && !refinery.InputInventory.Empty()
                        && refinery.IsFunctional
                        && refinery.Enabled
                    )
                    {
                        log.Debug(
                            "{0} exempted refinery ({1} active)",
                            group.GridNames,
                            refinery.CustomName
                        );
                        exclude = true;
                        loopState.Stop();
                        return;
                    }

                    if (
                        block is MyProductionBlock production
                        && keepActiveProductionRevealed
                        && production.IsProducing
                    )
                    {
                        log.Debug(
                            "{0} exempted production ({1} active)",
                            group.GridNames,
                            production.CustomName
                        );
                        exclude = true;
                        loopState.Stop();
                        return;
                    }

                    var subtype = block.BlockDefinition.Id.SubtypeName;
                    if (excludedSubtypes != null && excludedSubtypes.Contains(subtype))
                    {
                        log.Debug("{0} exempted subtype {1}", group.GridNames, subtype);
                        exclude = true;
                        loopState.Stop();
                        return;
                    }
                }
            }
        );

        return exclude;
    }

    internal void StopScheduling()
    {
        if (stopped) return;
        stopped = true;
        Config.PropertyChanged -= ConfigOnPropertyChanged;
#if MAGNETAR
        pendingConceals.Clear();
        Accounting.Stop();
#endif
        if (session != null && onSavingCheckpoint != null)
            typeof(MySession).GetEvent("OnSavingCheckpoint").RemoveEventHandler(session, onSavingCheckpoint);
        if (multiplayer != null)
            typeof(MyMultiplayerBase).GetEvent("ClientJoined").RemoveEventHandler(multiplayer, (Action<ulong, string>)RevealCryoPod);
        foreach (var group in ConcealedGroups) group.Closing -= GroupOnClosing;
        lock (keepAliveTimers)
        {
            foreach (var timer in keepAliveTimers.Values) timer.Dispose();
            keepAliveTimers.Clear();
        }
        if (keepAliveAction != null && MyAPIGateway.TerminalControls != null)
            MyAPIGateway.TerminalControls.RemoveAction<IMyRemoteControl>(keepAliveAction);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopScheduling();
        Dynamic.Dispose();
#if MAGNETAR
        Accounting.Dispose();
#endif
        Production.Dispose();
    }

    private void InitializeSessionFeatures()
    {
        if (initialized || MyAPIGateway.TerminalControls == null)
            return;

        session = MySession.Static;
        readyTick = plugin.Tick + 60 * 30;

        // Every plugin has loaded by the time a session runs
        gridBackups = GridBackups.Find(log);
        if (gridBackups != null)
            log.Info("Grid Backups found, it backs up each grid group before it is concealed");

        if (Config.RemoteControlKeepAliveAction)
        {
            keepAliveAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>(
                "Concealment.KeepAlive"
            );
            keepAliveAction.Name = new StringBuilder("Concealment keep alive");
            keepAliveAction.Action = KeepAlive;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(keepAliveAction);
        }

        multiplayer = MyMultiplayer.Static;
        if (multiplayer != null)
            typeof(MyMultiplayerBase).GetEvent("ClientJoined").AddEventHandler(multiplayer, (Action<ulong, string>)RevealCryoPod);

        if (MySession.Static != null)
        {
#if MAGNETAR
            onSavingCheckpoint = _ => Accounting.SaveCache();
#else
            onSavingCheckpoint = _ => Production.SaveNow();
#endif
            typeof(MySession).GetEvent("OnSavingCheckpoint").AddEventHandler(MySession.Static, onSavingCheckpoint);
        }

        initialized = true;
        Dynamic.Refresh();
    }

    private void ConfigOnPropertyChanged(
        object sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (stopped) return;
        configChanged = true;
#if MAGNETAR
        if (e.PropertyName == nameof(PluginConfig.Enabled) && !Config.Enabled) pendingConceals.Clear();
        if (e.PropertyName == nameof(PluginConfig.ProductionConcealment)) { modeChanged = true; pendingConceals.Clear(); Dynamic.Refresh(); }
#endif

        if (
            e.PropertyName == nameof(PluginConfig.DynamicConcealment)
            || e.PropertyName == nameof(PluginConfig.DynamicConcealQueryInterval)
            || e.PropertyName == nameof(PluginConfig.DynamicConcealScanInterval)
        )
        {
            Dynamic.Refresh();
        }
    }

    private int ConcealGroup(ConcealGroup group, Action<MyEntity> removeUpdatingComponents, double distance)
    {
        if (ConcealedGroups.Any(g => g.Id == group.Id))
            return 0;

        log.Debug("Concealing grids: {0}", group.GridNames);

#if MAGNETAR
        if (!Accounting.TryBegin(group, () => gridBackups?.BackUp(group)))
        {
            pendingConceals[group.Id] = (group, distance);
            return 0;
        }
        pendingConceals.Remove(group.Id);
        Dynamic.GuardConcealedUpdates(group.Grids.Select(g => g.GetType())
            .Concat(group.Grids.SelectMany(g => g.GetFatBlocks()).Select(b => b.GetType())));
#else
        gridBackups?.BackUp(group);
        CacheProductionCatchupGrids(group);
        group.ConcealedAtTick = plugin.Tick;
#endif
        group.Conceal(removeUpdatingComponents);
        group.UpdateAabb();
        var aabb = group.WorldAabb;
        group.ProxyId = concealedAabbTree.AddProxy(ref aabb, group, 0);
        group.Closing += GroupOnClosing;
        group.UpdatePostConceal();
        ConcealedGroups.Add(group);
#if MAGNETAR
        Accounting.Frozen(group);
#endif

        return group.Grids.Count;
    }

    private void GroupOnClosing(ConcealGroup group)
    {
        if (stopped) return;
        RevealGroup(group);
    }

    private int RevealGroup(ConcealGroup group)
    {
        return RevealGroup(group, MySession.Static?.GetComponent<MyEntityComponentUpdater>());
    }

    private int RevealGroup(ConcealGroup group, MyEntityComponentUpdater componentUpdater)
    {
        if (!group.IsConcealed)
        {
            log.Warning("Attempted to reveal a group that was not concealed: {0}", group.GridNames);
            return 0;
        }

        var count = group.Grids.Count;
        log.Debug("Revealing grids: {0}", group.GridNames);

#if MAGNETAR
        if (!Accounting.Settle(group)) return 0;
#endif
        group.Reveal(componentUpdater);
#if !MAGNETAR
        GrantProductionBoost(group);
#endif
        ConcealedGroups.Remove(group);

        if (group.ProxyId >= 0)
            concealedAabbTree.RemoveProxy(group.ProxyId);

        group.UpdatePostReveal();
        return count;
    }

    // Concealed grids keep their physics, so a grid concealed while moving drifts
    // away from the box it was concealed with. Keeps the boxes of concealed
    // ships where their grids are.
    private void UpdateMovedGroups()
    {
        foreach (var group in ConcealedGroups)
        {
            if (group.ProxyId < 0 || group.Grids.All(g => g.IsStatic))
                continue;

            group.UpdateAabb();
            var aabb = group.WorldAabb;
            concealedAabbTree.MoveProxy(group.ProxyId, ref aabb, Vector3D.Zero);
        }
    }

    private int RevealGridsInSphere(
        BoundingSphereD sphere,
        MyEntityComponentUpdater componentUpdater
    )
    {
        var revealed = 0;
        concealedAabbTree.OverlapAllBoundingSphere(ref sphere, intersectGroups);

        foreach (var group in intersectGroups.ToArray())
            revealed += RevealGroup(group, componentUpdater);

        intersectGroups.Clear();
        return revealed;
    }

    private void RevealCryoPod(ulong steamId, string username)
    {
        if (stopped) return;
        try
        {
            var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();

            for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
            {
                var group = ConcealedGroups[i];
                if (group.IsCryoOccupied(steamId))
                {
                    RevealGroup(group, componentUpdater);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reveal cryopod failed");
        }
    }

    private void KeepAlive(IMyTerminalBlock block)
    {
        if (stopped) return;
        if (block?.CubeGrid == null || block.CubeGrid.IsStatic)
            return;

        log.Debug("Keepalive triggered on grid {0}", block.CubeGrid.DisplayName);
        var dueTime = TimeSpan.FromSeconds(Math.Max(1, Config.ConcealInterval) / 60d);

        lock (keepAliveTimers)
        {
            if (keepAliveTimers.TryGetValue(block.CubeGrid.EntityId, out var timer))
            {
                timer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
            else
            {
                keepAliveTimers.Add(
                    block.CubeGrid.EntityId,
                    new Timer(
                        KeepAliveCallback,
                        block.CubeGrid.EntityId,
                        dueTime,
                        Timeout.InfiniteTimeSpan
                    )
                );
            }
        }
    }

    private void KeepAliveCallback(object state)
    {
        var gridId = (long)state;
        log.Debug("Keepalive expired on grid {0}", gridId);

        lock (keepAliveTimers)
        {
            if (!keepAliveTimers.TryGetValue(gridId, out var timer))
                return;

            timer.Dispose();
            keepAliveTimers.Remove(gridId);
        }
    }

    private List<BoundingSphereD> GetPlayerViewSpheres(double distance)
    {
        var players = new List<IMyPlayer>();
        MyAPIGateway.Players.GetPlayers(players, p => p != null && !p.IsBot);
        return players.Select(p => new BoundingSphereD(p.GetPosition(), distance)).ToList();
    }

#if MAGNETAR
    internal void RestoreConcealed(ConcealGroup group)
    {
        Dynamic.GuardConcealedUpdates(group.Grids.Select(g => g.GetType())
            .Concat(group.Grids.SelectMany(g => g.GetFatBlocks()).Select(b => b.GetType())));
        group.Conceal(GetRemoveUpdatingComponentsAction(MySession.Static));
        group.UpdateAabb();
        var aabb = group.WorldAabb;
        group.ProxyId = concealedAabbTree.AddProxy(ref aabb, group, 0);
        group.Closing += GroupOnClosing;
        group.UpdatePostConceal();
        ConcealedGroups.Add(group);
    }

    private void AdvancePending()
    {
        foreach (var pending in pendingConceals.Values.ToArray())
        {
            var group = pending.Group;
            var physical = MyCubeGridGroups.Static.Physical.GetGroup(group.Grids[0]);
            if (!Accounting.Enabled || physical == null || !physical.Nodes.Select(n => n.NodeData.EntityId).Order().SequenceEqual(group.Grids.Select(g => g.EntityId).Order())
                || group.Grids.Any(g => g.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(g.EntityId), g)) || IsExcluded(group)
                || pending.Distance != 0 && GetPlayerViewSpheres(pending.Distance).Any(s => s.Contains(GetGroupWorldAabb(physical)) != ContainmentType.Disjoint))
            { pendingConceals.Remove(group.Id); Accounting.Cancel(group); continue; }
            ConcealGroup(group, GetRemoveUpdatingComponentsAction(MySession.Static), pending.Distance);
        }
    }

#else
    private void CacheProductionCatchupGrids(ConcealGroup group)
    {
        group.ProductionCatchupGridIds.Clear();

        if (Config.ProductionConcealment != ProductionConcealmentMode.Approximate)
            return;

        foreach (var grid in group.Grids)
            if (grid.GetFatBlocks().OfType<MyProductionBlock>().Any(IsProductionActiveForCatchup))
                group.ProductionCatchupGridIds.Add(grid.EntityId);
    }

    private static bool IsProductionActiveForCatchup(MyProductionBlock block)
    {
        if (block == null || !block.IsFunctional || !block.Enabled)
            return false;

        if (block.IsProducing)
            return true;

        return block is MyRefinery refinery && !refinery.InputInventory.Empty();
    }

    private void GrantProductionBoost(ConcealGroup group)
    {
        if (
            Config.ProductionConcealment != ProductionConcealmentMode.Approximate
            || group.ProductionCatchupGridIds.Count == 0
        )
        {
            return;
        }

        var concealedFrames = Math.Max(0, plugin.Tick - group.ConcealedAtTick);
        if (concealedFrames == 0)
            return;

        var count = 0;
        foreach (var grid in group.Grids)
        {
            if (!group.ProductionCatchupGridIds.Contains(grid.EntityId))
                continue;

            Production.GrantBoost(grid, concealedFrames);
            count++;
        }

        if (count > 0)
            log.Info(
                "Banked {0} ticks of production catch-up for {1} grids in {2}.",
                concealedFrames,
                count,
                group.GridNames
            );
    }

#endif

    private static BoundingBoxD GetGroupWorldAabb(
        MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group
    )
    {
        var first = true;
        var box = default(BoundingBoxD);

        foreach (var node in group.Nodes)
        {
            var gridBox = node.NodeData.PositionComp.WorldAABB;
            if (first)
            {
                box = gridBox;
                first = false;
            }
            else
            {
                box.Include(gridBox);
            }
        }

        return box;
    }

    private Action<MyEntity> GetRemoveUpdatingComponentsAction(MySession session)
    {
        if (session == null || OnEntityClosingMethod == null)
        {
            onEntityClosingAction = null;
            return null;
        }

        var componentUpdater = session.GetComponent<MyEntityComponentUpdater>();

        if (
            componentUpdater != null
            && (onEntityClosingAction == null || onEntityClosingAction.Target != componentUpdater)
        )
            onEntityClosingAction =
                (Action<MyEntity>)
                    OnEntityClosingMethod.CreateDelegate(
                        typeof(Action<MyEntity>),
                        componentUpdater
                    );

        return onEntityClosingAction;
    }
}
