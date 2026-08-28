using System.Globalization;
using System.Text;

namespace ProjectTimeTracker.Core;

public enum AutomaticTagDecisionKind
{
    None,
    Apply,
    Suggest,
}

public sealed record AutomaticTagCandidate(
    Guid? TagId,
    string Name,
    string MatchText,
    string? BuiltInKey = null,
    IReadOnlyList<string>? Aliases = null)
{
    public IReadOnlyList<string> MatchAliases => Aliases ?? [];
}

public sealed record AutomaticTagScore(
    AutomaticTagCandidate Candidate,
    double Similarity);

public sealed record AutomaticTagHistoryEvidence(
    Guid TagId,
    string TagName,
    int MatchingEntries,
    int TaggedEntries,
    int RunnerUpEntries)
{
    public bool IsDominant =>
        MatchingEntries >= AutomaticTaggingPolicy.MinimumHistoryMatches &&
        TaggedEntries > 0 &&
        (double)MatchingEntries / TaggedEntries >= AutomaticTaggingPolicy.HistoryShareThreshold &&
        MatchingEntries > RunnerUpEntries;
}

public sealed record AutomaticTagPolicyDecision(
    AutomaticTagDecisionKind Kind,
    string? TagName = null,
    Guid? TagId = null,
    string? BuiltInKey = null,
    string? MatchText = null,
    double Confidence = 0,
    string? Evidence = null)
{
    public static AutomaticTagPolicyDecision NoTag { get; } = new(AutomaticTagDecisionKind.None);
}

/// <summary>
/// Selects no more than one conservative subject/domain tag. Exact task preferences,
/// history, and curated aliases are intentionally evaluated before semantic scores.
/// </summary>
public sealed class AutomaticTaggingPolicy
{
    public const int MinimumHistoryMatches = 2;
    public const double HistoryShareThreshold = 0.80d;
    public const double AutomaticSimilarityThreshold = 0.80d;
    public const double AutomaticMarginThreshold = 0.10d;
    public const double SuggestionSimilarityThreshold = 0.65d;
    public const double SuggestionMarginThreshold = 0.04d;

    public AutomaticTagPolicyDecision Decide(
        string taskName,
        bool entryAlreadyTagged,
        TaskAutomaticTagPreference? preference,
        AutomaticTagHistoryEvidence? history,
        IReadOnlyList<AutomaticTagCandidate> candidates,
        IReadOnlyList<AutomaticTagScore>? semanticScores = null)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        ArgumentNullException.ThrowIfNull(candidates);

        if (entryAlreadyTagged || string.IsNullOrWhiteSpace(taskName))
        {
            return AutomaticTagPolicyDecision.NoTag;
        }

        if (preference is { IsSuppressed: true })
        {
            return AutomaticTagPolicyDecision.NoTag;
        }

        if (preference is { TagId: { } preferredTagId, TagName: { } preferredTagName })
        {
            return new AutomaticTagPolicyDecision(
                AutomaticTagDecisionKind.Apply,
                preferredTagName,
                preferredTagId,
                Confidence: 1,
                Evidence: "task preference");
        }

        if (history is { IsDominant: true })
        {
            return new AutomaticTagPolicyDecision(
                AutomaticTagDecisionKind.Apply,
                history.TagName,
                history.TagId,
                Confidence: (double)history.MatchingEntries / history.TaggedEntries,
                Evidence: "task history");
        }

        var normalizedTaskTokens = Tokenize(taskName);
        var exact = candidates
            .Where(candidate => candidate.MatchAliases.Any(alias =>
                Tokenize(alias).Count > 0 &&
                ContainsTokenSequence(normalizedTaskTokens, Tokenize(alias))))
            .Take(2)
            .ToArray();
        if (exact.Length == 1)
        {
            return FromCandidate(
                AutomaticTagDecisionKind.Apply,
                exact[0],
                confidence: 1,
                evidence: "curated alias");
        }

        if (semanticScores is null || semanticScores.Count == 0)
        {
            return AutomaticTagPolicyDecision.NoTag;
        }

        var ranked = semanticScores
            .Where(score => candidates.Any(candidate => SameCandidate(candidate, score.Candidate)))
            .OrderByDescending(score => score.Similarity)
            .ThenBy(score => score.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length == 0)
        {
            return AutomaticTagPolicyDecision.NoTag;
        }

        var best = ranked[0];
        var runnerUp = ranked.Length > 1 ? ranked[1].Similarity : 0;
        var margin = best.Similarity - runnerUp;
        if (best.Similarity >= AutomaticSimilarityThreshold && margin >= AutomaticMarginThreshold)
        {
            return FromCandidate(
                AutomaticTagDecisionKind.Apply,
                best.Candidate,
                best.Similarity,
                "semantic match");
        }

