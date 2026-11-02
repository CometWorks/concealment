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
    internal sealed record Prepared(Action[] Write, Action[] Notify, Action[] Advance);
    internal static Prepared Prepare(MyCubeGrid[] grids, Dictionary<long, MyObjectBuilder_EntityBase> builders, OfflineProfile[] models, bool restore = false, Dictionary<long, MyObjectBuilder_EntityBase> stage = null)
    {
        var blocks = grids.SelectMany(g => g.GetFatBlocks()).ToDictionary(b => b.EntityId);
        var saved = builders.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var staged = stage?.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var advance = new List<Action>();
        var write = new List<Action>(); var notify = new List<Action>();
        void Add(long block, Action action, Action<MyObjectBuilder_CubeBlock> next)
        {
            write.Add(action);
            advance.Add(() => { if (staged != null) next(staged[block]); });
        }
        foreach (var profile in models)
        {
            foreach (var inventory in profile.Inventories)
            {
                var block = blocks[inventory.Block];
                var live = block.GetInventory(inventory.Index) ?? throw new InvalidDataException("Live inventory is missing");
                var candidates = AccountingEngine.Inventories(saved[inventory.Block]).ToArray();
                var ob = candidates.FirstOrDefault(i => i.InventoryId == inventory.InventoryId) ?? candidates[inventory.Index];
                if (!restore && AccountingStock.Xml(live.GetObjectBuilder()) == AccountingStock.Xml(ob)) continue;
                var items = ob.Items.Select(i => new MyPhysicalInventoryItem { ItemId = i.ItemId, Amount = i.Amount, Scale = i.Scale, Content = i.PhysicalContent }).ToList();
                var ids = items.Select(i => i.ItemId).ToHashSet();
                if (ids.Count != items.Count) throw new InvalidDataException("Duplicate modeled item identity");
                uint next = ob.nextItemId;
                var mass = live.m_currentMass.Value; var volume = live.m_currentVolume.Value;
                Add(inventory.Block, () =>
                {
                    live.m_items = items; live.m_usedIds = ids; live.m_nextItemID = next;
                    // Rollback must not rerun the notification that invalidated the
                    // transaction. Restore its captured derived values silently.
                    if (restore) { live.m_currentMass.m_value = mass; live.m_currentVolume.m_value = volume; }
                }, block =>
                {
                    var inventories = AccountingEngine.Inventories(block).ToArray();
                    var target = inventories.FirstOrDefault(i => i.InventoryId == inventory.InventoryId) ?? inventories[inventory.Index];
                    target.Items = ob.Items;
                });
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
                        bool full = restore ? battery.m_isFull.Value : store.Full;
                        int lastUpdate = restore ? battery.m_lastUpdateTime : MySession.Static.GameplayFrameCounter - 1 - (int)store.ElapsedFrames;
                        Add(store.Block, () => { battery.CurrentStoredPower = energy; battery.m_isFull.Value = full; battery.m_lastUpdateTime = lastUpdate; if (restore) battery.m_storedPower.m_value = energy; }, b => ((MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = energy);
                        notify.Add(() => battery.m_storedPower.Value = energy);
                        break;
                    case "Tank":
                        var tank = (MyGasTank)block; float ratio = ((MyObjectBuilder_GasTank)saved[store.Block]).FilledRatio;
                        Add(store.Block, () => tank.m_filledRatio = ratio, b => ((MyObjectBuilder_GasTank)b).FilledRatio = ratio);
                        notify.Add(() => ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(ratio, true));
                        break;
                    case "Engine":
                        var engine = (MyGasFueledPowerProducer)block; float capacity = ((MyObjectBuilder_FueledPowerProducer)saved[store.Block]).Capacity;
                        Add(store.Block, () => engine.Capacity = capacity, b => ((MyObjectBuilder_FueledPowerProducer)b).Capacity = capacity); break;
                    case "Jump":
                        var jump = (MyJumpDrive)block; float stored = ((MyObjectBuilder_JumpDrive)saved[store.Block]).StoredPower;
                        Add(store.Block, () => jump.m_storedPower.SetLocalValue(stored), b => ((MyObjectBuilder_JumpDrive)b).StoredPower = stored);
                        notify.Add(() => ((Sandbox.ModAPI.IMyJumpDrive)jump).CurrentStoredPower = stored); break;
                }
            }
            foreach (var source in profile.Sources.Where(s => s.FuelTickFrames > 0))
            {
                var reactor = (MyReactor)blocks[source.Block];
                if (reactor.m_timer == null) throw new InvalidDataException("Live fuel timer missing");
                uint elapsed = restore ? reactor.m_timer.FramesFromLastTrigger : checked((uint)(Math.Floor(source.FuelElapsedFrames / 100) * 100));
                uint period = restore ? reactor.m_timer.TimerTickInFrames : checked((uint)source.FuelTickFrames);
                // Native inventory notification derives reactor capacity from fuel.
                // Prepare that same cache value so validation remains exact both
                // before and after notifications; it is not independent stored fuel.
                var builder = (MyObjectBuilder_Reactor)saved[source.Block];
                float capacity = builder.Capacity;
                if (!restore)
                {
                    capacity = float.MaxValue;
                    var inventory = AccountingEngine.Inventories(builder).First();
                    foreach (var fuel in reactor.BlockDefinition.FuelInfos)
                    {
                        var amount = inventory.Items.Where(i => i.PhysicalContent.GetId() == fuel.FuelId)
                            .Aggregate((VRage.MyFixedPoint)0, (sum, item) => sum + item.Amount);
                        capacity = Math.Min(capacity, (float)amount / fuel.Ratio);
                    }
                    if (capacity == 0 && reactor.IsCreativeModeEnabled)
                    {
                        var fuel = reactor.BlockDefinition.FuelInfos[0];
                        capacity = fuel.FuelDefinition.Mass / fuel.Ratio;
                    }
                    builder.Capacity = capacity;
                }
                Add(source.Block, () => { reactor.m_timer.FramesFromLastTrigger = elapsed; reactor.m_timer.TimerTickInFrames = period; reactor.Capacity = capacity; }, b => ((MyObjectBuilder_Reactor)b).Capacity = capacity);
            }
            foreach (var production in profile.Production)
            {
                if (restore)
                {
                    var native = (MyProductionBlock)blocks[production.Block];
                    var originalQueue = native.m_queue.ToList(); var originalItem = native.m_currentQueueItem; uint originalNext = native.m_nextItemId;
                    var timer = native.m_timer; uint? elapsed = timer?.FramesFromLastTrigger, period = timer?.TimerTickInFrames;
                    bool rebuild = native is MyRefinery refinery && refinery.m_queueNeedsRebuild;
                    float progress = native is MyAssembler current ? current.CurrentProgress : 0;
                    int index = native is MyAssembler indexed ? indexed.CurrentItemIndex : 0;
                    write.Add(() =>
                    {
                        native.m_queue = originalQueue; native.m_currentQueueItem = originalItem; native.m_nextItemId = originalNext;
                        if (timer != null) { timer.FramesFromLastTrigger = elapsed.Value; timer.TimerTickInFrames = period.Value; }
                        if (native is MyRefinery refinery) refinery.m_queueNeedsRebuild = rebuild;
                        if (native is MyAssembler assembler) { assembler.CurrentItemIndex = index; assembler.CurrentProgress = progress; }
                    });
                    continue;
                }
                if (production.Refinery) continue;
                var assembler = (MyAssembler)blocks[production.Block];
                var ob = (MyObjectBuilder_Assembler)saved[production.Block];
                var queue = (ob.Queue ?? []).Select(q => new MyProductionBlock.QueueItem { Blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(q.Id), Amount = q.Amount, ItemId = q.ItemId ?? 0 }).ToList();
                if (queue.Any(q => q.Blueprint == null)) throw new InvalidDataException("Modeled blueprint disappeared");
                Add(production.Block, () => { assembler.m_queue = queue; assembler.m_nextItemId = ob.NextItemId; assembler.m_currentQueueItem = null; assembler.CurrentItemIndex = 0; assembler.CurrentProgress = ob.CurrentProgress; }, b =>
                {
                    var target = (MyObjectBuilder_Assembler)b;
                    target.Queue = ob.Queue; target.CurrentProgress = ob.CurrentProgress;
                });
            }
            foreach (var upkeep in profile.Upkeep)
            {
                var zone = (MySafeZoneBlock)blocks[upkeep.Block];
                var manager = zone.Components.Get<MySafeZoneComponent>() ?? throw new InvalidDataException("Live zone component missing");
                var component = saved[upkeep.Block].ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single();
                // The bubble was removed while concealed. Restore paid credit/settings;
                // native working/allocation and activation own any future protection.
                bool activating = manager.m_activating; var remaining = manager.m_timeLeft;
                Add(upkeep.Block, () =>
                {
                    if (restore) manager.SafeZoneRemove_Server();
                    manager.Deserialize(component);
                    if (restore) { manager.m_activating = activating; manager.m_timeLeft = remaining; }
                }, b => b.ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single().SafeZoneOb = component.SafeZoneOb);
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
        return new(write.ToArray(), notify.ToArray(), advance.ToArray());
    }
}
