namespace ProjectTimeTracker.Core;

public enum AutomationLearningDecisionKind
{
    Ignore,
    Learn,
    NeedsTitleReview,
    Expired,
}

public sealed record AutomationLearningIntent(
    Guid ProjectId,
    string ProjectName,
    string? TaskName,
    double ArmedAtMonotonicSeconds);

public sealed record AutomationLearningDecision(
    AutomationLearningDecisionKind Kind,
    string? TitlePattern = null);

public sealed class AutomationLearningPolicy
{
    public static readonly TimeSpan IntentLifetime = TimeSpan.FromSeconds(60);

    public AutomationLearningDecision Evaluate(
        AutomationLearningIntent intent,
        WindowActivity activity,
        double monotonicSeconds)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(activity);

        if (monotonicSeconds - intent.ArmedAtMonotonicSeconds > IntentLifetime.TotalSeconds)
        {
            return new AutomationLearningDecision(AutomationLearningDecisionKind.Expired);
        }

        if (string.IsNullOrWhiteSpace(activity.ProcessName) ||
            string.IsNullOrWhiteSpace(activity.Title))
        {
            return new AutomationLearningDecision(AutomationLearningDecisionKind.Ignore);
        }

        var projectPhrase = FindKnownPhrase(activity.Title, intent.ProjectName);
        if (projectPhrase is not null)
        {
            return new AutomationLearningDecision(
                AutomationLearningDecisionKind.Learn,
                projectPhrase);
        }

        var taskPhrase = FindKnownPhrase(activity.Title, intent.TaskName);
        return taskPhrase is null
            ? new AutomationLearningDecision(AutomationLearningDecisionKind.NeedsTitleReview)
            : new AutomationLearningDecision(AutomationLearningDecisionKind.Learn, taskPhrase);
    }

    public static string DefaultSoftwareLabel(string processName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Application";
        }

        var words = normalized.Replace('_', ' ').Replace('-', ' ');
        return string.Join(
            ' ',
            words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 1
                    ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string? FindKnownPhrase(string title, string? knownPhrase)
    {
        knownPhrase = string.IsNullOrWhiteSpace(knownPhrase) ? null : knownPhrase.Trim();
        return knownPhrase is not null &&
               title.Contains(knownPhrase, StringComparison.OrdinalIgnoreCase)
            ? knownPhrase
            : null;
    }
}
