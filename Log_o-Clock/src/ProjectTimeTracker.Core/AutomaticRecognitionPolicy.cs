namespace ProjectTimeTracker.Core;

public sealed record AutomaticRecognitionVisit(
    Guid? ProjectId,
    DateTimeOffset StartedUtc,
    double StartedMonotonicSeconds,
    WindowActivity? Activity);

public sealed record AutomaticRecognitionAction(
    Guid? EndingProjectId,
    DateTimeOffset? EndUtc,
    AutomaticRecognitionVisit? StartingVisit)
{
    public bool IsInitialStart => EndingProjectId is null && EndUtc is null && StartingVisit is not null;
    public bool IsStop => EndingProjectId is not null && EndUtc is not null && StartingVisit is null;
    public bool IsTransition => EndingProjectId is not null && EndUtc is not null && StartingVisit is not null;
}

/// <summary>
/// Keeps a reversible foreground-project timeline. The controller persists only
/// actions returned by <see cref="TakeNextAction"/>; visits newer than the grace
/// period remain memory-only and can be collapsed by returning to an earlier project.
/// </summary>
public sealed class AutomaticRecognitionPolicy
{
    private readonly List<AutomaticRecognitionVisit> _timeline = [];
    private Guid? _committedProjectId;

    public AutomaticRecognitionPolicy(TimeSpan gracePeriod)
    {
        SetGracePeriod(gracePeriod);
    }

    public TimeSpan GracePeriod { get; private set; }

    public Guid? PreferredProjectId =>
        _timeline.LastOrDefault(visit => visit.ProjectId is not null)?.ProjectId ??
        _committedProjectId;

    public IReadOnlyList<AutomaticRecognitionVisit> Timeline => _timeline;

    public void SetGracePeriod(TimeSpan gracePeriod)
    {
        if (gracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        }

        GracePeriod = gracePeriod;
    }

    public void Reset(
        Guid? runningProjectId,
        DateTimeOffset observedUtc,
        double monotonicSeconds)
    {
        _committedProjectId = runningProjectId;
        _timeline.Clear();
        if (runningProjectId is not null)
        {
            _timeline.Add(new AutomaticRecognitionVisit(
                runningProjectId,
                observedUtc.ToUniversalTime(),
                monotonicSeconds,
                Activity: null));
        }
    }

    public Guid? ResolveProjectId(RecognitionMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!match.IsMatch)
        {
            return null;
        }

        if (!match.IsAmbiguous)
        {
            return match.Single!.Project.Id;
        }

        var preferredProjectId = PreferredProjectId;
        return preferredProjectId is not null &&
               match.Candidates.Any(candidate => candidate.Project.Id == preferredProjectId)
            ? preferredProjectId
            : null;
    }

    public void Observe(
        Guid? projectId,
        DateTimeOffset observedUtc,
        double monotonicSeconds,
        WindowActivity? activity)
    {
        observedUtc = observedUtc.ToUniversalTime();
        if (_timeline.Count == 0)
        {
            if (projectId is not null)
            {
                _timeline.Add(new AutomaticRecognitionVisit(
                    projectId,
                    observedUtc,
                    monotonicSeconds,
                    activity));
            }

            return;
        }

        var current = _timeline[^1];
        if (current.ProjectId == projectId)
        {
            return;
        }

        if (projectId is not null)
        {
            var priorIndex = _timeline.FindLastIndex(visit => visit.ProjectId == projectId);
            if (priorIndex >= 0)
            {
                var returnBoundary = _timeline[priorIndex + 1];
                if (monotonicSeconds - returnBoundary.StartedMonotonicSeconds <
                    GracePeriod.TotalSeconds)
                {
                    _timeline.RemoveRange(priorIndex + 1, _timeline.Count - priorIndex - 1);
                    return;
                }
            }
        }

        _timeline.Add(new AutomaticRecognitionVisit(
            projectId,
            observedUtc,
            monotonicSeconds,
            activity));
    }

    public AutomaticRecognitionAction? TakeNextAction(double monotonicSeconds)
    {
        if (_committedProjectId is null)
        {
            var initialIndex = _timeline.FindIndex(visit => visit.ProjectId is not null);
            if (initialIndex < 0)
            {
                return null;
            }

            var initial = _timeline[initialIndex];
            _timeline.RemoveRange(0, initialIndex);
            _committedProjectId = initial.ProjectId;
            return new AutomaticRecognitionAction(
                EndingProjectId: null,
                EndUtc: null,
                StartingVisit: initial);
        }

        if (_timeline.Count < 2)
        {
            return null;
        }

        var boundary = _timeline[1];
        if (monotonicSeconds - boundary.StartedMonotonicSeconds < GracePeriod.TotalSeconds)
        {
            return null;
        }

        var endingProjectId = _committedProjectId;
        var nextProjectIndex = _timeline.FindIndex(1, visit => visit.ProjectId is not null);
        if (nextProjectIndex < 0)
        {
            _timeline.Clear();
            _committedProjectId = null;
            return new AutomaticRecognitionAction(
                endingProjectId,
                boundary.StartedUtc,
                StartingVisit: null);
        }

        var nextVisit = _timeline[nextProjectIndex];
        _timeline.RemoveRange(0, nextProjectIndex);
        _committedProjectId = nextVisit.ProjectId;
        return new AutomaticRecognitionAction(
            endingProjectId,
            boundary.StartedUtc,
            nextVisit);
    }
}
