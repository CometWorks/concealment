using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concealment.Accounting;

// Ordered, lossless runs of *observed* native updates. Between run starts every
// recorded update was checked against the native 160000-tick increment. A stall
// contributes no work position; a non-admin sun jump starts a new run.
internal sealed class SunWorkPath
{
    [JsonRequired] public int Version;
    [JsonRequired] public string Epoch;
    [JsonRequired] public long From, Through;
    [JsonRequired] public long[][] Runs;

    internal static SunWorkPath Read(byte[] bytes, string epoch, long from, long through)
    {
        var path = JsonSerializer.Deserialize<SunWorkPath>(bytes, OfflineProfile.Json) ?? throw new InvalidDataException("Missing sun/work path");
        if (path.Version != 1 || path.Epoch != epoch || path.From != from || path.Through != through || from < 0 || through <= from
            || path.Runs == null || path.Runs.Length == 0) throw new InvalidDataException("Incompatible sun/work interval");
        long previous = -1;
        foreach (var run in path.Runs)
        {
            if (run == null || run.Length != 2 || run[0] <= previous || run[0] < 0 || run[0] >= through || run[1] < 0)
                throw new InvalidDataException("Invalid ordered sun/work run");
            previous = run[0];
        }
        if (path.Runs[0][0] > from) throw new InvalidDataException("Sun/work path has no interval origin");
        return path;
    }

    private int Run(long frame)
    {
        int low = 0, high = Runs.Length;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (Runs[middle][0] <= frame) low = middle; else high = middle;
        }
        return low;
    }

    internal double Angle(OfflineProfile p, double address)
    {
        long frame = Math.Min(Through - 1, checked((long)Math.Floor(address + 1e-8)));
        int i = Run(frame);
        long ticks = checked(Runs[i][1] + (frame - Runs[i][0]) * 160000);
        return p.SunFrame + (ticks - p.SunClockTicks.Value) / 160000d;
    }

    // SE-1096: the step runs to the frame whose bin differs, across run starts that keep the bin. A run starts at almost
    // every World Authority heartbeat (the sun clock drifts), so stopping at each one gave an event per 61 frames that
    // changed nothing. The frames walked are remembered per bin count until that frame, for the next events.
    internal double UntilBinChange(OfflineProfile p, double address, int bins)
    {
        long frame = checked((long)Math.Floor(address + 1e-8));
        var known = p.Index?.SunBinChanges;
        if (known != null && known.TryGetValue(bins, out var range) && frame >= range.From && frame < range.Until)
            return Math.Max(1e-8, range.Until - address);
        int run = Run(frame);
        double width = p.SunPeriodFrames / bins;
        double Position(long at)
        {
            double angle = Angle(p, at);
            double position = ((angle % p.SunPeriodFrames + p.SunPeriodFrames) % p.SunPeriodFrames) / width;
            double boundary = Math.Round(position);
            return Math.Abs(position - boundary) <= 1e-8 ? boundary : position;
        }
        double start = Position(frame);
        long bin = (long)Math.Floor(start) % bins, at = frame, until;
        double position = start;
        while (true)
        {
            double units = Math.Max(1, Math.Ceiling((Math.Floor(position) + 1 - position) * width - 1e-8));
            long end = run + 1 < Runs.Length ? Runs[run + 1][0] : Through;
            if (at + units < end) { until = (long)Math.Min(Through, at + units); break; }
            if (end >= Through) { until = Through; break; }
            at = end; run++;
            position = Position(at);
            if ((long)Math.Floor(position) % bins != bin) { until = at; break; }
        }
        known?.Remove(bins);
        known?.Add(bins, (frame, until));
        return Math.Max(1e-8, until - address);
    }
}
