using System.Diagnostics;

namespace ProjectTimeTracker.Core;

public sealed class SystemClock : IClock
{
    private static readonly double TickToSeconds = 1d / Stopwatch.Frequency;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public double MonotonicSeconds => Stopwatch.GetTimestamp() * TickToSeconds;
}
