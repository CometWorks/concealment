using System.Text.Json;
using System.Text.Json.Nodes;
using Concealment.Accounting;

int checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
void Refuses(Action action, string why)
{
    try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or OverflowException) { checks++; return; }
    throw new Exception(why);
}
string epoch = Guid.NewGuid().ToString("D");
OfflineProfile Basis(int bins) => new()
{
    Schema = 3, ClockDomain = "ClusterAuthority", ClockEpoch = epoch, ClockGeneration = 1, SunClockTicks = 0,
    Group = 1, Members = [1], SunFrame = 0, SunPeriodFrames = 200,
    Sources = [new() { Block = 1, Kind = "Solar", Resource = OfflineModel.Electricity, Maximum = 2,
        Curve = Enumerable.Range(0, bins).Select(i => i % 2 == 0 ? 1d : 0d).ToArray() }],
    Sinks = [new() { Block = 2, Resource = OfflineModel.Electricity, Required = 3, Adaptible = true }]
};
SunWorkPath PathFor(long[][] runs, long through) => SunWorkPath.Read(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Epoch = epoch, From = 0L, Through = through, Runs = runs }), epoch, 0, through);
foreach (int bins in new[] { 4, 7, 720 })
{
    // Explicitly retained native-update samples, including stall-sized jumps and
    // a recovery rewind. The reference sums actual per-update geometric bins.
    long[] ticks = Enumerable.Range(0, 300).Select(i => (long)(i < 50 ? i : i < 90 ? i + 300 : i < 170 ? i + 40 : i + 1000) * 160000).ToArray();
    var runs = ticks.Select((t, i) => new[] { (long)i, t }).Where((r, i) => i == 0 || ticks[i] != ticks[i - 1] + 160000).ToArray();
    var p = Basis(bins); p.SunPath = PathFor(runs, ticks.Length);
    var result = OfflineModel.Simulate(p, ticks.Length, ticks.Length);
    double expected = ticks.Sum(t => 2 * ((((t / 160000) % 200) * bins / 200) % 2 == 0 ? 1 : 0)) / 216000d;
    Check(Math.Abs(result.ProducerEnergyMWh.GetValueOrDefault(1) - expected) < 1e-12, $"ordered samples bins={bins} actual={result.ProducerEnergyMWh.GetValueOrDefault(1):R} expected={expected:R} events={result.Events}");
    Check(p.SimulatedThrough == ticks.Length, "actual-wake address retained");
    // The original scalar sun progression cannot reproduce these samples.
    var scalar = Basis(bins); scalar.Schema = 2; scalar.ClockDomain = scalar.ClockEpoch = null; scalar.ClockGeneration = 0; scalar.SunClockTicks = null;
    var old = OfflineModel.Simulate(scalar, ticks.Length, ticks.Length);
    Check(Math.Abs(old.ProducerEnergyMWh.GetValueOrDefault(1) - expected) > 1e-8, "negative scalar-time control differs from actual sun/work path");
}
OfflineProfile Ordered(long[][] runs)
{
    var p = Basis(4); p.Sources[0].Curve = [1, 0, 1, 0]; p.Sinks[0].Required = 1;
    p.Storage = [new() { Block = 3, Kind = "Battery", Resource = OfflineModel.Electricity, Enabled = true,
        Amount = 25d / 216000, Capacity = 25d / 216000, MaxInput = 1, InputConversion = 1d / 216000 }];
    p.Sources.Add(new() { Block = 3, Kind = "Battery", Resource = OfflineModel.Electricity, Maximum = 1, Priority = 1, Storage = 0, StorageCost = 1d / 216000 });
    p.SunPath = PathFor(runs, 100); return p;
}
var early = Ordered([[0, 0], [50, 150 * 160000]]);
var late = Ordered([[0, 50 * 160000], [50, 200 * 160000]]);
OfflineModel.Simulate(early, 100, 100); OfflineModel.Simulate(late, 100, 100);
Check(early.Storage[0].Amount < 1e-12 && late.Storage[0].Amount > 5d / 216000, $"ordered finite storage early={early.Storage[0].Amount:R} late={late.Storage[0].Amount:R}");
var capped = Basis(4); capped.SunPath = PathFor([[0, 0]], 25);
var capResult = OfflineModel.Simulate(capped, 100000000, 25);
Check(capResult.Frames == 25 && capped.SimulatedThrough == 100000000 && capResult.Capped, "first capped history interval survives a much longer absence; excess forfeited");
Check(OfflineModel.Simulate(capped, 100000000, 25).Frames == 0, "same wake cannot charge a second chunk");
foreach (string field in new[] { "ClockDomain", "ClockEpoch", "ClockGeneration", "SunClockTicks" })
{
    var node = JsonNode.Parse(JsonSerializer.Serialize(Basis(4), OfflineProfile.Json))!.AsObject(); node.Remove(field);
    var p = node.Deserialize<OfflineProfile>(OfflineProfile.Json)!;
    Refuses(() => OfflineModel.Simulate(p, 100, 100), "missing required schema3 discriminator must refuse");
}
foreach (long[][] runs in new long[][][] { [], [[1, 0]], [[0, 0], [0, 1]], [[0, -1]], [[0, 0], [10, 0]] })
    Refuses(() => PathFor(runs, 10), "malformed path must refuse before simulation");
