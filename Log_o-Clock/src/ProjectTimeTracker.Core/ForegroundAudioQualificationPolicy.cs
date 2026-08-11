namespace ProjectTimeTracker.Core;

public sealed class ForegroundAudioQualificationPolicy(
    TimeSpan? requiredDuration = null)
{
    public static readonly TimeSpan DefaultRequiredDuration = TimeSpan.FromSeconds(10);

    private readonly double _requiredSeconds =
        (requiredDuration ?? DefaultRequiredDuration).TotalSeconds;
    private string? _candidateProcess;
    private double _candidateSince;

    public bool Observe(
        string? normalizedProcessName,
        bool hasActiveRenderSession,
        bool isExplicitMusicOrImage,
        double monotonicSeconds)
    {
        if (string.IsNullOrWhiteSpace(normalizedProcessName) ||
            !hasActiveRenderSession ||
            isExplicitMusicOrImage)
        {
            Reset();
            return false;
        }

        if (!string.Equals(
                _candidateProcess,
                normalizedProcessName,
                StringComparison.OrdinalIgnoreCase) ||
            monotonicSeconds < _candidateSince)
        {
            _candidateProcess = normalizedProcessName;
            _candidateSince = monotonicSeconds;
            return _requiredSeconds <= 0;
        }

        return monotonicSeconds - _candidateSince >= _requiredSeconds;
    }

    public void Reset()
    {
        _candidateProcess = null;
        _candidateSince = 0;
    }
}
