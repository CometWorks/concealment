using System.Text.Json;
namespace Concealment.Accounting;

static class Program
{
    static void Main()
    {
        int checks = 0, reads = 0;
        const string epochId = "00000000-0000-0000-0000-000000000001";
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        OfflineProfile Basis(int schema = 3, string confidence = "Exact") => new()
        {
            Schema = schema, ClockDomain = schema == 3 ? "ClusterAuthority" : null,
            ClockEpoch = schema == 3 ? epochId : null, ClockGeneration = schema == 3 ? 1 : 0,
            SunClockTicks = schema == 3 ? 0 : null, ContentHash = "current", Group = 1, Members = [1],
            SunPeriodFrames = 3750, SunFrame = 2000, Confidence = confidence,
            Sources = [new() { Block = 1, Kind = "Solar", Resource = OfflineModel.Electricity, Maximum = 2, Curve = [0, 1] }],
            Sinks = [new() { Block = 2, Resource = OfflineModel.Electricity, Required = 3, Adaptible = true }]
        };
        byte[] Bytes(OfflineProfile p) => JsonSerializer.SerializeToUtf8Bytes(new[] { p }, OfflineProfile.Json);
        OfflineResult Predict(OfflineProfile p, long frames) => AccountingEngine.DryRun(Bytes(p), frames / 225000d).Single();
        Host.SunPath = (_, epoch, from, through) =>
        {
            reads++;
            return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Epoch = epoch, From = from, Through = through, Runs = new[] { new long[] { 0, 0 } } });
        };
        foreach (int schema in new[] { 2, 3 })
        foreach (double minutes in new[] { 1d, 2d })
        foreach (string confidence in new[] { "Exact", "Observed" })
        {
            MySession.Static.Settings.SunRotationIntervalMinutes = minutes;
            var p = Basis(schema, confidence); byte[] before = Bytes(p);
            var prediction = Predict(p, 1800);
            double period = minutes * 3750;
            double expected = Enumerable.Range(0, 1800).Count(i => (p.SunFrame + i) % period >= period / 2) * 2d / 216000;
            Check(prediction.Confidence == (minutes == 2 && confidence == "Exact" ? "Approximate" : confidence), "prediction reports period fallback quality");
            Check(Math.Abs(prediction.ProducerEnergyMWh[1] - expected) < 1e-12, "prediction uses independent current-period bin integral");
            var actual = AccountingEngine.ModelForHost(p, 1800);
            Check(JsonSerializer.Serialize(prediction, OfflineProfile.Json) == JsonSerializer.Serialize(actual.Result, OfflineProfile.Json), "prediction and wake use identical preparation");
            Check(before.SequenceEqual(Bytes(p)), "prediction/wake preserve original profile bytes and entitlement");
        }
        MySession.Static.Settings.SunRotationIntervalMinutes = 2;
        AccountingEngine.MaxFrames = 500;
        var capped = Predict(Basis(), 1800);
        Check(capped.Capped && capped.Frames == 500 && capped.Confidence == "Approximate", "cap retains quality and bounds historical path");
        Check(Math.Abs(capped.ProducerEnergyMWh[1]) < 1e-12, "capped window remains in definite darkness");
        AccountingEngine.MaxFrames = 72 * 225000;
        var mismatch = Basis(); mismatch.ContentHash = "old"; int prior = reads;
        var skipped = Predict(mismatch, 1800);
        Check(skipped.Confidence == "Frozen" && skipped.Skipped == "content mismatch" && reads == prior, "content mismatch remains skipped before history/modeling");
        Host.SunPath = (_, _, _, _) => throw new InvalidOperationException("future not recorded");
        skipped = Predict(Basis(), 1800);
        Check(skipped.Confidence == "Frozen" && skipped.Skipped.StartsWith("WA sun/work prediction unavailable:"), "unavailable future history still refuses without inventing a path");
        Check(skipped.ClockDomain == "ClusterAuthority" && skipped.ClockEpoch == epochId && skipped.ClockGeneration == 1, "refusal preserves clock provenance");
        foreach (double hours in new[] { double.NaN, double.PositiveInfinity, -1, 169 })
        {
            try { AccountingEngine.DryRun(Bytes(Basis()), hours); throw new Exception("invalid hours accepted"); }
            catch (ArgumentException) { checks++; }
        }
        Check(AccountingEngine.DryRun(null, 0).Length == 0, "absent profiles remain empty");
        Console.WriteLine($"PASS {checks} prediction/current-period/quality/immutable-input/refusal assertions (inert host, real detached model)");
    }
}

static class Host { internal static Func<string, string, long, long, byte[]> SunPath; }
sealed class MySession
{
    internal static readonly MySession Static = new();
    internal readonly SessionSettings Settings = new();
}
sealed class SessionSettings
{
    internal bool EnableSunRotation = true;
    internal double SunRotationIntervalMinutes = 1;
}