var missing = Basis(4); string before = JsonSerializer.Serialize(missing, OfflineProfile.Json);
Refuses(() => OfflineModel.Simulate(missing, 100, 100), "missing actual path must refuse");
Check(before == JsonSerializer.Serialize(missing, OfflineProfile.Json), "refusal preserves original profile and entitlements");
var local = Basis(4); local.ClockDomain = "ClusterLocal"; local.ClockGeneration = 0;
local.FrozenFrame = local.SimulatedThrough = 5; local.SunFrame = 500;
var clock = (Epoch: Guid.NewGuid().ToString("D"), Frame: 1000L, Generation: 2L, SunTicks: 100L * 160000);
var stock = JsonSerializer.Serialize(new { local.Inventories, local.Storage, local.Sources, local.Production, local.ResourceFrameRemaining }, OfflineProfile.Json);
OfflineInterval.Freeze(local, epoch, clock);
Check(local.FrozenFrame == 1000 && local.SimulatedThrough == 1000 && local.ClockEpoch == clock.Epoch && local.SunFrame == 600,
    "zero-unpaid freeze translates native work and non-admin sun origins while preserving the captured manual offset");
Check(stock == JsonSerializer.Serialize(new { local.Inventories, local.Storage, local.Sources, local.Production, local.ResourceFrameRemaining }, OfflineProfile.Json), "freeze preserves all stock/rate/native phase basis");
OfflineInterval.ValidateSettlement(local, true, clock);
Refuses(() => OfflineInterval.Freeze(local, epoch, clock), "authority profile cannot be translated twice");
Refuses(() => OfflineInterval.ValidateSettlement(local, true, clock with { Epoch = epoch }), "wrong WA epoch refused");
Refuses(() => OfflineInterval.ValidateSettlement(local, true, clock with { Generation = 1 }), "older WA generation refused");
Refuses(() => OfflineInterval.ValidateSettlement(local, false, default), "authority interval cannot enter standalone world");
foreach (int schema in new[] { 1, 2 })
{
    var legacy = new OfflineProfile { Schema = schema, ClockFrameSeconds = schema == 1 ? .016 : 1d / 60 };
    string original = JsonSerializer.Serialize(legacy, OfflineProfile.Json);
    Refuses(() => OfflineInterval.ValidateSettlement(legacy, true, clock), "old Cluster native-vs-extrapolated provenance is ambiguous");
    OfflineInterval.ValidateSettlement(legacy, false, default);
    Check(original == JsonSerializer.Serialize(legacy, OfflineProfile.Json), "legacy refusal retains evidence and standalone compatibility");
}
Console.WriteLine($"PASS {checks} production-DLL explicit-domain/ordered-sun/cap/refusal checks");
