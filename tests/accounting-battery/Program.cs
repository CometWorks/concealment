using System.Text.Json;
using System.Text.Json.Nodes;
using Concealment.Accounting;

int checks = 0;
void Check(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
OfflineProfile Clone(OfflineProfile p) => JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(p, OfflineProfile.Json), OfflineProfile.Json)!;
OfflineProfile Battery(bool creative, int mode = 0, double? amount = null, bool enabled = true)
{
    var p = new OfflineProfile { Group = 1, Members = [1], Storage = [new() {
        Block = 1, Kind = "Battery", Resource = OfflineModel.Electricity, Amount = amount ?? (double).05f,
        Capacity = .05f, MaxInput = .2, InputConversion = 1d / 216000, RechargeEfficiency = .8f,
        Pair = true, Mode = mode, Enabled = enabled, Full = amount == null, TickFrames = 100 }],
        Sources = [new() { Block = 1, Kind = "Battery", Resource = OfflineModel.Electricity,
            Storage = 0, StorageCost = 1d / 216000, Maximum = enabled && mode != 1 ? .2 : 0 }],
        Sinks = [new() { Block = 2, Resource = OfflineModel.Electricity, Required = .1 }] };
    // JSON injection also runs against the old binary: it ignores the new flag
    // and demonstrably debits the NPC battery, rather than failing to compile.
    var json = JsonNode.Parse(JsonSerializer.Serialize(p, OfflineProfile.Json))!;
    json["Storage"]![0]!["Creative"] = creative;
    return json.Deserialize<OfflineProfile>(OfflineProfile.Json)!;
}
foreach (int mode in new[] { 0, 2 })
{
    var p = Battery(true, mode); double before = p.Storage[0].Amount;
    var result = OfflineModel.Simulate(p, 3600, 3600);
    Check(p.Storage[0].Amount == before, $"creative mode={mode} supplies power without debit");
    Check(Math.Abs(result.ConsumerEnergyMWh[2] - .1 * 3600 / 216000) < 1e-12, "creative supply still meets actual grid demand");
    Check(result.EnergyDelta == 0 && !p.Creative, "per-battery exemption does not turn the Survival group Creative");
    Check(OfflineModel.Simulate(p, 3600, 3600).Frames == 0 && p.Storage[0].Amount == before, "serialized payment address prevents double compensation");
}
foreach (int mode in new[] { 0, 1 })
{
    var p = Battery(true, mode, 0); p.Storage[0].ElapsedFrames = 20;
    OfflineModel.Simulate(p, 79, 79);
    Check(p.Storage[0].Amount == 0, "empty creative battery waits for its captured native Update100 boundary");
    var retry = Clone(p);
    OfflineModel.Simulate(p, 80, 1); OfflineModel.Simulate(retry, 80, 1);
    float input = 3600f * .05f / 8f;
    float expected = (100f * (1f / 60f) * 1000f) * (input / (3600f * 1000f)) * .8f;
    Check(p.Storage[0].Amount == expected && retry.Storage[0].Amount == expected, "native creative refill uses capacity/8, efficiency and native float arithmetic after reload");
    OfflineModel.Simulate(p, 3600, 3520);
    Check(p.Storage[0].Amount == (double).05f && p.Storage[0].Full, "creative Auto/Recharge reaches capacity without external power");
}
foreach (int mode in new[] { 0, 1, 2 })
{
    var disabled = Battery(true, mode, .02f, false); OfflineModel.Simulate(disabled, 3600, 3600);
    Check(disabled.Storage[0].Amount == (double).02f, "disabled creative battery neither drains nor refills");
}
var emptyDischarge = Battery(true, 2, 0); var blackout = OfflineModel.Simulate(emptyDischarge, 3600, 3600);
Check(emptyDischarge.Storage[0].Amount == 0 && blackout.ConsumerEnergyMWh[2] == 0, "empty creative Discharge remains unable to supply");
foreach (int mode in new[] { 0, 2 })
{
    var finite = Battery(false, mode); double before = finite.Storage[0].Amount;
    // Historical finite profiles keep the continuous contract. Native sampled
    // Auto captures have a retained real-engine oracle in accounting-auto.
    finite.Storage[0].TickFrames = 0;
    OfflineModel.Simulate(finite, 3600, 3600);
    Check(Math.Abs(before - finite.Storage[0].Amount - .1 * 3600 / 216000) < 1e-12, "legacy normal/forced-Survival battery retains delivered power cost");
}
var legacy = Battery(false, 2);
var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(legacy, OfflineProfile.Json))!; legacyJson["Storage"]![0]!.AsObject().Remove("Creative");
var old = legacyJson.Deserialize<OfflineProfile>(OfflineProfile.Json)!;
OfflineModel.Simulate(old, 3600, 3600); OfflineModel.Simulate(legacy, 3600, 3600);
Check(old.Storage[0].Amount == legacy.Storage[0].Amount, "legacy finite profile representation retains finite semantics");
var mixed = Battery(true, 2); mixed.Sources[0].Maximum = .04;
mixed.Storage.Add(new() { Block = 3, Kind = "Battery", Resource = OfflineModel.Electricity, Amount = .05f, Capacity = .05f, Mode = 2, Pair = true });
mixed.Sources.Add(new() { Block = 3, Kind = "Battery", Resource = OfflineModel.Electricity, Maximum = .06, Storage = 1, StorageCost = 1d / 216000 });
double npcBefore = mixed.Storage[0].Amount, finiteBefore = mixed.Storage[1].Amount;
OfflineModel.Simulate(mixed, 3600, 3600);
Check(mixed.Storage[0].Amount == npcBefore && Math.Abs(finiteBefore - mixed.Storage[1].Amount - .06 * 3600 / 216000) < 1e-12, "mixed group exempts only the NPC battery");
var factory = Battery(true, 2); factory.Sources[0].Maximum = .04;
foreach (string id in new[] { "Fuel", "Ore", "Ingot" }) factory.Items[id] = new();
factory.Inventories = [new() { Block = 3, MaxMass = 1000, MaxVolume = 1000, Stacks = [new() { Item = "Fuel", Amount = 1 }] },
    new() { Block = 2, MaxMass = 1000, MaxVolume = 1000, Stacks = [new() { Item = "Ore", Amount = 100 }] }];
