using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class RecognitionEngineTests
{
    private readonly RecognitionEngine _engine = new();

    [Fact]
    public void UnknownTitleDoesNotMatch()
    {
        var candidates = BuildCandidates(("Phoenix", null));
        var result = _engine.Match(Activity("Email - Inbox", "outlook"), candidates);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void MatchIsCaseInsensitiveAndCanFilterProcess()
    {
        var candidates = BuildCandidates(("PHOENIX", "rider.exe"));
        var result = _engine.Match(Activity("solution phoenix – Rider", "Rider"), candidates);
        Assert.True(result.IsMatch);
        Assert.NotNull(result.Single);
    }

    [Fact]
    public void WrongProcessDoesNotMatch()
    {
        var candidates = BuildCandidates(("Phoenix", "rider"));
        var result = _engine.Match(Activity("Phoenix - Browser", "chrome"), candidates);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void LongestTitlePatternWins()
    {
        var client = new Client(Guid.NewGuid(), "Client", "#000000");
        var broadProject = new Project(Guid.NewGuid(), client.Id, "Broad", "#000000");
        var exactProject = new Project(Guid.NewGuid(), client.Id, "Exact", "#000000");
        var candidates = new[]
        {
            new RecognitionCandidate(broadProject, client, new RecognitionRule(Guid.NewGuid(), broadProject.Id, "Phoenix", null)),
            new RecognitionCandidate(exactProject, client, new RecognitionRule(Guid.NewGuid(), exactProject.Id, "Phoenix API", null)),
        };

        var result = _engine.Match(Activity("Phoenix API - Rider", "rider"), candidates);
        Assert.Equal(exactProject.Id, result.Single?.Project.Id);
    }

    [Fact]
    public void EqualBestMatchesAreAmbiguous()
    {
        var client = new Client(Guid.NewGuid(), "Client", "#000000");
        var first = new Project(Guid.NewGuid(), client.Id, "First", "#000000");
        var second = new Project(Guid.NewGuid(), client.Id, "Second", "#000000");
        var candidates = new[]
        {
            new RecognitionCandidate(first, client, new RecognitionRule(Guid.NewGuid(), first.Id, "Common", null)),
            new RecognitionCandidate(second, client, new RecognitionRule(Guid.NewGuid(), second.Id, "Common", null)),
        };

        var result = _engine.Match(Activity("Common - Editor", "editor"), candidates);
        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Single);
    }

    private static WindowActivity Activity(string title, string process) => new(1, title, process, DateTimeOffset.UtcNow);

    private static IReadOnlyList<RecognitionCandidate> BuildCandidates(params (string Pattern, string? Process)[] rules)
    {
        var client = new Client(Guid.NewGuid(), "Client", "#000000");
        var project = new Project(Guid.NewGuid(), client.Id, "Project", "#000000");
        return rules.Select(rule => new RecognitionCandidate(
            project,
            client,
            new RecognitionRule(Guid.NewGuid(), project.Id, rule.Pattern, rule.Process))).ToArray();
    }
}
