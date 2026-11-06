using System.Text.Json;
using Concealment.Accounting;

static class Program
{
    internal const string Epoch = "6c1f6f0e-3e2b-4d0f-9a51-1151a0000001";
    static int checks;
    static void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
    static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OfflineProfile.Json), OfflineProfile.Json)!;

    // One solar panel feeding an adaptible sink, as Capture writes it. The standalone profile turns
    // through its sun bins; the cluster one has sun rotation off, so it needs no WA sun path.
    static OfflineProfile Solar(long group, double[] curve, double[] allowance, bool cluster = false)
    {
        var p = new OfflineProfile { ClockFrameSeconds = 1.0 / 60, Group = group, Members = [group], FrozenFrame = 10000, SimulatedThrough = 10000,
            ContentHash = "content", SunPeriodFrames = cluster ? 0 : 7200,
            Sources = [new() { Block = group, Kind = "Environmental", Resource = OfflineModel.Electricity, Maximum = 1, Curve = curve, ShadowAllowance = allowance }],
            Sinks = [new() { Block = group + 1, Resource = OfflineModel.Electricity, Required = 2, Adaptible = true }] };
        if (cluster)
        {
            p.Schema = OfflineProfile.ClusterSchema; p.ClockDomain = "ClusterAuthority"; p.ClockEpoch = Epoch; p.ClockGeneration = 1; p.SunClockTicks = 0;
        }
        return p;
    }

    static bool Refused(OfflineProfile p)
    {
        try { OfflineModel.Validate(p); return false; }
        catch (InvalidDataException e) { return e.Message == "Invalid solar shadow allowance"; }
    }

    static void Main()
    {
        // 1. The writer's own allowance, after the save round trip. Partition 3432 on octahedron-test
        // (2026-11-05) held exactly these values: raw 2^-25 written as 2.980232238769531E-08.
        double raw = Math.ScaleB(1, -25);
        var written = Solar(1, [raw, .5], [raw * 0.05, .025]);
        OfflineModel.Validate(written);
        var saved = RoundTrip(written);
        if (saved.Sources[0].Curve[0] == raw) Console.WriteLine("note: this runtime round-trips 2^-25 exactly; the rounding case is not reproduced");
        else Check(saved.Sources[0].Curve[0] == Math.BitDecrement(raw), "System.Text.Json reads 2^-25 back one ulp low");
        var live = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(written, OfflineProfile.Json)
            .Replace(JsonSerializer.Serialize(raw), "2.980232238769531E-08").Replace(JsonSerializer.Serialize(raw * 0.05), "1.4901161193847657E-09"), OfflineProfile.Json)!;
        foreach (var (p, label) in new[] { (saved, "round-tripped writer output"), (live, "live partition 3432 values") })
        {
            Check(!Refused(p), label + " validates");
            var result = OfflineModel.Simulate(RoundTrip(p), p.SimulatedThrough + 225000, 225000);
            double produced = result.ProducerEnergyMWh[1], allowed = result.SolarShadowAllowanceMWh.GetValueOrDefault(1);
            Check(produced > 0 && allowed >= 0 && allowed <= produced * 0.05 / 1.05 * (1 + 1e-9), label + " credits at most the 5 % allowance");
        }

        // 2. A real overshoot is still refused; only rounding is tolerated.
        foreach (var (curve, extra, label) in new[] { (.5, .026, "over 5 %"), (1.0, .01, "over full sun"), (0.0, 1e-9, "allowance without sun"),
            (.5, double.NaN, "non-finite"), (.5, -1e-12, "negative") })
            Check(Refused(Solar(1, [curve], [extra])), label + " refused");

        // 3. Recovery Apply: a bad profile skips its own group, logged once; the load and the other group go on.
        var entities = new Dictionary<long, MyObjectBuilder_EntityBase> { [10] = new(), [20] = new() };
        var bad = Solar(10, [.5], [.4], cluster: true);
        var good = Solar(20, [.5], [.025], cluster: true);
        HashSet<long>[] members = [[10], [20]];
        AccountingEngine.Wake Wake(Func<OfflineProfile, Exception> transient) => AccountingEngine.Wake.Run([RoundTrip(bad), RoundTrip(good)], transient);
        AccountingEngine.Apply(entities, Wake(_ => null), members);
        Check(AccountingEngine.Results[10].Skipped == "Invalid solar shadow allowance" && AccountingEngine.Results[10].Confidence == "Frozen", "bad group skipped and frozen");
        Check(AccountingEngine.Committed.SequenceEqual([20L]) && AccountingEngine.Results[20].Skipped == null, "good group still compensated");
        Check(MyLog.Default.Lines.Count(l => l.Contains("skipped group=10")) == 1, "bad group logged once per load");

        // 4. A transient failure still fails the whole load, before any native write, so the unpaid Final is retried.
        AccountingEngine.Committed.Clear();
        bool failed = false;
        try { AccountingEngine.Apply(entities, Wake(p => p.Group == 20 ? new InvalidOperationException("WA unavailable") : null), members); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && AccountingEngine.Committed.Count == 0, "transient failure fails the load with no partial commit");

        Console.WriteLine($"PASS {checks} solar shadow allowance round-trip and recovery Apply checks (exact production model and Apply)");
    }
}
