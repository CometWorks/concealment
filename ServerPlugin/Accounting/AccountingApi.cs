using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Concealment.Accounting;

// Versioned process-local boundary. JSON uses the original field names/units;
// native arguments are shared game types. Works with Magnetar's random source
// assembly names and built plugins without linking a second copy of the model.
public static class AccountingApi
{
    public const int Contract = 3;
    private static bool configured;
    private static string owner;
    public static void Configure(string host, Dictionary<string, object> callbacks)
    {
        if (host != (Shared.Concealment.ConcealmentRuntime.ClusterMode ? "Cluster" : "Standalone"))
            throw new InvalidOperationException("Accounting host does not own this process mode.");
        if (configured)
        {
            if (host != owner) throw new InvalidOperationException("Accounting host already configured.");
            if (host == "Cluster") return;
        }
        Host.ClockFrameSeconds = (double)callbacks["clockFrameSeconds"];
        if (Host.ClockFrameSeconds != 1.0 / 60) throw new ArgumentException("Accounting contract 3 requires native resource-frame units; update the host adapter");
        Host.ClockMilliseconds = (Func<double>)callbacks["clock"];
        Host.NativeFrame = host == "Cluster" ? (Func<long>)callbacks["nativeFrame"] : null;
        Host.NativeEpoch = host == "Cluster" ? (Func<string>)callbacks["nativeEpoch"] : null;
        Host.AuthorityClock = host == "Cluster" ? (Func<(string Epoch, long Frame, long Generation, long SunTicks)>)callbacks["authorityClock"] : null;
        Host.SunPath = host == "Cluster" ? (Func<string, string, long, long, byte[]>)callbacks["sunPath"] : null;
        Host.SunClock = host == "Cluster" ? (Func<long>)callbacks["sunClock"] : null;
        if (host == "Cluster" && (Host.NativeFrame == null || Host.NativeEpoch == null || Host.AuthorityClock == null || Host.SunPath == null || Host.SunClock == null))
            throw new ArgumentException("Accounting contract 3 requires explicit native, authority and sun/work callbacks");
        Host.Setting = (Func<string, string>)callbacks["setting"];
        Host.CanProfile = (Func<ulong, uint, bool>)callbacks["admit"];
        Host.Generation = (Func<ulong, uint>)callbacks["generation"];
        Host.CanObserve = (Func<bool>)callbacks["observe"];
        Host.IsFrozen = (Func<long, bool>)callbacks["frozen"];
        Host.CanMeasure = (Func<MyCubeGrid[], bool>)callbacks["measure"];
        Host.AwaitingZonePower = (Func<MySafeZoneBlock, bool>)callbacks["zonePending"];
        owner = host;
        configured = true;
    }
    private static void RequireHost()
    {
        if (!configured) throw new InvalidOperationException("Accounting host has not been configured.");
    }
    internal static void EndStandaloneSession()
    {
        if (owner != "Standalone") return;
        AccountingEngine.EndStandaloneSession();
        OfflineRateCache.Reset();
        configured = false;
        owner = null;
        // Persistence can outlive scheduling; canceled jobs and detached hosts
        // must not retain callable delegates to the old world.
        Host.ClockMilliseconds = null;
        Host.NativeFrame = null;
        Host.NativeEpoch = null;
        Host.AuthorityClock = null;
        Host.SunPath = null;
        Host.SunClock = null;
        Host.Setting = null;
        Host.CanProfile = null;
        Host.Generation = null;
        Host.CanObserve = null;
        Host.IsFrozen = null;
        Host.CanMeasure = null;
        Host.AwaitingZonePower = null;
    }
    public static VRageMath.Vector3D SolarPivot(VRageMath.Vector3 pivot, VRageMath.MatrixD matrix) => AccountingEngine.SolarPivot(pivot, matrix);
    public static bool CanMeasureAssembler(bool repeat, bool disassemble, double progress) => AccountingEngine.CanMeasureAssembler(repeat, disassemble, progress);
    public static long ObservationClockFrames(int seconds) => AccountingEngine.ObservationClockFrames(seconds);
    public static bool ContentReady() { RequireHost(); return AccountingEngine.ContentReady; }
    public static void PreserveMetadata(MyCubeGrid grid, VRage.ObjectBuilders.MyObjectBuilder_EntityBase builder) => AccountingMetadata.Preserve(grid, builder);
    public static string Readiness(MyCubeGrid grid) => AccountingEngine.Readiness(grid);
    public static bool Prepare(ulong scope, MyCubeGrid[][] groups, MyCubeGrid[] occluders, bool quick)
    { RequireHost(); return AccountingEngine.Prepare(scope, groups, occluders, quick); }
    public static byte[] Capture(ulong scope, MyCubeGrid[][] groups, MyCubeGrid[] occluders, bool quick)
    {
        RequireHost();
        var profiles = quick ? AccountingEngine.CaptureForDrain(scope, groups, occluders) : AccountingEngine.CaptureFor(scope, groups, occluders);
        if (profiles != null && Host.Cluster)
        {
            // Capture and serialization run on the game thread. This changes the
            // origin of an unpaid interval, never its stocks, native phase or rate.
            var clock = Host.AuthorityClock();
            foreach (var p in profiles)
            {
                OfflineInterval.Freeze(p, Host.NativeEpoch(), clock);
            }
        }
        return profiles == null ? null : JsonSerializer.SerializeToUtf8Bytes(profiles, OfflineProfile.Json);
    }
    public static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities, byte[][] sections, long[][] covers)
    {
        RequireHost();
        if (sections.Length != covers.Length) throw new InvalidDataException("Profile cover count mismatch");
        var profiles = sections.SelectMany((section, i) => Read(section).Select(p => (p, covers[i].ToHashSet())));
        AccountingEngine.Apply(entities, profiles);
    }
    internal static OfflineProfile[] Read(byte[] profiles) => profiles == null ? []
        : JsonSerializer.Deserialize<OfflineProfile[]>(profiles, OfflineProfile.Json) ?? throw new InvalidDataException("Missing profiles");
    public static long? FrozenFrame(byte[] profiles) => Read(profiles).FirstOrDefault()?.FrozenFrame;
    public static object Summary(byte[] profiles) => Read(profiles).Select(p => new { p.Group, p.Confidence, p.Unknowns, p.ClockDomain, p.ClockEpoch, p.ClockGeneration }).ToArray();
    public static object Status() { RequireHost(); return AccountingEngine.Status(); }
    public static object Profiles(long[] members) => AccountingEngine.Profiles(members);
    public static object AdminRead(long[] members, byte[] profiles) => AccountingEngine.AdminRead(members, profiles == null ? null : Read(profiles));
    public static object DryRun(byte[] profiles, double hours) { RequireHost(); return AccountingEngine.DryRun(profiles, hours); }
    public static void CancelScope(ulong scope) { RequireHost(); AccountingEngine.CancelScope(scope); }
    public static void UpdateObservations() { RequireHost(); AccountingEngine.UpdateObservations(); }
    public static object BeginMeasurement(MyProductionBlock block, uint frames) => AccountingEngine.BeginMeasurement(block, frames);
    public static void FinishMeasurement(object state, bool succeeded) => AccountingEngine.FinishMeasurement((AccountingEngine.Measurement)state, succeeded);
    public static Action ObserveReactorFuel(MyReactor reactor, float milliseconds) => AccountingEngine.ObserveReactorFuel(reactor, milliseconds);
    public static string CacheSnapshot() => OfflineRateCache.PersistedRecord();
    public static string CacheContributions() => OfflineRateCache.ContributionsRecord();
    public static void CacheRestore(string payload) => OfflineRateCache.Restore(payload);
    public static void CacheReceive(string payload) => OfflineRateCache.Receive(payload);
    public static string CacheClear(string key, int? epoch) => OfflineRateCache.ClearOnAuthority(key, epoch);
    public static object CacheStatus() => OfflineRateCache.Status();
    public static byte[] Model(byte[] profile, long wakeFrame, long maxFrames)
    {
        var value = JsonSerializer.Deserialize<OfflineProfile>(profile, OfflineProfile.Json) ?? throw new InvalidDataException("Missing profile");
        var result = OfflineModel.Simulate(value, wakeFrame, maxFrames);
        return JsonSerializer.SerializeToUtf8Bytes(new { profile = value, result }, OfflineProfile.Json);
    }
}
