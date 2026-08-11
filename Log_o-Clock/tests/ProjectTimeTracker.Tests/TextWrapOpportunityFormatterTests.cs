using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TextWrapOpportunityFormatterTests
{
    private const string Break = "\u200B";

    [Theory]
    [InlineData("simpleWord", "simple\u200BWord")]
    [InlineData("GiantMonkey", "Giant\u200BMonkey")]
    [InlineData("XMLParser", "XML\u200BParser")]
    public void AddsBreaksAtCamelAndPascalCaseBoundaries(string source, string expected)
    {
        Assert.Equal(expected, TextWrapOpportunityFormatter.AddInvisibleBreaks(source));
    }

    [Fact]
    public void AddsBreaksAfterVisibleSeparatorsWithoutDuplicatingAdjacentBreaks()
    {
        const string source = "folder_name-long/path\\GiantMonkey";
        var expected = $"folder_{Break}name-{Break}long/{Break}path\\{Break}Giant{Break}Monkey";

        Assert.Equal(expected, TextWrapOpportunityFormatter.AddInvisibleBreaks(source));
    }

    [Fact]
    public void KeepsTheVisibleTextExactlyUnchanged()
    {
        const string source = "GiantMonkey_folder-name/path\\file.txt";

        var formatted = TextWrapOpportunityFormatter.AddInvisibleBreaks(source);

        Assert.Equal(source, formatted.Replace(Break, string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("two ordinary words", "two ordinary words")]
    public void LeavesTextWithoutAdditionalBoundariesUnchanged(string? source, string expected)
    {
        Assert.Equal(expected, TextWrapOpportunityFormatter.AddInvisibleBreaks(source));
    }
}