        return best.Similarity >= SuggestionSimilarityThreshold && margin >= SuggestionMarginThreshold
            ? FromCandidate(
                AutomaticTagDecisionKind.Suggest,
                best.Candidate,
                best.Similarity,
                "semantic suggestion")
            : AutomaticTagPolicyDecision.NoTag;
    }

    private static AutomaticTagPolicyDecision FromCandidate(
        AutomaticTagDecisionKind kind,
        AutomaticTagCandidate candidate,
        double confidence,
        string evidence) =>
        new(
            kind,
            candidate.Name,
            candidate.TagId,
            candidate.BuiltInKey,
            candidate.MatchText,
            confidence,
            evidence);

    private static bool SameCandidate(AutomaticTagCandidate left, AutomaticTagCandidate right) =>
        left.TagId == right.TagId &&
        string.Equals(left.BuiltInKey, right.BuiltInKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            }
            else
            {
                AddToken(tokens, current);
            }
        }

        AddToken(tokens, current);
        return tokens;
    }

    private static bool ContainsTokenSequence(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate)
    {
        if (candidate.Count == 0 || candidate.Count > source.Count)
        {
            return false;
        }

        for (var start = 0; start <= source.Count - candidate.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < candidate.Count; offset++)
            {
                if (!string.Equals(source[start + offset], candidate[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddToken(ICollection<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}

public static class AutomaticTagStarterCatalog
{
    public const string Version = "1";

    public static IReadOnlyList<AutomaticTagCandidate> Concepts { get; } =
    [
        Concept("animal", "Animals, pets, wildlife, and creatures.", "dog", "cat", "horse", "bird", "fish", "pet", "wildlife", "creature"),
        Concept("person", "Real people, professions, portraits, and human subjects.", "person", "people", "human", "portrait", "man", "woman", "child"),
        Concept("character", "Fictional, stylised, or game characters and mascots.", "character", "hero", "villain", "mascot", "avatar", "npc"),
        Concept("environment", "Scenes, locations, landscapes, worlds, and environments.", "environment", "scene", "location", "world", "landscape", "level"),
        Concept("nature", "Plants, weather, geology, and natural phenomena.", "nature", "plant", "tree", "flower", "weather", "mountain", "ocean"),
        Concept("architecture", "Buildings, structures, and architectural work.", "architecture", "building", "house", "bridge", "tower", "facade"),
        Concept("interior", "Rooms, furniture, and interior spaces.", "interior", "room", "kitchen", "bedroom", "office", "furniture"),
        Concept("vehicle", "Cars, aircraft, boats, trains, and other vehicles.", "vehicle", "car", "truck", "motorcycle", "aircraft", "plane", "boat", "train"),
        Concept("product", "Consumer products, packaging, tools, and physical objects.", "product", "packaging", "bottle", "device", "tool", "appliance"),
        Concept("technology", "Hardware, electronics, infrastructure, and technical systems.", "technology", "hardware", "electronics", "server", "network", "robot"),
        Concept("software", "Applications, websites, APIs, code, and software systems.", "software", "application", "website", "api", "code", "database"),
        Concept("food", "Food, drinks, cooking, and hospitality subjects.", "food", "drink", "meal", "recipe", "restaurant", "coffee"),
        Concept("fashion", "Clothing, footwear, accessories, and fashion subjects.", "fashion", "clothing", "dress", "shirt", "shoe", "jewellery"),
        Concept("sport", "Sports, exercise, teams, and sporting equipment.", "sport", "football", "basketball", "running", "fitness", "athlete"),
        Concept("health", "Health, medicine, care, and wellbeing.", "health", "medical", "medicine", "hospital", "patient", "wellbeing"),
        Concept("education", "Teaching, courses, schools, and learning materials.", "education", "school", "course", "lesson", "student", "training"),
        Concept("finance", "Money, accounting, banking, investment, and insurance.", "finance", "invoice", "accounting", "bank", "investment", "insurance"),
        Concept("legal", "Contracts, compliance, law, and legal matters.", "legal", "contract", "compliance", "law", "policy", "licence"),
        Concept("marketing", "Campaigns, advertising, brands, and promotional work.", "marketing", "campaign", "advertising", "brand", "promotion", "social media"),
        Concept("entertainment", "Film, television, music, games, and live entertainment.", "entertainment", "film", "movie", "television", "music", "game", "concert"),
    ];

    private static AutomaticTagCandidate Concept(
        string name,
        string matchText,
        params string[] aliases) =>
        new(
            TagId: null,
            Name: name,
            MatchText: matchText,
            BuiltInKey: name,
            Aliases: aliases);
}
