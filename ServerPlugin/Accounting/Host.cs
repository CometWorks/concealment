using System;
using System.Globalization;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;

namespace Concealment.Accounting;

// Only the active host supplies clocks, admission and lifecycle. The core never
// discovers a cluster provider or starts a timer/scheduler on its own.
internal static class Host
{
    internal static Func<double> ClockMilliseconds;
    internal static Func<long> NativeFrame;
    internal static Func<string> NativeEpoch;
    internal static Func<(string Epoch, long Frame, long Generation, long SunTicks)> AuthorityClock;
    internal static Func<string, string, long, long, byte[]> SunPath;
    internal static Func<long> SunClock;
    internal static bool Cluster => AuthorityClock != null;
    internal static double ClockFrameSeconds = 1.0 / 60;
    internal static Func<string, string> Setting;
    internal static Func<ulong, uint, bool> CanProfile;
    internal static Func<ulong, uint> Generation;
    internal static Func<bool> CanObserve;
    internal static Func<long, bool> IsFrozen;
    internal static Func<MyCubeGrid[], bool> CanMeasure;
    internal static Func<MySafeZoneBlock, bool> AwaitingZonePower;
}

internal static class HostSettings
{
    internal static string Get(string key) => Host.Setting(key);
    internal static int GetInt(string key) => int.Parse(Get(key), CultureInfo.InvariantCulture);
    internal static bool GetBool(string key) => bool.Parse(Get(key));
}
