using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Concealment.Accounting;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.ObjectBuilders;

namespace Shared.Concealment;

// Prepare all lookups, allocations and validation before native writes. The host
// holds the game thread and concealed registrations until stocks AND the receipt
// are committed. Reentrant snapshots are refused by StandaloneAccounting.Saving.
internal static class AccountingStockCommit
{
    internal sealed record Prepared(Action[] Write, Action[] Notify);
    internal static Prepared Prepare(MyCubeGrid[] grids, Dictionary<long, MyObjectBuilder_EntityBase> builders, OfflineProfile[] models)
    {
        var blocks = grids.SelectMany(g => g.GetFatBlocks()).ToDictionary(b => b.EntityId);
        var saved = builders.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var write = new List<Action>(); var notify = new List<Action>();
        foreach (var profile in models)
        {
            foreach (var inventory in profile.Inventories)
            {
                var block = blocks[inventory.Block];
                var live = block.GetInventory(inventory.Index) ?? throw new InvalidDataException("Live inventory is missing");
                var candidates = AccountingEngine.Inventories(saved[inventory.Block]).ToArray();
                var ob = candidates.FirstOrDefault(i => i.InventoryId == inventory.InventoryId) ?? candidates[inventory.Index];
                if (AccountingStock.Xml(live.GetObjectBuilder()) == AccountingStock.Xml(ob)) continue;
                var items = ob.Items.Select(i => new MyPhysicalInventoryItem { ItemId = i.ItemId, Amount = i.Amount, Scale = i.Scale, Content = i.PhysicalContent }).ToList();
                var ids = items.Select(i => i.ItemId).ToHashSet();
                if (ids.Count != items.Count) throw new InvalidDataException("Duplicate modeled item identity");
                uint next = ob.nextItemId;
                write.Add(() => { live.m_items = items; live.m_usedIds = ids; live.m_nextItemID = next; });
                notify.Add(() => { live.RefreshVolumeAndMass(); live.OnContentsChanged(); });
            }
            foreach (var store in profile.Storage)
            {
                var block = blocks[store.Block];
                switch (store.Kind)
                {
                    case "Battery":
                        var battery = (MyBatteryBlock)block;
                        float energy = ((MyObjectBuilder_BatteryBlock)saved[store.Block]).CurrentStoredPower;
                        write.Add(() => { battery.CurrentStoredPower = energy; battery.m_isFull.Value = store.Full; battery.m_lastUpdateTime = MySession.Static.GameplayFrameCounter - 1 - (int)store.ElapsedFrames; });
                        notify.Add(() => battery.m_storedPower.Value = energy);
                        break;
                    case "Tank":
                        var tank = (MyGasTank)block; float ratio = ((MyObjectBuilder_GasTank)saved[store.Block]).FilledRatio;
                        write.Add(() => tank.m_filledRatio = ratio);
                        notify.Add(() => ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(ratio, true));
                        break;
                    case "Engine":
                        var engine = (MyGasFueledPowerProducer)block; float capacity = ((MyObjectBuilder_FueledPowerProducer)saved[store.Block]).Capacity;
                        write.Add(() => engine.Capacity = capacity); break;
                    case "Jump":
                        var jump = (MyJumpDrive)block; float stored = ((MyObjectBuilder_JumpDrive)saved[store.Block]).StoredPower;
                        write.Add(() => jump.m_storedPower.SetLocalValue(stored));
                        notify.Add(() => ((Sandbox.ModAPI.IMyJumpDrive)jump).CurrentStoredPower = stored); break;
                }
            }
            foreach (var source in profile.Sources.Where(s => s.FuelTickFrames > 0))
            {
                var reactor = (MyReactor)blocks[source.Block];
                if (reactor.m_timer == null) throw new InvalidDataException("Live fuel timer missing");
                uint elapsed = checked((uint)(Math.Floor(source.FuelElapsedFrames / 100) * 100));
                uint period = checked((uint)source.FuelTickFrames);
                write.Add(() => { reactor.m_timer.FramesFromLastTrigger = elapsed; reactor.m_timer.TimerTickInFrames = period; });
            }
            foreach (var production in profile.Production.Where(p => !p.Refinery))
            {
                var assembler = (MyAssembler)blocks[production.Block];
                var ob = (MyObjectBuilder_Assembler)saved[production.Block];
                var queue = (ob.Queue ?? []).Select(q => new MyProductionBlock.QueueItem { Blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(q.Id), Amount = q.Amount, ItemId = q.ItemId ?? 0 }).ToList();
                if (queue.Any(q => q.Blueprint == null)) throw new InvalidDataException("Modeled blueprint disappeared");
                write.Add(() => { assembler.m_queue = queue; assembler.m_nextItemId = ob.NextItemId; assembler.m_currentQueueItem = null; assembler.CurrentItemIndex = 0; assembler.CurrentProgress = ob.CurrentProgress; });
            }
            foreach (var upkeep in profile.Upkeep)
            {
                var zone = (MySafeZoneBlock)blocks[upkeep.Block];
                var manager = zone.Components.Get<MySafeZoneComponent>() ?? throw new InvalidDataException("Live zone component missing");
                var component = saved[upkeep.Block].ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single();
                // The bubble was removed while concealed. Restore paid credit/settings;
                // native working/allocation and activation own any future protection.
                write.Add(() => manager.Deserialize(component));
                notify.Add(() =>
                {
                    zone.ResourceSink.Update();
                    if (upkeep.Exists && upkeep.Enabled && zone.Enabled && zone.IsFunctional)
                    {
                        manager.m_activating = false;
                        manager.m_timeLeft = TimeSpan.FromMilliseconds(upkeep.RemainingFrames * (1000.0 / 60));
                        manager.SafeZoneCreate_Server();
                    }
                });
            }
        }
        return new(write.ToArray(), notify.ToArray());
    }
}
