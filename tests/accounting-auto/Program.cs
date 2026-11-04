using System.Text.Json;
using Concealment.Accounting;
using System.Diagnostics;
var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference.json")));
int checks = 0;
void Check(bool pass, string why) { if (!pass) throw new Exception(why); checks++; }
foreach (var row in data.RootElement.GetProperty("rows").EnumerateArray())
{
    string name = row.GetProperty("name").GetString()!;
    var p = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
    var legacy = row.GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
    var actual = new OfflineProfile { Storage = row.GetProperty("actual").GetProperty("Storage").Deserialize<List<OfflineStorage>>(OfflineProfile.Json)!, Inventories = row.GetProperty("actual").GetProperty("Inventories").Deserialize<List<OfflineInventory>>(OfflineProfile.Json)! };
    // New capture records the native100 phase for every battery. The retained
    // native oracle already supplies each actual ElapsedFrames; stocks/rates/phase stay unchanged.
    foreach (var store in p.Storage.Where(s => s.Kind == "Battery")) store.TickFrames = 100;
    long frames = row.GetProperty("frames").GetInt64();
    string fuel = "MyObjectBuilder_Ingot/Uranium";
    decimal Stock(OfflineProfile value) => value.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item == fuel).Sum(s => s.Amount);
    decimal beforeFuel = Stock(p), actualFuel = Stock(actual), actualDebit = beforeFuel - actualFuel;
    OfflineModel.Simulate(legacy, legacy.SimulatedThrough + frames, frames);
    Check(Stock(legacy) == row.GetProperty("legacyExpectedFuel").GetDecimal(), name + " legacy continuous profile retains exact prior fuel entitlement");
    var watch = Stopwatch.StartNew();
    var result = OfflineModel.Simulate(p, p.SimulatedThrough + frames, frames);
    Console.WriteLine($"{name}: fuel native={actualFuel} model={Stock(p)} debitError={Math.Abs(Stock(p)-actualFuel)} events={result.Events} seconds={watch.Elapsed.TotalSeconds:F3}");
    Check(result.Frames == 1350000 && result.Skipped == null, "literal six elapsed-world-hour endpoint");
    foreach (var store in p.Storage.Where(s => s.Kind == "Battery"))
    {
        var expected = actual.Storage.Single(s => s.Block == store.Block);
        Check(Math.Abs(store.Amount - expected.Amount) <= Math.Max(.000001, expected.Amount * .01), name + " native battery stock bound");
    }
    Check(Math.Abs(Stock(p)-actualFuel) <= Math.Max(.000001m, actualFuel * .01m), name + " original remaining-fuel stock bound");
    Check(Math.Abs(Stock(p)-actualFuel) <= Math.Max(.000001m, actualDebit * .01m), name + " retained stricter fuel-debit bound");
}
var idle = new OfflineProfile { Group = 1, Members = [1] };
for (int i = 0; i < 200; i++)
{
    idle.Storage.Add(new() { Block = i + 1, Kind = "Battery", Resource = OfflineModel.Electricity, Amount = 3, Capacity = 3, Full = true, Pair = true, Mode = 0, MaxInput = 12, TickFrames = 100, ElapsedFrames = i % 100 });
    idle.Sources.Add(new() { Block = i + 1, Kind = "Battery", Resource = OfflineModel.Electricity, Maximum = 12, Storage = i, StorageCost = 1d / 216000 });
}
var idleResult = OfflineModel.Simulate(idle, 100000000, 72 * 225000);
Check(idleResult.Capped && idleResult.Events < 4, "idle full reservoirs do not exhaust event budget over capped first72hours");
Check(idle.Storage.Select((s, i) => s.Amount == 3 && s.ElapsedFrames == i % 100).All(v => v), "idle float stocks and distinct native phases preserved");
var capped = data.RootElement.GetProperty("rows")[0].GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
foreach (var store in capped.Storage) store.TickFrames = 100;
var cap = OfflineModel.Simulate(capped, 100000000, 72 * 225000);
Check(cap.Capped && cap.Events < 2000000 && capped.Storage.All(s => s.Amount >= 0 && s.Amount <= s.Capacity), "active Auto native phases respect72hour cap, stocks and event budget");
Check(OfflineModel.Simulate(capped, 100000000, 72 * 225000).Frames == 0, "actual wake forfeiture prevents another capped grant");
// R17-1: active phases, unlike the idle control, previously required >3.2M events.
var phased = data.RootElement.GetProperty("rows")[0].GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OfflineProfile.Json), OfflineProfile.Json)!;
var battery = Copy(phased.Storage[0]); var port = Copy(phased.Sources.First(s => s.Storage == 0));
var external = phased.Sources.Where(s => s.Storage < 0).ToList();
phased.Storage.Clear(); phased.Sources.Clear();
for (int i = 0; i < 20; i++)
{
    var store = Copy(battery); store.Block = 1000 + i; store.Amount = store.Capacity; store.Full = true;
    store.TickFrames = 100; store.ElapsedFrames = i * 5; store.Sampling = false; store.AccountedEnergy = 0;
    phased.Storage.Add(store);
    var source = Copy(port); source.Block = store.Block; source.Storage = i; phased.Sources.Add(source);
}
phased.Sources.AddRange(external);
foreach (var stack in phased.Inventories.SelectMany(i => i.Stacks).Where(s => s.Item == "MyObjectBuilder_Ingot/Uranium")) stack.Amount = 1000;
var phasedResult = OfflineModel.Simulate(phased, phased.SimulatedThrough + 72 * 225000, 72 * 225000);
Check(phasedResult.Frames == 72 * 225000 && phasedResult.Events < 2000000, "20 active staggered native phases complete the full72hours under unchanged guard");
Console.WriteLine($"20 active phases: {phasedResult.Events} events");
Console.WriteLine($"PASS {checks} native-reference/legacy/cap assertions");
