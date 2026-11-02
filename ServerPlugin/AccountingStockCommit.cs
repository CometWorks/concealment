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
    internal sealed record Prepared(Action[] Write, Action[] Notify, string[] Stages, string Result, Action Validate);
    internal static Prepared Prepare(MyCubeGrid[] grids, Dictionary<long, MyObjectBuilder_EntityBase> builders, OfflineProfile[] models, bool restore = false, Dictionary<long, MyObjectBuilder_EntityBase> stage = null)
    {
        var blocks = grids.SelectMany(g => g.GetFatBlocks()).ToDictionary(b => b.EntityId);
        var saved = builders.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var staged = stage?.Values.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
        var advance = new List<Action>(); var validate = new List<Action>();
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
                var items = ob.Items.Select(i => new MyPhysicalInventoryItem { ItemId = i.ItemId, Amount = i.Amount, Scale = i.Scale, Content = (MyObjectBuilder_PhysicalObject)i.PhysicalContent.Clone() }).ToList();
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
                    target.Items = ((MyObjectBuilder_Inventory)ob.Clone()).Items;
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
                        double capturedRatio = tank.FilledRatio, litres = tank.m_oldInLitres;
                        var tankSource = tank.SourceComp.m_dataPerType.ToArray();
                        var tankSink = tank.ResourceSink.m_dataPerType.ToArray();
                        bool forcePower = tank.ResourceSink.m_forceTriggerPowerChanged, emptyEffect = tank.m_isEmptyDamageEffectStarted;
                        bool detailedInfo = tank.m_detailedInfoDirty, syncNeeded = tank.m_syncNeeded;
                        var previousColor = tank.m_prevColor; int previousFill = tank.m_prevFillCount;
                        double previousTransfer = tank.m_prevSumTotalTransfer;
                        bool tankWritten = false;
                        Add(store.Block, () =>
                        {
                            if (!restore)
                            {
                                // Native sync quantization dispatches the local server receiver,
                                // which owns assignment, source capacity, sink and notifications.
                                ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(ratio, true);
                                tankWritten = true;
                            }
                            else try { ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(capturedRatio, true); }
                            finally
                            {
                                tank.m_filledRatio = capturedRatio; tank.m_oldInLitres = litres;
                                tank.SourceComp.m_dataPerType = tankSource; tank.ResourceSink.m_dataPerType = tankSink;
                                tank.ResourceSink.m_forceTriggerPowerChanged = forcePower; tank.m_isEmptyDamageEffectStarted = emptyEffect;
                                tank.m_detailedInfoDirty = detailedInfo; tank.m_syncNeeded = syncNeeded;
                                tank.m_prevColor = previousColor; tank.m_prevFillCount = previousFill; tank.m_prevSumTotalTransfer = previousTransfer;
                            }
                        }, b => ((MyObjectBuilder_GasTank)b).FilledRatio = ratio);
                        validate.Add(() =>
                        {
                            if (tankWritten && (tank.FilledRatio != ratio || tank.SourceComp.RemainingCapacityByType(tank.BlockDefinition.StoredGasId)
                                != (float)((double)ratio * tank.Capacity))) throw new InvalidDataException("Native tank reservoir/source mismatch");
                        });
                        break;
                    case "Engine":
                        var engine = (MyGasFueledPowerProducer)block; float capacity = ((MyObjectBuilder_FueledPowerProducer)saved[store.Block]).Capacity;
                        float capturedCapacity = engine.Capacity, previousCapacity = engine.m_previousCapacity;
                        var engineSource = engine.SourceComp.m_dataPerType.ToArray();
                        Add(store.Block, () =>
                        {
                            if (!restore) engine.Capacity = capacity;
                            else try { engine.Capacity = capturedCapacity; }
                            finally
                            {
                                engine.m_capacity.m_value = capturedCapacity;
                                engine.m_previousCapacity = previousCapacity;
                                engine.SourceComp.m_dataPerType = engineSource;
                            }
                        }, b => ((MyObjectBuilder_FueledPowerProducer)b).Capacity = capacity); break;
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
                float reactorPrevious = reactor.m_previousCapacity;
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
                Add(source.Block, () =>
                {
                    reactor.m_timer.FramesFromLastTrigger = elapsed; reactor.m_timer.TimerTickInFrames = period;
                    try { reactor.Capacity = capacity; }
                    finally { if (restore) reactor.m_previousCapacity = reactorPrevious; }
                }, b => ((MyObjectBuilder_Reactor)b).Capacity = capacity);
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
                var component = (MyObjectBuilder_SafeZoneComponent)saved[upkeep.Block].ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single().Clone();
                long now = MySandboxGame.TotalGamePlayTimeInMilliseconds;
                var (paidUpkeep, serializedUpkeep) = AccountingStock.ZoneUpkeep(component.UpkeepTime, now);
                // Keep the raw native input separate from its serialized expected
                // output. The immutable original rollback basis is never rewritten.
                if (!restore) saved[upkeep.Block].ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single().UpkeepTime = serializedUpkeep;
                long paidActivation = checked(now + component.ActivationTime);
                bool activating = manager.m_activating, processing = manager.m_processingActivation;
                var remaining = manager.m_timeLeft; var originalUpkeep = manager.m_upkeepTime;
                long originalActivation = manager.m_safeZoneActivationTimeMS;
                var zoneSink = zone.ResourceSink.m_dataPerType.ToArray();
                bool zoneForcePower = zone.ResourceSink.m_forceTriggerPowerChanged;
                var originalUpdates = zone.NeedsUpdate; bool waiting = manager.WaitingResponse;
                var bubblePose = new VRage.MyPositionAndOrientation(zone.WorldMatrix).GetMatrix();
                var initialZones = MyEntities.GetEntities().OfType<MySafeZone>().ToHashSet();
                MySafeZone created = null;
                bool protectedZone = false;
                bool zoneWritten = false;
                string settings = AccountingStock.ZoneSettings(component.SafeZoneOb as MyObjectBuilder_SafeZone);
                void ValidateZone()
                {
                    if (manager.SafeZoneEntityId != (created?.EntityId ?? 0)
                        || created != null && (created.MarkedForClose || !ReferenceEquals(MyEntities.GetEntityById(created.EntityId), created)
                            || initialZones.Contains(created) || created.SafeZoneBlockId != zone.EntityId || created.Enabled != protectedZone || created.WorldMatrix != bubblePose))
                        throw new InvalidDataException("Accounting safe zone ownership changed");
                    if (created != null)
                    {
                        var actual = (MyObjectBuilder_SafeZone)created.GetObjectBuilder();
                        actual.Enabled = false; // retained settings are disabled; live protection is checked separately
                        if (AccountingStock.ZoneSettings(actual) != settings)
                            throw new InvalidDataException("Native safe zone settings changed");
                    }
                }
                validate.Add(ValidateZone);
                Add(upkeep.Block, () =>
                {
                    if (restore)
                    {
                        // Only remove newly allocated bubbles owned by this block.
                        // A foreign id installed by a callback must never be deleted.
                        if (MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone own, false)
                            && !initialZones.Contains(own) && own.SafeZoneBlockId == zone.EntityId) manager.SafeZoneRemove_Server();
                        else foreach (var bubble in MyEntities.GetEntities().OfType<MySafeZone>().Where(z => !initialZones.Contains(z) && z.SafeZoneBlockId == zone.EntityId).ToArray()) bubble.Close();
                        manager.m_safeZoneEntityId.Value = 0;
                    }
                    manager.Deserialize((MyObjectBuilder_SafeZoneComponent)component.Clone());
                    zoneWritten = true;
                    if (restore)
                    {
                        manager.m_activating = activating; manager.m_timeLeft = remaining;
                        manager.m_upkeepTime = originalUpkeep; manager.m_safeZoneActivationTimeMS = originalActivation;
                        manager.m_processingActivation = processing;
                        manager.WaitingResponse = waiting; zone.NeedsUpdate = originalUpdates;
                        zone.ResourceSink.m_dataPerType = zoneSink; zone.ResourceSink.m_forceTriggerPowerChanged = zoneForcePower;
                    }
                }, b =>
                {
                    var target = b.ComponentContainer.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().Single();
                    target.SafeZoneOb = component.SafeZoneOb?.Clone() as MyObjectBuilder_EntityBase;
                    target.UpkeepTime = serializedUpkeep; target.Activating = component.Activating; target.ActivationTime = component.ActivationTime;
                });
                notify.Add(() => zone.ResourceSink.Update());
                notify.Add(() =>
                {
                    ValidateZone();
                    if (upkeep.Exists && upkeep.Enabled && zone.Enabled && zone.IsFunctional)
                    {
                        if (!zone.IsWorking) throw new InvalidDataException("Paid safe zone has no native power");
                        var savedZone = component.SafeZoneOb as MyObjectBuilder_SafeZone ?? throw new InvalidDataException("Paid safe zone settings missing");
                        if (savedZone.SafeZoneBlockId != zone.EntityId) throw new InvalidDataException("Paid safe zone belongs to another block");
                        // Creation owns allocation, settings, sync and activation events.
                        // Its temporary activation deadline is validated before restoring
                        // the already-accounted activation remainder, never learned from it.
                        bool collide = MySessionComponentSafeZones.IsSafeZoneColliding(0, zone.WorldMatrix, MySafeZoneShape.Sphere,
                            VRageMath.MathHelper.Clamp(savedZone.Radius, MySafeZone.MIN_RADIUS, MySafeZone.MAX_RADIUS));
                        bool complete = !component.Activating && !collide;
                        protectedZone = complete;
                        long nativeActivation = collide ? paidActivation : checked(now + zone.Definition.SafeZoneActivationTimeS * 1000);
                        try { manager.SafeZoneCreate_Server(skipActivationTimer: complete); }
                        finally
                        {
                            var allocated = MyEntities.GetEntities().OfType<MySafeZone>().Where(z => !initialZones.Contains(z) && z.SafeZoneBlockId == zone.EntityId).ToArray();
                            if (allocated.Length == 1) created = allocated[0];
                        }
                        ValidateZone();
                        if (created == null || created.Enabled != complete || manager.m_upkeepTime != paidUpkeep
                            || manager.m_activating != (!collide && !complete) || manager.m_safeZoneActivationTimeMS != nativeActivation
                            || manager.m_timeLeft != paidUpkeep - TimeSpan.FromMilliseconds(now))
                            throw new InvalidDataException("Unexpected native safe zone lifecycle result");
                        manager.m_activating = component.Activating;
                        manager.m_safeZoneActivationTimeMS = paidActivation;
                    }
                });
                validate.Add(() =>
                {
                    if (zoneWritten && (manager.m_upkeepTime != paidUpkeep || manager.m_activating != component.Activating
                        || manager.m_safeZoneActivationTimeMS != paidActivation || manager.m_timeLeft != paidUpkeep - TimeSpan.FromMilliseconds(now)))
                        throw new InvalidDataException("Native safe zone credit/activation changed");
                });
            }
        }
        // Seal every intermediate expectation before the first native action.
        // Neither native retained builders nor notification callbacks can rewrite it.
        var stages = new List<string>();
        if (stage != null)
        {
            stages.Add(AccountingStock.TransactionFingerprint(stage.Values.Cast<MyObjectBuilder_CubeGrid>()));
            foreach (var next in advance)
            {
                next();
                stages.Add(AccountingStock.TransactionFingerprint(stage.Values.Cast<MyObjectBuilder_CubeGrid>()));
            }
        }
        return new(write.ToArray(), notify.ToArray(), stages.ToArray(),
            AccountingStock.TransactionFingerprint(builders.Values.Cast<MyObjectBuilder_CubeGrid>()), () => { foreach (var check in validate) check(); });
    }
}
