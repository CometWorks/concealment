using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders.Private;
using VRage.ObjectBuilders;
using VRageMath;

namespace ExtractionProbe;

public sealed partial class Probe
{
    private static MyObjectBuilder_CubeGrid EnvironmentGrid(string name, int lane)
    {
        var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
        b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
        b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(50000 + lane * 5000, 50000, 0), Vector3D.Forward, Vector3D.Up);
        b.DisplayName = "extract-env-" + name;
        return b;
    }

    private static object EnvironmentFixtures()
    {
        long owner = MySession.Static.Players.CreateNewIdentity("extraction-zone-owner", initialPlayer: true).IdentityId;
        for (int variant = 0; variant < 2; variant++)
        {
            var b = EnvironmentGrid(variant == 0 ? "zone-paid" : "zone-exhaust", variant);
            var reactor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
            reactor.Owner = owner; reactor.Min = new SerializableVector3I(0, 0, 0); reactor.UseConveyorSystem = false; b.CubeBlocks.Add(reactor);
            var refinery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Refinery>("LargeRefinery");
            refinery.Owner = owner; refinery.Min = new SerializableVector3I(5, 0, 0); refinery.UseConveyorSystem = false; b.CubeBlocks.Add(refinery);
            var zoneBuilder = MyObjectBuilderSerializerKeen.CreateNewObject<ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock>("SafeZoneBlock");
            zoneBuilder.Owner = owner; zoneBuilder.Min = new SerializableVector3I(10, 0, 0); zoneBuilder.UseConveyorSystem = false; b.CubeBlocks.Add(zoneBuilder);
            for (int x = 0; x <= 12; x++)
            {
                var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(x, -1, 0); b.CubeBlocks.Add(armor);
            }
            var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
            grid.GetFatBlocks<MyReactor>().Single().GetInventory().AddItems(10, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
            grid.GetFatBlocks<MyRefinery>().Single().InputInventory.AddItems(10000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron"));
            var zone = grid.GetFatBlocks<MySafeZoneBlock>().Single();
            if (variant == 0) zone.GetInventory().AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Component>("ZoneChip"));
            var manager = zone.Components.Get<MySafeZoneComponent>();
            manager.m_upkeepTime = TimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds + 75000);
            manager.m_timeLeft = TimeSpan.FromSeconds(75);
            manager.SafeZoneCreate_Server(skipActivationTimer: true);
            if (!MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone native)) throw new InvalidOperationException("Fixture native zone missing");
            var settings = (MyObjectBuilder_SafeZone)native.GetObjectBuilder(); settings.Radius = 10;
            settings.AllowedActions ^= MySafeZoneAction.Welding; MySessionComponentSafeZones.UpdateSafeZone(settings, true);
        }
        for (int variant = 0; variant < 2; variant++)
        {
            var b = EnvironmentGrid(variant == 0 ? "gas-paid" : "gas-cold", 2 + variant);
            var engine = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_HydrogenEngine>("LargeHydrogenEngine");
            engine.Min = new SerializableVector3I(0, 0, 0); engine.Capacity = variant == 0 ? 10000 : 0; b.CubeBlocks.Add(engine);
            var tank = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenTank>("LargeHydrogenTank");
            tank.Min = new SerializableVector3I(5, 0, 0); tank.FilledRatio = variant == 0 ? 200000f / 15000000 : 0; b.CubeBlocks.Add(tank);
            var generator = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>();
            generator.Min = new SerializableVector3I(12, 0, 0); generator.UseConveyorSystem = false; b.CubeBlocks.Add(generator);
            var light = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna");
            light.Min = new SerializableVector3I(25, 0, 0); light.BroadcastRadius = 50000; b.CubeBlocks.Add(light);
            var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
            ConnectEnvironmentPorts(grid);
            grid.GetFatBlocks<MyGasGenerator>().Single().GetInventory().AddItems(500, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
        }
        var solar = EnvironmentGrid("solar", 4);
        var panelBuilder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SolarPanel>("LargeBlockSolarPanel");
        solar.CubeBlocks.Add(panelBuilder);
        var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock");
        battery.Min = new SerializableVector3I(-3, 0, 0); battery.CurrentStoredPower = 0; battery.ChargeMode = 1; solar.CubeBlocks.Add(battery);
        var sunGrid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(solar, false);
        var panel = sunGrid.GetFatBlocks<MySolarPanel>().Single();
        Vector3D normal = panel.SolarPanelDefinition.PanelOrientation, sun = MySector.DirectionToSunNormalized;
        var local = MatrixD.CreateWorld(Vector3D.Zero, normal, Vector3D.CalculatePerpendicularVector(normal));
        var facing = MatrixD.CreateWorld(Vector3D.Zero, sun, Vector3D.CalculatePerpendicularVector(sun));
        var aligned = MatrixD.Invert(local) * facing; aligned.Translation = sunGrid.PositionComp.GetPosition(); sunGrid.WorldMatrix = aligned;
        return Inspect();
    }

    // Same ordinary conveyor geometry as the retained native reference fixture;
    // no artificial distributor links or resource-state assignments.
    private static void ConnectEnvironmentPorts(MyCubeGrid grid)
    {
        int minX = grid.Min.X - 6, maxX = grid.Max.X + 6, minY = grid.Min.Y - 6, maxY = grid.Max.Y + 6, minZ = grid.Min.Z - 6, maxZ = grid.Max.Z + 6;
        var ports = grid.GetFatBlocks().OfType<IMyConveyorEndpointBlock>().Select(e => e.ConveyorEndpoint.GetPosition(0).NeighbourGridPosition).ToArray();
        foreach (var port in ports)
        {
            var hub = new Vector3I(0, -4, 5); var queue = new Queue<Vector3I>(); queue.Enqueue(port);
            var previous = new Dictionary<Vector3I, Vector3I> { [port] = port };
            while (queue.Count > 0 && !previous.ContainsKey(hub))
            {
                var cell = queue.Dequeue();
                foreach (var direction in Base6Directions.IntDirections)
                {
                    var next = cell + direction;
                    if (next.X < minX || next.X > maxX || next.Y < minY || next.Y > maxY || next.Z < minZ || next.Z > maxZ || previous.ContainsKey(next)) continue;
                    var block = grid.GetCubeBlock(next);
                    if (block != null && block.BlockDefinition.Id.SubtypeName != "LargeBlockConveyor") continue;
                    previous[next] = cell; queue.Enqueue(next);
                }
            }
            if (!previous.ContainsKey(hub)) throw new InvalidOperationException("Fixture conveyor route missing");
            for (var cell = hub; ; cell = previous[cell])
            {
                if (grid.GetCubeBlock(cell) == null)
                {
                    var junction = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Conveyor>("LargeBlockConveyor"); junction.Min = cell; grid.AddBlock(junction, false);
                }
                if (cell == port) break;
            }
        }
    }

    private static object EnvironmentState(MyCubeGrid grid) => new {
        generators = grid.GetFatBlocks<MyGasGenerator>().Select(g => new { block = g.EntityId, ice = (decimal)g.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ore/Ice")), g.IsWorking }).ToArray(),
        tanks = grid.GetFatBlocks<MyGasTank>().Select(t => new { block = t.EntityId, ratio = t.FilledRatio, capacity = t.Capacity, t.IsWorking }).ToArray(),
        engines = grid.GetFatBlocks<MyGasFueledPowerProducer>().Select(e => new { block = e.EntityId, capacity = e.Capacity }).ToArray(),
        solar = grid.GetFatBlocks<MySolarPanel>().Select(p => new { block = p.EntityId, maximum = p.SourceComp.MaxOutput, output = p.SourceComp.CurrentOutput }).ToArray(),
        zones = grid.GetFatBlocks<MySafeZoneBlock>().Select(z => {
            var manager = z.Components.Get<MySafeZoneComponent>();
            MyEntities.TryGetEntityById(manager.SafeZoneEntityId, out MySafeZone native);
            var saved = (VRage.Game.ObjectBuilders.Components.Beacon.MyObjectBuilder_SafeZoneComponent)manager.Serialize();
            return new { block = z.EntityId, active = native?.Enabled == true, zone = manager.SafeZoneEntityId, z.Enabled,
                supplied = z.ResourceSink.CurrentInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId),
                required = z.ResourceSink.RequiredInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId),
                chips = (decimal)z.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Component/ZoneChip")),
                creditMs = native != null && !saved.Activating ? saved.UpkeepTime : manager.m_timeLeft.TotalMilliseconds, saved.Activating };
        }).ToArray(),
    };
}
