using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Game.EntityComponents;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ExtractionProbe;

public sealed partial class Probe
{
    private static object OpaqueRows(object accounting)
    {
        var values = accounting?.GetType().GetField("opaque", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(accounting) as System.Collections.IDictionary;
        return values?.Keys.Cast<long>().Select(id => new { id, evidence = values[id] }).ToArray();
    }
    private static object RecoveryFixtures()
    {
        Fixtures();
        var grid = Named("extract-powered");
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MotorStator>("LargeStator");
        builder.Min = new SerializableVector3I(10, 0, 0);
        var rotor = (MyMotorStator)(grid.AddBlock(builder, false) ?? throw new InvalidOperationException("Native rotor did not fit")).FatBlock;
        rotor.RecreateTop(builder.Owner, instantBuild: true);
        rotor.TargetVelocityRPM = 0;
        ((Sandbox.ModAPI.Ingame.IMyMotorStator)rotor).RotorLock = true;
        if (rotor.TopGrid == null) throw new InvalidOperationException("Native rotor top missing");
        rotor.TopGrid.DisplayName = "extract-recovery-top";
        return Inspect();
    }

    private sealed class ClockCase
    {
        public MyCubeGrid Grid;
        public object Initial;
        public long NativeFrame, ElapsedTicks, HostFrame;
        public double HostMilliseconds;
        public object Native;
    }
    private readonly List<ClockCase> clockCases = new();
    private readonly List<object> clockRows = new();
    private int clockNext;
    private long clockThrough;
    private JsonElement? pendingClock;
    private readonly List<object> factoryTrace = new();
    private static readonly long[] ClockHorizons = { 1, 4, 100, 600, 1200, 3600, 7200 };
    private static Type Engine => PluginType.Assembly.GetType("Concealment.Accounting.AccountingEngine");
    private static long HostFrame => (long)Engine.GetProperty("Frame", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private static double HostMilliseconds => ((Func<double>)PluginType.Assembly.GetType("Concealment.Accounting.Host").GetField("ClockMilliseconds", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))();
    private static object ReadClockProfile(MyCubeGrid grid)
    {
        return ((Array)Engine.GetMethod("CaptureFor", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
            new object[] { unchecked((ulong)grid.EntityId), new[] { new[] { grid } }, new[] { grid }, true })).GetValue(0);
    }
    private static object ClusterReferenceHost()
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("EXTRACTION_CLUSTER_REFERENCE"));
        var runtime = assembly.GetType("ClusterPlugin.ClusterRuntime");
        if ((bool)runtime.GetProperty("IsClustered").GetValue(null)
            || assembly.GetType("ClusterNodePlugin.Plugin").GetProperty("Instance").GetValue(null) != null)
            throw new InvalidOperationException("Native reference requires a dormant Cluster library");
        // Use the production facade's real Configure callbacks, never test clocks.
        bool ready = (bool)assembly.GetType("ClusterPlugin.OfflineCompensation").GetProperty("ContentReady", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        return new { ready, state = Inspect() };
    }
    private static object FactoryFixture()
    {
        var builder = EnvironmentGrid("factory", 9); builder.DisplayName = "extract-factory";
        var battery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_BatteryBlock>("OfflinePartialBattery");
        battery.Min = new SerializableVector3I(-3, 0, 0); battery.CurrentStoredPower = 3; battery.ChargeMode = 2; builder.CubeBlocks.Add(battery);
        var tank = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_GasTank>("LargeHydrogenTank");
        tank.Min = new SerializableVector3I(0, 0, 0); tank.IsStockpiling = true; builder.CubeBlocks.Add(tank);
        var generator = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_OxygenGenerator>("OfflineCyclingGenerator");
        generator.Min = new SerializableVector3I(1, 1, -1); generator.UseConveyorSystem = false; builder.CubeBlocks.Add(generator);
        var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(builder, false);
        grid.GetFatBlocks<MyGasGenerator>().Single().GetInventory().AddItems(10000, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Ice"));
        return State(grid);
    }
    private static object FactoryState(ClockCase c)
    {
        var gas = c.Grid.GetFatBlocks<MyGasGenerator>().Single();
        var battery = c.Grid.GetFatBlocks<MyBatteryBlock>().Single();
        var tank = c.Grid.GetFatBlocks<MyGasTank>().Single();
        return new { frame = MySession.Static.GameplayFrameCounter - c.NativeFrame, elapsedTicks = MySession.Static.ElapsedGameTime.Ticks,
            working = gas.IsWorking, enabled = gas.SourceComp.Enabled, input = gas.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId),
            output = gas.SourceComp.CurrentOutputByType(MyResourceDistributorComponent.HydrogenId), pending = gas.m_gasOutputAggregate.Values.Sum(),
            ice = (decimal)gas.GetInventory().GetItemAmount(VRage.Game.MyDefinitionId.Parse("MyObjectBuilder_Ore/Ice")),
            battery = battery.CurrentStoredPower, batteryOutput = battery.SourceComp.CurrentOutput, tank = tank.FilledRatio * tank.Capacity };
    }
    private static object ClockModel(JsonElement request)
    {
        var type = PluginType.Assembly.GetType("Concealment.Accounting.OfflineProfile");
        var profile = JsonSerializer.Deserialize(request.GetProperty("profile").GetRawText(), type, Json);
        var pair = Engine.GetMethod("ModelForHost", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
            new object[] { profile, request.GetProperty("wake").GetInt64() });
        return new { original = JsonSerializer.SerializeToElement(profile, Json),
            profile = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item1").GetValue(pair), Json),
            result = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item2").GetValue(pair), Json) };
    }
    private object BeginClockReference(JsonElement request)
    {
        if (clockCases.Count != 0) throw new InvalidOperationException("Clock reference already active");
        if (request.TryGetProperty("phase", out var phase) && phase.GetInt32() >= 0)
        {
            var gas = Named(request.GetProperty("names")[0].GetString()).GetFatBlocks<MyGasGenerator>().Single();
            if ((gas.IsWorking ? 1 : 0) + (gas.SourceComp.Enabled ? 2 : 0) != phase.GetInt32())
            {
                pendingClock = request.Clone();
                Write("clock-reference.json", new { complete = false, waiting = true });
                return new { waiting = true };
            }
        }
        pendingClock = null;
        clockRows.Clear(); clockNext = 0;
        factoryTrace.Clear();
        clockThrough = request.TryGetProperty("through", out var through) ? through.GetInt64() : ClockHorizons.Last();
        if (!ClockHorizons.Contains(clockThrough)) throw new ArgumentException("Unknown native prefix horizon");
        foreach (var name in request.GetProperty("names").EnumerateArray())
        {
            var grid = Named(name.GetString());
            if (((int)grid.Flags & 4) != 0) throw new InvalidOperationException("Reference must advance native live stocks");
            clockCases.Add(new() { Grid = grid, Initial = ReadClockProfile(grid), NativeFrame = MySession.Static.GameplayFrameCounter,
                ElapsedTicks = MySession.Static.ElapsedGameTime.Ticks, HostMilliseconds = HostMilliseconds, HostFrame = HostFrame, Native = State(grid) });
        }
        if (clockCases.Count == 1 && clockCases[0].Grid.DisplayName == "extract-factory") factoryTrace.Add(FactoryState(clockCases[0]));
        Write("clock-reference.json", new { complete = false, waiting = false });
        return new { cases = clockCases.Select(c => new { name = c.Grid.DisplayName, initial = JsonSerializer.SerializeToElement(c.Initial, Json),
            c.NativeFrame, c.ElapsedTicks, c.HostMilliseconds, c.HostFrame, native = State(c.Grid) }).ToArray(), state = Inspect() };
    }
    private void ObserveClockReference()
    {
        if (pendingClock is JsonElement pending) { BeginClockReference(pending); return; }
        if (clockCases.Count == 1 && clockCases[0].Grid.DisplayName == "extract-factory") factoryTrace.Add(FactoryState(clockCases[0]));
        if (clockCases.Count == 0 || MySession.Static.GameplayFrameCounter - clockCases[0].NativeFrame < ClockHorizons[clockNext]) return;
        foreach (var c in clockCases)
        {
            long nativeFrames = MySession.Static.GameplayFrameCounter - c.NativeFrame;
            long elapsedTicks = MySession.Static.ElapsedGameTime.Ticks - c.ElapsedTicks;
            long wake = HostFrame;
            if (elapsedTicks < 0 || wake < c.HostFrame
                || Math.Abs((HostMilliseconds - c.HostMilliseconds) * 10000 - elapsedTicks) > 1)
                throw new InvalidOperationException("Actual host/raw elapsed clock disagreement");
            var pair = Engine.GetMethod("ModelForHost", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { c.Initial, wake });
            clockRows.Add(new { name = c.Grid.DisplayName, nativeFrames, elapsedTicks, hostUnits = wake - c.HostFrame,
                elapsedStartTicks = c.ElapsedTicks, elapsedEndTicks = MySession.Static.ElapsedGameTime.Ticks,
                initial = JsonSerializer.SerializeToElement(c.Initial, Json), initialNative = c.Native, actual = JsonSerializer.SerializeToElement(ReadClockProfile(c.Grid), Json),
                modeled = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item1").GetValue(pair), Json),
                result = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item2").GetValue(pair), Json), native = State(c.Grid) });
        }
        bool complete = ClockHorizons[clockNext++] == clockThrough;
        Write("clock-reference.json", new { complete, state = Inspect(), rows = clockRows.ToArray() });
        if (complete && factoryTrace.Count != 0) Write("clock-factory-trace.json", new { initial = JsonSerializer.SerializeToElement(clockCases[0].Initial, Json), trace = factoryTrace.ToArray() });
        if (complete) clockCases.Clear();
    }
}
