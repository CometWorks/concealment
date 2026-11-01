using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concealment.Accounting;

static class JsonBoundary
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string game = Environment.GetEnvironmentVariable("DEDICATED64") ?? throw new Exception("Set DEDICATED64 to the private native DS references");
            string path = Path.Combine(game, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var assembly = typeof(AccountingApi).Assembly;
        var apply = typeof(AccountingApi).GetMethod("Apply")!;
        var entityType = apply.GetParameters()[0].ParameterType.GenericTypeArguments[1];
        var entities = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(long), entityType));
        var host = assembly.GetType("Concealment.Accounting.Host")!;
        host.GetField("ClockMilliseconds", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (Func<double>)(() => 3200));
        host.GetField("Setting", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (Func<string, string>)(key => key == "offline.compensate" ? "true" : "72"));
        typeof(AccountingApi).GetField("configured", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
        var record = assembly.GetType("Shared.Concealment.StandaloneAccounting+Record")!;
        void Refuses(Action action, string why)
        {
            try { action(); }
            catch (Exception e) when ((e is TargetInvocationException ? e.InnerException : e) is JsonException or InvalidDataException)
            { checks++; return; }
            throw new Exception(why);
        }
        var basis = new OfflineProfile { Group = 10, Members = [10], FrozenFrame = 100, SimulatedThrough = 101 };
        foreach (string[] omit in new[] { new[] { "Schema" }, new[] { "ClockFrameSeconds" }, new[] { "Schema", "ClockFrameSeconds" } })
        foreach (int schema in new[] { 1, 2 })
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(basis, OfflineProfile.Json))!.AsObject();
            json["Schema"] = schema;
            foreach (string field in omit) json.Remove(field);
            string original = json.ToJsonString();
            byte[] single = Encoding.UTF8.GetBytes(original), section = Encoding.UTF8.GetBytes("[" + original + "]");
            string label = $"schema {schema} missing {string.Join('/', omit)}";
            Refuses(() => AccountingApi.Read(section), label + " Read");
            Refuses(() => AccountingApi.Model(single, 200, 100), label + " Model before clone");
            Refuses(() => apply.Invoke(null, [entities, new[] { section }, new[] { new long[] { 10 } }]), label + " Apply propagation");
            Refuses(() => JsonSerializer.Deserialize("{\"Profiles\":[" + original + "]}", record, OfflineProfile.Json), label + " nested standalone Record");
            Check(original == json.ToJsonString(), label + " immutable evidence");
        }
        foreach (int schema in new[] { 1, 2, 3 })
        foreach (double clock in new[] { .016, 1d / 60, .02 })
        foreach (bool advanced in new[] { false, true })
        {
            var p = new OfflineProfile { Schema = schema, ClockFrameSeconds = clock, Group = 10, Members = [10], FrozenFrame = 100, SimulatedThrough = advanced ? 101 : 100 };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(p, OfflineProfile.Json);
            byte[] section = Encoding.UTF8.GetBytes("[" + Encoding.UTF8.GetString(bytes) + "]");
            var parsed = AccountingApi.Read(section).Single();
            Check(parsed.Schema == schema && parsed.ClockFrameSeconds == clock, "explicit JSON retains discriminators");
            Check(JsonSerializer.Deserialize("{\"Profiles\":" + Encoding.UTF8.GetString(section) + "}", record, OfflineProfile.Json) != null, "nested explicit fields parse");
            bool valid = schema == 2 && clock == 1d / 60 || schema == 1 && !advanced && (clock == .016 || clock == 1d / 60);
            if (!valid)
            {
                Refuses(() => AccountingApi.Model(bytes, 200, 100), "explicit incompatible history must refuse");
                Refuses(() => apply.Invoke(null, [entities, new[] { section }, new[] { new long[] { 10 } }]), "Apply must propagate explicit clock refusal");
            }
            else
            {
                using var result = JsonDocument.Parse(AccountingApi.Model(bytes, 200, 100));
                Check(result.RootElement.GetProperty("profile").GetProperty("Schema").GetInt32() == 2, "explicit old/new model succeeds");
                Check((result.RootElement.GetProperty("result").GetProperty("ClockMigration").ValueKind != JsonValueKind.Null) == (schema == 1), "only unpaid legacy migrates");
                apply.Invoke(null, [entities, new[] { section }, new[] { new long[] { 10 } }]);
                Check(true, "valid explicit Apply reaches ordinary missing-builder skip");
            }
            Check(bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(p, OfflineProfile.Json)), "input never rewritten");
        }
        Console.WriteLine($"PASS {checks} production-DLL required-discriminator Read/Model/Apply/nested-Record checks; Apply uses inert host and empty builders");
        return checks;
    }
}
