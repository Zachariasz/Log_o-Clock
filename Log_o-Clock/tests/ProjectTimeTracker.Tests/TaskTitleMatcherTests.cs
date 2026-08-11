using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TaskTitleMatcherTests
{
    private readonly TaskTitleMatcher _matcher = new();

    [Fact]
    public void MatchesTaskNameWithinWindowTitleIgnoringCaseAndSeparators()
    {
        var animation = Task("Character Animation");
        var rigging = Task("Rigging");

        var match = _matcher.Match(
            "shot-010_CHARACTER_animation_v04.blend - Blender",
            [rigging, animation]);

        Assert.Equal(animation.Id, match?.Id);
    }

    [Theory]
    [InlineData("CharacterAnimation-v04.blend - Blender")]
    [InlineData("character animation-v04.blend - Blender")]
    [InlineData("character_animation-v04.blend - Blender")]
    [InlineData("character-animation-v04.blend - Blender")]
    public void MatchesTaskWhenSpacesAndDelimitersDiffer(string windowTitle)
    {
        var animation = Task("Character Animation");

        var match = _matcher.Match(windowTitle, [animation]);

        Assert.Equal(animation.Id, match?.Id);
    }

    [Fact]
    public void LongestMatchingTaskNameWins()
    {
        var broad = Task("Animation");
        var specific = Task("Character Animation");

        var match = _matcher.Match(
            "Character Animation - scene.blend",
            [broad, specific]);

        Assert.Equal(specific.Id, match?.Id);
    }

    [Fact]
    public void PartialWordAndArchivedTasksDoNotMatch()
    {
        var partial = Task("Rig");
        var archived = Task("Animation", isArchived: true);

        var match = _matcher.Match(
            "Rigging animation scene",
            [partial, archived]);

        Assert.Null(match);
    }

    [Fact]
    public void DoesNotMatchAJoinedTaskNameAsPartOfALongerWord()
    {
        var partial = Task("Rig");

        var match = _matcher.Match("RiggingPreview.blend", [partial]);

        Assert.Null(match);
    }

    [Fact]
    public void EqualBestMatchesRemainUnselected()
    {
        var first = Task("Shot-010");
        var second = Task("Shot 010");

        var match = _matcher.Match("Shot_010 - Blender", [first, second]);

        Assert.Null(match);
    }

    private static SavedTask Task(string name, bool isArchived = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, isArchived);
}
