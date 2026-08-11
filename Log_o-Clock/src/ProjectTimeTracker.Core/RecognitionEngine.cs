namespace ProjectTimeTracker.Core;

public sealed class RecognitionEngine
{
    public RecognitionMatch Match(WindowActivity activity, IReadOnlyList<RecognitionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(activity.Title))
        {
            return new RecognitionMatch([], 0);
        }

        var matches = candidates
            .Where(candidate => candidate.Rule.IsEnabled && !candidate.Project.IsArchived && !candidate.Client.IsArchived)
            .Where(candidate => ProcessMatches(activity.ProcessName, candidate.Rule.ProcessName))
            .Where(candidate => activity.Title.Contains(candidate.Rule.TitlePattern, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new { Candidate = candidate, Score = candidate.Rule.TitlePattern.Trim().Length })
            .Where(match => match.Score > 0)
            .ToArray();

        if (matches.Length == 0)
        {
            return new RecognitionMatch([], 0);
        }

        var bestScore = matches.Max(match => match.Score);
        var best = matches
            .Where(match => match.Score == bestScore)
            .Select(match => match.Candidate)
            .GroupBy(candidate => candidate.Project.Id)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Client.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RecognitionMatch(best, bestScore);
    }

    private static bool ProcessMatches(string actual, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        var normalizedConfigured = Path.GetFileNameWithoutExtension(configured.Trim());
        var normalizedActual = Path.GetFileNameWithoutExtension(actual.Trim());
        return string.Equals(normalizedActual, normalizedConfigured, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RecognitionPromptPolicy(TimeSpan leaveThreshold)
{
    private readonly HashSet<Guid> _activeProjects = [];
    private readonly HashSet<Guid> _promptedProjects = [];
    private readonly Dictionary<Guid, double> _leftAt = [];
    private double _snoozedUntilMonotonicSeconds = double.NegativeInfinity;

    public void Observe(IReadOnlyCollection<Guid> matchedProjectIds, double monotonicSeconds)
    {
        ArgumentNullException.ThrowIfNull(matchedProjectIds);

        foreach (var prior in _activeProjects.Where(id => !matchedProjectIds.Contains(id)).ToArray())
        {
            _leftAt.TryAdd(prior, monotonicSeconds);
        }

        foreach (var current in matchedProjectIds)
        {
            if (_leftAt.TryGetValue(current, out var leftAt))
            {
                if (monotonicSeconds - leftAt >= leaveThreshold.TotalSeconds)
                {
                    _promptedProjects.Remove(current);
                }

                _leftAt.Remove(current);
            }
        }

        _activeProjects.Clear();
        _activeProjects.UnionWith(matchedProjectIds);
    }

    public bool CanPrompt(
        Guid projectId,
        bool timerRunning,
        bool systemAvailable,
        double monotonicSeconds) =>
        !timerRunning &&
        systemAvailable &&
        monotonicSeconds >= _snoozedUntilMonotonicSeconds &&
        !_promptedProjects.Contains(projectId);

    /// <summary>
    /// Temporarily suppresses all recognition prompts.  The caller supplies a
    /// monotonic timestamp so this remains correct if the system clock changes.
    /// </summary>
    public void Snooze(double monotonicSeconds, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _snoozedUntilMonotonicSeconds = Math.Max(
            _snoozedUntilMonotonicSeconds,
            monotonicSeconds + duration.TotalSeconds);

        // The reminder which initiated the snooze was already marked as shown.
        // Clear that visit state so normal prompting can resume when the break
        // expires, rather than requiring another leave-and-return cycle.
        _promptedProjects.Clear();
    }

    public void MarkPrompted(IEnumerable<Guid> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        _promptedProjects.UnionWith(projectIds);
    }
}
