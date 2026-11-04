using System;
using System.IO;

namespace Concealment.Accounting;

internal static class OfflineInterval
{
    internal static void Freeze(OfflineProfile p, string localEpoch, (string Epoch, long Frame, long Generation, long SunTicks) clock)
    {
        OfflineModel.ValidateClock(p);
        if (p.Schema != OfflineProfile.ClusterSchema || p.ClockDomain != "ClusterLocal" || p.ClockEpoch != localEpoch
            || p.FrozenFrame != p.SimulatedThrough || !Guid.TryParseExact(clock.Epoch, "D", out _)
            || clock.Frame < 0 || clock.Generation <= 0 || clock.SunTicks < 0)
            throw new InvalidDataException("Only a current unpaid local capture may enter a valid WA domain");
        p.SunFrame += (clock.SunTicks - p.SunClockTicks.Value) / 160000d;
        p.FrozenFrame = p.SimulatedThrough = clock.Frame;
        p.ClockDomain = "ClusterAuthority";
        p.ClockEpoch = clock.Epoch;
        p.ClockGeneration = clock.Generation;
        p.SunClockTicks = clock.SunTicks;
    }

    internal static void ValidateSettlement(OfflineProfile p, bool cluster, (string Epoch, long Frame, long Generation, long SunTicks) clock)
    {
        OfflineModel.ValidateClock(p);
        if (cluster)
        {
            if (p.Schema != OfflineProfile.ClusterSchema || p.ClockDomain != "ClusterAuthority" || p.ClockEpoch != clock.Epoch
                || p.ClockGeneration > clock.Generation || clock.Frame < p.SimulatedThrough)
                throw new InvalidDataException("Ambiguous or incompatible WA simulation interval; immutable Final retained");
        }
        else if (p.Schema == OfflineProfile.ClusterSchema)
            throw new InvalidDataException("Cluster accounting interval cannot enter a standalone world");
    }
}
