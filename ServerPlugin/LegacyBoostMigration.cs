using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Shared.Config;
using Shared.Logging;
using VRage.FileSystem;
using VRage.Game.Entity;

namespace Shared.Concealment;

// Drain-only compatibility for pre-extraction ONLINE boost frames. No new interval
// grants a bank. Native production and its configured power multiplier remain the
// old path until this credit is spent; the whole accounting group is excluded from
// new compensation meanwhile. Remaining credit travels with native stock builders.
public sealed partial class LegacyBoostMigration : IDisposable
{
    // 30 seconds at 60 simulation frames per second.
    private const long CheckIntervalFrames = 30 * 60;

    private const string BanksFileName = "ConcealmentBanks.jsonl";

    private static readonly WeakReference<ConcealmentRuntime> PluginRef = new WeakReference<ConcealmentRuntime>(null);

    // Banked online boost time per grid, keyed by grid EntityId, measured in
    // simulation frames (60 per second, same clock as ConcealmentRuntime.Tick).
    private static readonly ConcurrentDictionary<long, double> BoostBankFrames =
        new ConcurrentDictionary<long, double>();

    // Latest power-gate decision per grid: true while the grid can supply the boosted
    // draw. Absent or false means produce at normal speed and do not spend the bank.
    private static readonly ConcurrentDictionary<long, bool> CanBoostGrid =
        new ConcurrentDictionary<long, bool>();

    // Last tick the grid's bank was drained, so consumption tracks real elapsed time
    // regardless of how many blocks the grid has or when each one updates.
    private static readonly ConcurrentDictionary<long, long> LastConsumeTick =
        new ConcurrentDictionary<long, long>();

