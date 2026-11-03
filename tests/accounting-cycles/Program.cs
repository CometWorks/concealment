using System.Text.Json;
using Concealment.Accounting;
var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference.json")));
T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OfflineProfile.Json), OfflineProfile.Json)!;
int checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
OfflineProfile Basis()
{
    var p = document.RootElement.GetProperty("rows")[0].GetProperty("initial").Deserialize<OfflineProfile>(OfflineProfile.Json)!;
    var battery = Copy(p.Storage[0]); var source = Copy(p.Sources.First(s => s.Storage == 0));
    var external = p.Sources.Where(s => s.Storage < 0).ToList(); p.Storage.Clear(); p.Sources.Clear();
    for (int i = 0; i < 20; i++)
    {
        var s = Copy(battery); s.Block = 1000 + i; s.Amount = s.Capacity; s.Full = true;
        s.TickFrames = 100; s.ElapsedFrames = i * 5; s.Sampling = false; s.AccountedEnergy = 0; p.Storage.Add(s);
        var port = Copy(source); port.Block = s.Block; port.Storage = i; p.Sources.Add(port);
    }
    p.Sources.AddRange(external); return p;
}
(OfflineProfile P, OfflineResult R) Compare(OfflineProfile p, long frames, string name)
{
    var ordinary = Copy(p);
    ordinary.SunPath = p.SunPath; // the detached path is intentionally JsonIgnore
    var actual = OfflineModel.Simulate(p, p.SimulatedThrough + frames, frames);
    var expected = SerialModel.Simulate(ordinary, ordinary.SimulatedThrough + frames, frames);
    Check(JsonSerializer.Serialize(p, OfflineProfile.Json) == JsonSerializer.Serialize(ordinary, OfflineProfile.Json), name + " exact stocks/phases/timers/queues/profile");
    Check(actual.Events <= expected.Events, name + " event count");
    foreach (var field in new[]{"ProducerEnergyMWh", "ConsumerEnergyMWh", "StorageInputEnergyMWh", "SolarShadowAllowanceMWh", "ProductionUnits"})
    {
        var a = (Dictionary<long,double>)typeof(OfflineResult).GetField(field)!.GetValue(actual)!;
        var e = (Dictionary<long,double>)typeof(OfflineResult).GetField(field)!.GetValue(expected)!;
        Check(a.Keys.Order().SequenceEqual(e.Keys.Order()) && a.All(kv => Math.Abs(kv.Value-e[kv.Key]) <= 1e-10 * Math.Max(1,Math.Abs(e[kv.Key]))), name + " serial attribution " + field);
        typeof(OfflineResult).GetField(field)!.SetValue(actual, e);
    }
    int events = actual.Events; actual.Events = expected.Events;
    Check(JsonSerializer.Serialize(actual, OfflineProfile.Json) == JsonSerializer.Serialize(expected, OfflineProfile.Json), name + " exact fuel/upkeep/results");
    actual.Events = events;
    Console.WriteLine($"{name}: {events}/{expected.Events} evaluated events");
    return (p, actual);
}
foreach (long end in new[]{99L,100,101,999,1000,1001,35099,35100,35101,100000}) Compare(Basis(), end, "phase/fuel/tier endpoint " + end);
var accelerated = Compare(Basis(), 100000, "repeated cycles");
Check(accelerated.R.Events * 2 < 20001, "fixture must actually exercise batching");
var solar = Basis(); solar.SunFrame = 0; solar.SunPeriodFrames = 7200;
solar.Sources.Add(new(){ Block=3000, Resource=OfflineModel.Electricity, Kind="Environmental", Maximum=.15, Priority=1, Curve=[0,.8,.2,.8], ShadowAllowance=[0,.01,.005,.01] });
Compare(solar, 30001, "changing sun/shadow bins");
var mixed = Basis(); mixed.SunFrame = 0; mixed.SunPeriodFrames = 7800;
mixed.Sources.Add(new(){Block=3003,Resource=OfflineModel.Electricity,Kind="Environmental",Maximum=0,Priority=1,Curve=[0,0,0]});
mixed.Sources.Add(new(){Block=3004,Resource=OfflineModel.Electricity,Kind="Environmental",Maximum=1,Priority=1,Curve=[0,1]});
Compare(mixed, 30001, "mixed2/3 solar bins");
var cluster = Copy(mixed); cluster.Schema = OfflineProfile.ClusterSchema;
cluster.ClockDomain = "ClusterAuthority"; cluster.ClockEpoch = Guid.NewGuid().ToString("D"); cluster.ClockGeneration = 1; cluster.SunClockTicks = 0;
cluster.SunPath = SunWorkPath.Read(JsonSerializer.SerializeToUtf8Bytes(new SunWorkPath {Version=1,Epoch=cluster.ClockEpoch,From=cluster.SimulatedThrough,Through=cluster.SimulatedThrough+30001,
    Runs=[[cluster.SimulatedThrough,0],[cluster.SimulatedThrough+3777,9900L*160000]]}, OfflineProfile.Json),cluster.ClockEpoch,cluster.SimulatedThrough,cluster.SimulatedThrough+30001);
Compare(cluster, 30001, "Cluster exact sun/work path jump");
var zone = Basis(); zone.Items["chip"] = new(){Volume=1,Mass=1}; zone.Inventories[0].Stacks.Add(new(){Item="chip",Amount=2});
zone.Sinks.Add(new(){Block=3001,Resource=OfflineModel.Electricity,Required=.1});
zone.Upkeep.Add(new(){Block=3001,PowerSink=zone.Sinks.Count-1,OperationalPower=.1,IdlePower=.001,Enabled=true,Exists=true,RemainingFrames=1100,PeriodFrames=2400,Chips=new(){Item="chip",Amount=1,Inventories=[0]}});
Compare(zone, 30000, "zone renew/exhaust");
var depletion = Basis(); foreach(var s in depletion.Inventories.SelectMany(i=>i.Stacks))s.Amount=.000002m;
Compare(depletion, 30000, "fuel depletion and paused timer");
var continuous = Basis(); continuous.Sources.Single(s=>s.Kind=="Reactor").FuelTickFrames=0;
Compare(continuous, 10000, "legacy continuous-fuel fallback");
var fractional = Basis(); fractional.Sources.Single(s=>s.Kind=="Reactor").FuelElapsedFrames += .5;
Compare(fractional, 10000, "fractional timer fallback");
var production = Basis(); production.Items["ore"]=new(){Volume=.001m,Mass=.001m}; production.Items["ingot"]=new(){Volume=.001m,Mass=.001m};
production.Inventories[0].Stacks.Add(new(){Item="ore",Amount=10});
production.Sinks.Add(new(){Block=3002,Resource=OfflineModel.Electricity,Required=.1});
production.Production.Add(new(){Block=3002,Refinery=true,Enabled=true,PowerSink=production.Sinks.Count-1,OperationalPower=.1,Recipes=[new(){Id="refine",UnitsPerFrame=.00001,Inputs=[new(){Item="ore",Amount=1,Inventories=[0]}],Outputs=[new(){Item="ingot",Amount=1,Inventories=[0]}]}]});
Compare(production, 10000, "nonrepeating production stocks");
Console.WriteLine($"PASS {checks} exact-state/serial/boundary assertions (synthetic, no native host)");
