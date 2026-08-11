namespace ProjectTimeTracker.Core;

public static class ShortIdleReviewPolicy
{
    public static readonly TimeSpan MaximumAccumulatedInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AccumulationWindow = TimeSpan.FromHours(4);

    public static bool IsAccumulatedInterval(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration < MaximumAccumulatedInterval;

    public static bool TryClipToAccumulationWindow(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset nowUtc,
        out DateTimeOffset clippedStartUtc,
        out DateTimeOffset clippedEndUtc)
    {
        startUtc = startUtc.ToUniversalTime();
        endUtc = endUtc.ToUniversalTime();
        nowUtc = nowUtc.ToUniversalTime();
        clippedStartUtc = startUtc;
        clippedEndUtc = endUtc;
        if (!IsAccumulatedInterval(endUtc - startUtc))
        {
            return false;
        }

        var windowStartUtc = nowUtc - AccumulationWindow;
        clippedStartUtc = startUtc < windowStartUtc
            ? windowStartUtc
            : startUtc;
        clippedEndUtc = endUtc > nowUtc
            ? nowUtc
            : endUtc;
        return IsAccumulatedInterval(clippedEndUtc - clippedStartUtc);
    }

    public static bool ShouldPrompt(
        long accumulatedSeconds,
        int thresholdMinutes,
        int nextPromptMultiplier)
    {
        if (accumulatedSeconds <= 0 ||
            !AccumulatedAwayReviewSettings.IsValidMinimumMinutes(thresholdMinutes))
        {
            return false;
        }

        var multiplier = NormalizePromptMultiplier(nextPromptMultiplier);
        var baseSeconds = (long)thresholdMinutes * 60;
        var requiredSeconds = multiplier > long.MaxValue / baseSeconds
            ? long.MaxValue
            : baseSeconds * multiplier;
        return accumulatedSeconds >= requiredSeconds;
    }

    public static int NextPromptMultiplier(int currentMultiplier, bool removed)
    {
        if (removed)
        {
            return 1;
        }

        currentMultiplier = NormalizePromptMultiplier(currentMultiplier);
        return currentMultiplier == 1
            ? 3
            : currentMultiplier == int.MaxValue
                ? int.MaxValue
                : currentMultiplier + 1;
    }

    public static int NormalizePromptMultiplier(int multiplier) =>
        multiplier <= 1
            ? 1
            : multiplier == 2
                ? 3
                : multiplier;
}
