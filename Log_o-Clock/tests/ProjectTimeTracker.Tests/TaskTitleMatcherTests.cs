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

    [Fact]
    public void SuggestsFileNameWhenNoSavedTaskMatchesWindowTitle()
    {
        var suggestion = _matcher.Suggest(
            "Autodesk MotionBuilder 2026.1 - G:\\Mój dysk\\Projects\\GameDev\\TheHighlandKeep\\Humanoid\\BoulderSet\\BoulderWalkF\\BoulderWalkF.fbx",
            []);

        Assert.Null(suggestion.SavedTask);
        Assert.Equal("Boulder Walk F", suggestion.TaskName);
    }

    [Fact]
    public void SuggestionRemovesOnlyFinalFileExtension()
    {
        var suggestion = _matcher.Suggest("Blender - C:\\shots\\BoulderWalkF.v002.fbx", []);

        Assert.Equal("Boulder Walk F.v002", suggestion.TaskName);
    }

    [Fact]
    public void SuggestionDoesNotTreatOrdinaryApplicationTitleAsAFile()
    {
        var suggestion = _matcher.Suggest("Autodesk MotionBuilder 2026.1", []);

        Assert.Null(suggestion.SavedTask);
        Assert.Null(suggestion.TaskName);
    }

    [Fact]
    public void SavedTaskMatchTakesPriorityOverFileNameFallback()
    {
        var matchedTask = Task("BoulderWalkF");

        var suggestion = _matcher.Suggest(
            "Autodesk MotionBuilder - G:\\Projects\\BoulderWalkF.fbx",
            [matchedTask]);

        Assert.Equal(matchedTask.Id, suggestion.SavedTask?.Id);
        Assert.Null(suggestion.TaskName);
        Assert.Equal("Boulder Walk F", suggestion.FileTaskName);
    }

    [Fact]
    public void LocalSavedTaskWithOldSpacingIsMarkedForInPlaceCorrection()
    {
        var existingTask = Task("Boulder WalkF");

        var suggestion = _matcher.Suggest(
            "Autodesk MotionBuilder 2026.1 - G:\\Mój dysk\\Projects\\GameDev\\TheHighlandKeep\\Humanoid\\BoulderSet\\BoulderWalkF\\BoulderWalkF.fbx",
            [existingTask]);

        Assert.Equal(existingTask.Id, suggestion.SavedTask?.Id);
        Assert.Equal("Boulder Walk F", suggestion.FileTaskName);
        Assert.True(suggestion.ShouldCorrectSavedTaskName);
    }

    [Fact]
    public void AmbiguousSavedTaskMatchFallsBackToFileName()
    {
        var first = Task("Shot-010");
        var second = Task("Shot 010");

        var suggestion = _matcher.Suggest("MotionBuilder - G:\\Projects\\Shot_010.fbx", [first, second]);

        Assert.Null(suggestion.SavedTask);
        Assert.Equal("Shot_010", suggestion.TaskName);
    }

    private static SavedTask Task(string name, bool isArchived = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, isArchived);
}
