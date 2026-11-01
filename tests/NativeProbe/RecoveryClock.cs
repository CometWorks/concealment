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
using VRage;
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
    private static readonly long[] ClockHorizons = { 1, 4, 100, 600, 1200, 3600, 7200 };
    private static Type Engine => PluginType.Assembly.GetType("Concealment.Accounting.AccountingEngine");
    private static long HostFrame => (long)Engine.GetProperty("Frame", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private static double HostMilliseconds => ((Func<double>)PluginType.Assembly.GetType("Concealment.Accounting.Host").GetField("ClockMilliseconds", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))();
    private static object ReadClockProfile(MyCubeGrid grid)
    {
        return ((Array)Engine.GetMethod("CaptureFor", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
            new object[] { unchecked((ulong)grid.EntityId), new[] { new[] { grid } }, new[] { grid }, true })).GetValue(0);
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
        clockRows.Clear(); clockNext = 0;
        foreach (var name in request.GetProperty("names").EnumerateArray())
        {
            var grid = Named(name.GetString());
            if (((int)grid.Flags & 4) != 0) throw new InvalidOperationException("Reference must advance native live stocks");
            clockCases.Add(new() { Grid = grid, Initial = ReadClockProfile(grid), NativeFrame = MySession.Static.GameplayFrameCounter,
                ElapsedTicks = MySession.Static.ElapsedGameTime.Ticks, HostMilliseconds = HostMilliseconds, HostFrame = HostFrame, Native = State(grid) });
        }
        return new { cases = clockCases.Select(c => new { name = c.Grid.DisplayName, initial = JsonSerializer.SerializeToElement(c.Initial, Json),
            c.NativeFrame, c.ElapsedTicks, c.HostMilliseconds, c.HostFrame, native = State(c.Grid) }).ToArray(), state = Inspect() };
    }
    private void ObserveClockReference()
    {
        if (clockCases.Count == 0 || MySession.Static.GameplayFrameCounter - clockCases[0].NativeFrame < ClockHorizons[clockNext]) return;
        foreach (var c in clockCases)
        {
            long nativeFrames = MySession.Static.GameplayFrameCounter - c.NativeFrame;
            long elapsedTicks = MySession.Static.ElapsedGameTime.Ticks - c.ElapsedTicks;
            long wake = HostFrame;
            if (elapsedTicks != nativeFrames * 160000 || wake - c.HostFrame != nativeFrames
                || Math.Abs(HostMilliseconds - c.HostMilliseconds - nativeFrames * 16) > 1e-6)
                throw new InvalidOperationException("Actual host/native elapsed clock disagreement");
            var pair = Engine.GetMethod("ModelForHost", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { c.Initial, wake });
            clockRows.Add(new { name = c.Grid.DisplayName, nativeFrames, elapsedTicks, hostUnits = wake - c.HostFrame,
                initial = JsonSerializer.SerializeToElement(c.Initial, Json), initialNative = c.Native, actual = JsonSerializer.SerializeToElement(ReadClockProfile(c.Grid), Json),
                modeled = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item1").GetValue(pair), Json),
                result = JsonSerializer.SerializeToElement(pair.GetType().GetField("Item2").GetValue(pair), Json), native = State(c.Grid) });
        }
        bool complete = ++clockNext == ClockHorizons.Length;
        Write("clock-reference.json", new { complete, state = Inspect(), rows = clockRows.ToArray() });
        if (complete) clockCases.Clear();
    }
}
