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
}
