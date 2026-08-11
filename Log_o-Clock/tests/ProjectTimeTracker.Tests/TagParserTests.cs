using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TagParserTests
{
    [Fact]
    public void ExtractsNormalizedDistinctTags()
    {
        var tags = TagParser.Extract("Animation #Client-Review, then #client-review and #RIG_02.");

        Assert.Equal(["client-review", "rig_02"], tags);
    }

    [Fact]
    public void TagMatchingIsExactAndCaseInsensitive()
    {
        Assert.True(TagParser.Contains("Work on #Something", "#something"));
        Assert.False(TagParser.Contains("Work on #something-else", "something"));
    }

    [Fact]
    public void RenameChangesOnlyTheExactTag()
    {
        var description = TagParser.Rename("#rig and #rigging, then #RIG", "rig", "rig-new");

        Assert.Equal("#rig-new and #rigging, then #rig-new", description);
    }

    [Fact]
    public void ConvertToTextRemovesOnlyTheExactTagsHashAndPreservesItsText()
    {
        var description = TagParser.ConvertToText("#rig and #rigging, then #RIG plus #keep", "rig");

        Assert.Equal("rig and #rigging, then RIG plus #keep", description);
    }

    [Fact]
    public void AppendsOnlyNewTagsAsABracketedDescriptionSuffix()
    {
        var description = TagParser.AppendBracketedTags(
            "Character polish #anim",
            ["#RIG", "anim", "lighting"]);

        Assert.Equal("Character polish #anim [#lighting #rig]", description);
        Assert.Equal(
            ["anim", "lighting", "rig"],
            TagParser.Extract(description));
    }
}
