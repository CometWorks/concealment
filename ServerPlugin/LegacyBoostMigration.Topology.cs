using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Game.Entities;

namespace Shared.Concealment;

public sealed partial class LegacyBoostMigration
{
    private readonly ConcurrentDictionary<long, MyCubeGrid> trackedGrids = new();

    private static bool ValidReceipt(Receipt receipt) => double.IsFinite(receipt.RemainingFrames) && receipt.RemainingFrames >= 0
        && receipt.Retained != null && receipt.Opaque != null && receipt.Retained.Length <= 10000
        && receipt.Retained.All(r => r != null && r.Schema == 1 && r.Grid != 0 && double.IsFinite(r.RemainingFrames)
            && r.RemainingFrames >= 0 && r.Quarantined && r.Retained?.Length == 0 && r.Opaque?.Length == 0);

    // Physical and logical groups can differ (connectors). Close over both so a
    // member cannot drain old credit while another member claims a new interval.
    private static MyCubeGrid[] AccountingGroup(IEnumerable<MyCubeGrid> seeds)
    {
        var found = new Dictionary<long, MyCubeGrid>();
        var pending = new Queue<MyCubeGrid>(seeds);
        while (pending.Count > 0)
        {
            var grid = pending.Dequeue();
            if (!found.TryAdd(grid.EntityId, grid)) continue;
            var physical = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            if (physical != null) foreach (var node in physical.Nodes) pending.Enqueue(node.NodeData);
            var logical = MyCubeGridGroups.Static.Logical.GetGroup(grid);
            if (logical != null) foreach (var node in logical.Nodes) pending.Enqueue(node.NodeData);
        }
        return found.Values.OrderBy(g => g.EntityId).ToArray();
    }

    private static string Topology(MyCubeGrid grid) => AccountingStock.Hash(Encoding.UTF8.GetBytes(string.Join("|",
        AccountingGroup(new[] { grid }).Select(g => g.EntityId + ":" + string.Join(",", g.GetBlocks().OrderBy(b => b.Position.X)
            .ThenBy(b => b.Position.Y).ThenBy(b => b.Position.Z).Select(b => b.Position + ":" + b.BlockDefinition.Id + ":" + b.FatBlock?.EntityId))))));

    private void Track(MyCubeGrid grid)
    {
        if (!trackedGrids.TryAdd(grid.EntityId, grid)) return;
        grid.OnGridMerge += Merged;
        grid.OnGridSplit += Split;
    }

    private void Quarantine(MyCubeGrid grid)
    {
        if (!receipts.TryGetValue(grid.EntityId, out var receipt)) return;
        if (!receipt.Quarantined) receipt.RemainingFrames = BoostBankFrames.GetValueOrDefault(grid.EntityId);
        if (receipt.RemainingFrames <= 0 && receipt.Retained.Length == 0 && receipt.Opaque.Length == 0) return;
        receipt.Quarantined = true;
        BoostBankFrames.TryRemove(grid.EntityId, out _);
        CanBoostGrid.TryRemove(grid.EntityId, out _);
        LastConsumeTick.TryRemove(grid.EntityId, out _);
        log.Warning("Legacy credit retained without consumption after topology/identity change on {0}", grid.EntityId);
    }

    private bool ValidGroup(MyCubeGrid grid)
    {
        if (!Ready || !receipts.TryGetValue(grid.EntityId, out var receipt) || receipt.Quarantined) return false;
        if (receipt.Topology == Topology(grid)) return true;
        foreach (var member in AccountingGroup(new[] { grid })) Quarantine(member);
        return false;
    }

    private void Split(MyCubeGrid original, MyCubeGrid child)
    {
        Track(child);
        foreach (var member in AccountingGroup(new[] { original, child })) Quarantine(member);
        // The original row stays on the original grid; the child gets no grant.
    }

    private void Merged(MyCubeGrid survivor, MyCubeGrid removed)
    {
        Quarantine(survivor);
        Quarantine(removed);
        if (!receipts.TryRemove(removed.EntityId, out var previous)) previous = null;
        opaque.TryRemove(removed.EntityId, out var raw);
        if (previous == null && raw == null) return; // both native merge events may fire
        var carrier = receipts.GetOrAdd(survivor.EntityId, id => new Receipt { Grid = id, World = cutover?.World, Source = cutover?.Source });
        var retained = carrier.Retained.ToList();
        if (previous != null)
        {
            retained.AddRange(previous.Retained);
            if (previous.RemainingFrames > 0) retained.Add(new Receipt { Grid = previous.Grid, World = previous.World,
                Source = previous.Source, RemainingFrames = previous.RemainingFrames, Quarantined = true, Topology = previous.Topology });
        }
        carrier.Retained = retained.ToArray();
        carrier.Opaque = carrier.Opaque.Concat(previous?.Opaque ?? []).Concat(raw == null ? [] : new[] { raw }).ToArray();
        carrier.Quarantined |= carrier.Retained.Length > 0 || carrier.Opaque.Length > 0;
        foreach (var member in AccountingGroup(new[] { survivor })) Quarantine(member);
    }
}
