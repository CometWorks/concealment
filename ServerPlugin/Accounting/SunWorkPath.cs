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

    internal double UntilBinChange(OfflineProfile p, double address, int bins)
    {
        long frame = checked((long)Math.Floor(address + 1e-8));
        int run = Run(frame);
        double angle = Angle(p, address);
        double width = p.SunPeriodFrames / bins;
        double position = ((angle % p.SunPeriodFrames + p.SunPeriodFrames) % p.SunPeriodFrames) / width;
        double boundary = Math.Round(position);
        if (Math.Abs(position - boundary) <= 1e-8) position = boundary;
        double units = Math.Max(1, Math.Ceiling((Math.Floor(position) + 1 - position) * width - 1e-8));
        double next = Math.Min(Through, frame + units);
        if (run + 1 < Runs.Length) next = Math.Min(next, Runs[run + 1][0]);
        return Math.Max(1e-8, next - address);
    }
}