    private static readonly MethodInfo PrefixUpdateProductionMethod =
        typeof(LegacyBoostMigration).GetMethod(
            nameof(PrefixUpdateProduction),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static readonly MethodInfo PostfixUpdateProductionMethod =
        typeof(LegacyBoostMigration).GetMethod(
            nameof(PostfixUpdateProduction),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private static readonly MethodInfo PostfixPowerMethod =
        typeof(LegacyBoostMigration).GetMethod(
            nameof(PostfixPowerConsumption),
            BindingFlags.Static | BindingFlags.NonPublic
        );

    private readonly Harmony harmony;
    private readonly IPluginLogger log;
    private readonly string banksPath;
    private readonly HashSet<MethodBase> prefixed = new HashSet<MethodBase>();
    private readonly HashSet<MethodBase> postfixed = new HashSet<MethodBase>();
    private bool disposed;

    public LegacyBoostMigration(ConcealmentRuntime plugin, Harmony harmony)
    {
        if (ConcealmentRuntime.ClusterMode)
            throw new InvalidOperationException("Cluster owns grid lifecycle.");

        this.harmony = harmony;
        log = plugin.Log;
        PluginRef.SetTarget(plugin);
        banksPath = Path.Combine(plugin.DataDirectory, BanksFileName);

        // Import is deferred until the complete world and its saved grid receipts are available.

        PatchBlock(typeof(MyRefinery));
        PatchBlock(typeof(MyAssembler));
    }

    // Called by the standalone manager only; native world builders own persistence.
    public void Update(long tick)
    {
        if (disposed)
            return;

        InitializeCutover();
        if (Enabled && !BoostBankFrames.IsEmpty)
        {
            foreach (var entry in BoostBankFrames)
            {
                if (entry.Value <= 0.0)
                    continue;

                // Stagger: each grid is offset by a stable phase derived from its id, so
                // the 30 second checks spread out instead of bunching on the same tick.
                var offset = (int)(
                    ((entry.Key % CheckIntervalFrames) + CheckIntervalFrames) % CheckIntervalFrames
                );
                if ((tick + offset) % CheckIntervalFrames != 0)
                    continue;

                if (
                    MyEntities.TryGetEntityById(entry.Key, out MyEntity entity)
                    && entity is MyCubeGrid grid
                )
                    EvaluateGate(grid);
                else
                    CanBoostGrid[entry.Key] = false;
            }
        }

    }

    public void Dispose()
    {
        disposed = true;
        foreach (var method in prefixed) { harmony.Unpatch(method, PrefixUpdateProductionMethod); harmony.Unpatch(method, PostfixUpdateProductionMethod); }
        foreach (var method in postfixed) { harmony.Unpatch(method, PostfixUpdateProductionMethod); harmony.Unpatch(method, PostfixPowerMethod); }
        prefixed.Clear(); postfixed.Clear();
        foreach (var grid in trackedGrids.Values) { grid.OnGridMerge -= Merged; grid.OnGridSplit -= Split; }
        trackedGrids.Clear();
        BoostBankFrames.Clear(); CanBoostGrid.Clear(); LastConsumeTick.Clear(); PluginRef.SetTarget(null);
    }

    private void PatchBlock(Type blockType)
    {
        var updateProduction = blockType.GetMethod(
            "UpdateProduction",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (updateProduction != null && prefixed.Add(updateProduction))
        {
            harmony.Patch(
                updateProduction,
                prefix: new HarmonyMethod(PrefixUpdateProductionMethod),
                postfix: new HarmonyMethod(PostfixUpdateProductionMethod)
            );
        }
        else
        {
            log.Warning("ProductionBoost: UpdateProduction not found on {0}", blockType);
        }

        var power = blockType.GetMethod(
            "GetOperationalPowerConsumption",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (power != null && postfixed.Add(power))
            harmony.Patch(power, postfix: new HarmonyMethod(PostfixPowerMethod));
        else
            log.Warning(
                "ProductionBoost: GetOperationalPowerConsumption not found on {0}",
                blockType
            );
    }

    private static bool Enabled
    {
        get
        {
            return ConcealmentRuntime.StandaloneActive && PluginRef.TryGetTarget(out var plugin)
                && plugin.ConfigData.ProductionConcealment == ProductionConcealmentMode.Approximate;
        }
    }

    private static long CurrentTick => PluginRef.TryGetTarget(out var plugin) ? plugin.Tick : 0;

    private static double BoostLevel =>
        PluginRef.TryGetTarget(out var plugin)
            ? Math.Max(1.0, plugin.ConfigData.ProductionBoostLevel)
            : 1.0;

    // Decide whether a grid can currently support the boosted power draw. Hysteresis is
    // built in: a grid that is already boosting keeps boosting while supply meets the
    // (already boosted) demand; a grid that is not boosting only starts once there is
    // headroom for the full extra draw. That dead band keeps the decision from flapping.
    private static void EvaluateGate(MyCubeGrid grid)
    {
        var gridId = grid.EntityId;
        var distributor = grid.GridSystems?.ResourceDistributor;
        if (distributor == null)
        {
            CanBoostGrid[gridId] = false;
            return;
        }

        var electricity = MyResourceDistributorComponent.ElectricityId;
        var maxAvailable = distributor.MaxAvailableResourceByType(electricity, grid);
        var required = distributor.TotalRequiredInputByType(electricity, grid);

        double baseConsumption = 0.0;
        foreach (var block in grid.GetFatBlocks().OfType<MyProductionBlock>())
        {
            if (
                block.IsProducing && block.BlockDefinition is MyProductionBlockDefinition definition
            )
                baseConsumption += definition.OperationalPowerConsumption;
        }

        var extra = (BoostLevel - 1.0) * baseConsumption;
        var wasBoosting = CanBoostGrid.TryGetValue(gridId, out var previous) && previous;

        // While boosting the boosted draw is already part of 'required', so we only need
        // supply to keep meeting it. While not boosting we must fit the extra on top.
        CanBoostGrid[gridId] = wasBoosting
            ? maxAvailable >= required
            : maxAvailable >= required + extra;
    }

    // Boost multiplier currently applied to a block: the configured level while its grid
    // has banked time and the grid can supply the boosted power, otherwise 1.0 (normal,
    // unmodified production).
    private static double GetBoostMultiplier(MyProductionBlock block)
    {
        if (block == null || !Enabled)
            return 1.0;

        var grid = block.CubeGrid;
        if (grid == null)
            return 1.0;

        var gridId = grid.EntityId;
        if (!BoostBankFrames.TryGetValue(gridId, out var bank) || bank <= 0.0)
            return 1.0;

        if (!PluginRef.TryGetTarget(out var plugin) || !plugin.Manager.Production.ValidGroup(grid))
            return 1.0;

        if (!CanBoostGrid.TryGetValue(gridId, out var canBoost) || !canBoost)
            return 1.0;

        return BoostLevel;
    }

    // Bigger steps: scale the frame budget so the block processes boost level times
    // more material this update. The bank is read (not consumed) here so the same
    // multiplier stays consistent with the power patch within one update.
    private static void PrefixUpdateProduction(
        MyProductionBlock __instance,
        ref uint framesFromLastTrigger,
        out BoostState __state
    )
    {
        var multiplier = GetBoostMultiplier(__instance);
        __state = new BoostState
        {
            OriginalFrames = framesFromLastTrigger,
            Multiplier = multiplier,
        };

        if (multiplier <= 1.0)
            return;

        var boosted = framesFromLastTrigger * multiplier;
        framesFromLastTrigger = boosted >= uint.MaxValue ? uint.MaxValue : (uint)boosted;
    }

    // Consume the grid's banked boost time by the real frames elapsed since the bank was
    // last drained, clamped to the frames this update actually processed. The clamp keeps
    // a grid that resumes boosting after a power-starved gap from draining the whole gap,
    // and makes the per-grid bank drain at real time no matter how many blocks it has.
    private static void PostfixUpdateProduction(MyProductionBlock __instance, BoostState __state)
    {
        if (__state.Multiplier <= 1.0 || __instance == null || !__instance.IsProducing)
            return;

        var grid = __instance.CubeGrid;
        if (grid == null)
            return;

        var gridId = grid.EntityId;
        if (!BoostBankFrames.TryGetValue(gridId, out var bank))
            return;

        var now = CurrentTick;
        var last = LastConsumeTick.GetOrAdd(gridId, now);
        var elapsed = now - last;
        if (elapsed <= 0)
            return;

        var drain = Math.Min(elapsed, __state.OriginalFrames);
        if (drain <= 0)
            return;

        LastConsumeTick[gridId] = now;

        var remaining = bank - drain;
        if (remaining <= 0.0)
        {
            BoostBankFrames.TryRemove(gridId, out _);
            LastConsumeTick.TryRemove(gridId, out _);
            CanBoostGrid.TryRemove(gridId, out _);
        }
        else
        {
            BoostBankFrames[gridId] = remaining;
        }

    }

    // Power draw scales with the boost so catch-up is not free. The power gate decides
    // whether the grid can afford this, so here we only need to mirror the multiplier.
    private static void PostfixPowerConsumption(MyProductionBlock __instance, ref float __result)
    {
        var multiplier = GetBoostMultiplier(__instance);
        if (multiplier > 1.0)
            __result *= (float)multiplier;
    }

    private struct BoostState
    {
        public uint OriginalFrames;
        public double Multiplier;
    }
}
