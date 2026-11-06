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
    static class HostSettings { internal static bool GetBool(string key) => true; }
    static class Host
    {
        internal static bool Cluster => true;
        internal static (string Epoch, long Frame, long Generation, long SunTicks) AuthorityClock() => (Program.Epoch, 20000, 1, 0);
    }
    static class OfflineInterval { internal static void ValidateSettlement(OfflineProfile p, bool cluster, (string, long, long, long) clock) => OfflineModel.ValidateClock(p); }

    internal static partial class AccountingEngine
    {
        internal static readonly Dictionary<long, OfflineResult> Results = new();
        internal static readonly List<long> Committed = [];
        internal static Func<OfflineProfile, Exception> Transient = _ => null;
        static long Frame => throw new InvalidOperationException("cluster wakes use the authority clock");
        static void ValidateBatteryModes(OfflineProfile profile, IDictionary<long, MyObjectBuilder_EntityBase> entities) { }
        static SunWorkPath ReadSunPath(OfflineProfile profile, long wake) => null; // sun rotation off: no path needed
        internal static string ContentHash(IEnumerable<string> definitions) => "content";
        // Production ModelWithPath also reads the session's sun period; with rotation off it is 0, as here.
        static (OfflineProfile Model, OfflineResult Result) ModelWithPath(OfflineProfile profile, long wake, SunWorkPath path)
        {
            if (Transient(profile) is { } error) throw error;
            var model = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json)!;
            return (model, OfflineModel.Simulate(model, wake, 72 * 225000));
        }
        static void Commit(OfflineProfile profile, OfflineProfile model, IDictionary<long, MyObjectBuilder_EntityBase> entities) => Committed.Add(profile.Group);
    }
}
