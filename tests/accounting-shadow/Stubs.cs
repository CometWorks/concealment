using System.Text.Json;
using Concealment.Accounting;

class MyObjectBuilder_EntityBase { }
class MyLog
{
    internal static MyLog Default = new();
    internal readonly List<string> Lines = [];
    internal void WriteLineAndConsole(string line) => Lines.Add(line);
}

namespace Concealment.Accounting
{
    static class Host { internal static bool Cluster => true; }

    internal static partial class AccountingEngine
    {
        internal static readonly Dictionary<long, OfflineResult> Results = new();
        internal static readonly List<long> Committed = [];
        static void ValidateBatteryModes(OfflineProfile profile, IDictionary<long, MyObjectBuilder_EntityBase> entities) { }
        static void Commit(OfflineProfile profile, OfflineProfile model, IDictionary<long, MyObjectBuilder_EntityBase> entities) => Committed.Add(profile.Group);

        // The fields Apply reads, filled the way Wake.Run fills them: sun rotation off, so no
        // sun/work path, matching content, and each model's exception kept in Failures.
        internal sealed class Wake
        {
            internal OfflineProfile[] Profiles;
            internal (string Epoch, long Frame, long Generation, long SunTicks) Authority = (Program.Epoch, 20000, 1, 0);
            internal Exception PathFailure;
            internal bool[] Content;
            internal Exception[] ContentFailures;
            internal (OfflineProfile Model, OfflineResult Result)[] Models;
            internal Exception[] Failures;
            internal double[] Seconds;
            internal int MaxEvents = 500000;

            internal static Wake Run(OfflineProfile[] profiles, Func<OfflineProfile, Exception> transient)
            {
                var wake = new Wake { Profiles = profiles, Content = profiles.Select(_ => true).ToArray(), ContentFailures = new Exception[profiles.Length],
                    Models = new (OfflineProfile, OfflineResult)[profiles.Length], Failures = new Exception[profiles.Length], Seconds = new double[profiles.Length] };
                for (int i = 0; i < profiles.Length; i++)
                {
                    try
                    {
                        if (transient(profiles[i]) is { } error) throw error;
                        var model = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profiles[i], OfflineProfile.Json), OfflineProfile.Json)!;
                        wake.Models[i] = (model, OfflineModel.Simulate(model, wake.Authority.Frame, 72 * 225000, wake.MaxEvents));
                    }
                    catch (Exception e) { wake.Failures[i] = e; }
                }
                return wake;
            }
        }
    }
}