factory.Sources.Add(new() { Block = 3, Kind = "Reactor", Resource = OfflineModel.Electricity, Maximum = .06,
    Fuel = [new() { Item = "Fuel", Amount = .001m, Inventories = [0] }] });
factory.Production = [new() { Block = 2, Refinery = true, Enabled = true, PowerSink = 0, OperationalPower = .1,
    Recipes = [new() { Id = "Refine", UnitsPerFrame = .01, Inputs = [new() { Item = "Ore", Amount = 1, Inventories = [1] }],
        Outputs = [new() { Item = "Ingot", Amount = 1, Inventories = [1] }] }] }];
var produced = OfflineModel.Simulate(factory, 3600, 3600);
Check(factory.Storage[0].Amount == (double).05f && Math.Abs(produced.ProductionUnits[2] - 36) < 1e-10, "creative battery can power production without a global Creative shortcut");
Check(Math.Abs(produced.ProducerFuelConsumed[3]["Fuel"] - .216m) < .000000001m, "mixed reactor pays its share of production power");
Check(Math.Abs(produced.ConsumerEnergyMWh[2] - .1 * 3600 / 216000) < 1e-12, "production is included in the common power budget");
foreach (string broken in new[] { "TickFrames", "Kind", "RechargeEfficiency" })
{
    var invalid = JsonNode.Parse(JsonSerializer.Serialize(Battery(true), OfflineProfile.Json))!;
    if (broken == "Kind") invalid["Storage"]![0]![broken] = "Tank";
    else invalid["Storage"]![0]![broken] = -1;
    var p = invalid.Deserialize<OfflineProfile>(OfflineProfile.Json)!;
    string before = JsonSerializer.Serialize(p, OfflineProfile.Json);
    try { OfflineModel.Simulate(p, 100, 100); throw new Exception("invalid creative reservoir was accepted"); }
    catch (InvalidDataException) { Check(JsonSerializer.Serialize(p, OfflineProfile.Json) == before, "invalid creative reservoir refuses before mutation"); }
}
var capped = Battery(true, 0, 0); var capResult = OfflineModel.Simulate(capped, 100000000, 72 * 225000);
Check(capResult.Capped && capped.Storage[0].Amount == (double).05f && capped.SimulatedThrough == 100000000, "creative refill obeys first-72-hour cap and actual-wake forfeiture");
Console.WriteLine($"{checks} battery assertions passed");
