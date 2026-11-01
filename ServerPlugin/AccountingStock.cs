using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Concealment.Accounting;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace Shared.Concealment;

internal static class AccountingStock
{
    internal static string World => MySession.Static.WorldId != Guid.Empty ? MySession.Static.WorldId.ToString("N")
        : "path:" + Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(MySession.Static.CurrentPath)));
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    internal static string Xml(MyObjectBuilder_Base builder)
    {
        using var stream = new MemoryStream();
        if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder)) throw new InvalidDataException("Accounting snapshot serialization failed");
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    internal static string Fingerprint(IEnumerable<MyObjectBuilder_CubeGrid> grids)
    {
        var text = new StringBuilder();
        foreach (var grid in grids.OrderBy(g => g.EntityId))
        {
            text.Append(grid.EntityId).Append('|');
            foreach (var block in grid.CubeBlocks.OrderBy(b => b.EntityId))
            {
                text.Append(block.EntityId).Append(':').Append(block.GetId()).Append(':').Append(block.Min).Append(':').Append(block.BlockOrientation);
                if (block is MyObjectBuilder_FunctionalBlock functional) text.Append(':').Append(functional.Enabled);
                foreach (var inventory in AccountingEngine.Inventories(block))
                {
                    text.Append('|').Append(inventory.InventoryId).Append(':').Append(inventory.InventoryFlags);
                    foreach (var item in inventory.Items)
                        text.Append('|').Append(((decimal)item.Amount).ToString("G29", CultureInfo.InvariantCulture)).Append(':').Append(item.Scale.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(Xml(item.PhysicalContent));
                }
                var zone = block.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().FirstOrDefault();
                if (zone?.SafeZoneOb != null) text.Append(':').Append(Xml(zone.SafeZoneOb));
                switch (block)
                {
                    case MyObjectBuilder_BatteryBlock battery: text.Append(':').Append(battery.CurrentStoredPower.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(battery.ChargeMode).Append(':').Append(battery.ProducerEnabled); break;
                    case MyObjectBuilder_GasTank tank: text.Append(':').Append(tank.FilledRatio.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(tank.IsStockpiling); break;
                    case MyObjectBuilder_FueledPowerProducer engine: text.Append(':').Append(engine.Capacity.ToString("R", CultureInfo.InvariantCulture)); break;
                    case MyObjectBuilder_JumpDrive jump: text.Append(':').Append(jump.StoredPower.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(jump.Recharging); break;
                }
                if (block is MyObjectBuilder_ProductionBlock production)
                    foreach (var item in production.Queue ?? []) text.Append('|').Append(item.Id).Append(':').Append(((decimal)item.Amount).ToString("G29", CultureInfo.InvariantCulture));
                if (block is MyObjectBuilder_Assembler assembler) text.Append(':').Append(assembler.CurrentProgress.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(assembler.RepeatAssembleEnabled).Append(':').Append(assembler.DisassembleEnabled);
            }
        }
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }
    internal static Dictionary<long, MyObjectBuilder_EntityBase> Builders(IEnumerable<MyCubeGrid> grids) => grids.ToDictionary(g => g.EntityId, g => g.GetObjectBuilder());

    internal static double[] Pose(VRageMath.MatrixD m) => [m.M41, m.M42, m.M43, m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33];
    internal static double[][] Poses(IEnumerable<MyCubeGrid> grids) => grids.Select(g => Pose(g.WorldMatrix)).ToArray();
    internal static bool SameLoadedPoses(MyCubeGrid[] grids, double[][] saved, double[][] loaded)
    {
        if (saved == null || saved.Length != grids.Length || loaded.Length != grids.Length) return false;
        for (int i = 0; i < grids.Length; i++)
        {
            if (SamePoses([saved[i]], [loaded[i]])) continue;
            var p = saved[i];
            if (p.Length != 12 || p.Any(v => !double.IsFinite(v)) || grids[i].Physics == null) return false;
            var matrix = new VRageMath.MatrixD(p[3], p[4], p[5], 0, p[6], p[7], p[8], 0, p[9], p[10], p[11], 0, p[0], p[1], p[2], 1);
            var physics = grids[i].Physics;
            // Match the engine's exact double -> cluster-local float -> double
            // publication, including center/offset. No positional tolerance.
            VRageMath.MatrixD native = physics.GetRigidBodyMatrix(matrix);
            native.Translation += physics.Offset;
            native.Translation -= VRageMath.Vector3D.TransformNormal(physics.Center, ref native);
            if (!SamePoses([Pose(native)], [loaded[i]])) return false;
        }
        return true;
    }
    internal static bool SamePoses(double[][] before, double[][] after) => before.Length == after.Length
        && before.Zip(after).All(p => p.First.Length == 12 && p.Second.Length == 12
            && p.First.Zip(p.Second).Select((v, i) => double.IsFinite(v.First) && double.IsFinite(v.Second)
                // Native save/load rebuilds the orthonormal basis through float Up.
                // Positions remain exact; rotation permits only that native precision.
                && Math.Abs(v.First - v.Second) <= (i < 3 ? 0 : 1e-6)).All(equal => equal));

    internal static void RestoreLoadedEngines(IEnumerable<MyCubeGrid> grids, MyObjectBuilder_CubeGrid[] saved, string stockHash)
    {
        // Native Init clamps H2 capacity, although UpdateCapacity can save one
        // paid intake step above nameplate. Restore only that exact load change,
        // after validating the complete saved AND loaded group's other stocks.
        if (Fingerprint(saved) != stockHash) throw new InvalidDataException("Saved stocks differ from accounting claim");
        var live = Builders(grids).Values.Cast<MyObjectBuilder_CubeGrid>().ToArray();
        var blocks = live.SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var engines = grids.SelectMany(g => g.GetFatBlocks()).OfType<MyGasFueledPowerProducer>().ToDictionary(e => e.EntityId);
        var restore = new List<Action>();
        foreach (var original in saved.SelectMany(g => g.CubeBlocks).OfType<MyObjectBuilder_FueledPowerProducer>())
        {
            if (!engines.TryGetValue(original.EntityId, out var engine)) continue;
            var current = (MyObjectBuilder_FueledPowerProducer)blocks[original.EntityId];
            if (original.Capacity <= engine.GasCapacity) continue;
            if (!float.IsFinite(original.Capacity) || original.Capacity - engine.GasCapacity > engine.GasCapacity * 3 / 60
                || current.Capacity != engine.GasCapacity) throw new InvalidDataException("Unexpected native engine load stock");
            current.Capacity = original.Capacity;
            restore.Add(() => engine.Capacity = original.Capacity);
        }
        if (Fingerprint(live) != stockHash) throw new InvalidDataException("Loaded stocks differ from accounting claim");
        foreach (var write in restore) write();
    }
}
