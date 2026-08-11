using System.Text;

namespace ProjectTimeTracker.Core;

public static class TextWrapOpportunityFormatter
{
    public const char InvisibleBreak = '\u200B';

    public static string AddInvisibleBreaks(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var formatted = new StringBuilder(text.Length + Math.Max(4, text.Length / 8));
        for (var index = 0; index < text.Length; index++)
        {
            if (ShouldBreakBefore(text, index) &&
                (formatted.Length == 0 || formatted[^1] != InvisibleBreak))
            {
                formatted.Append(InvisibleBreak);
            }

            var current = text[index];
            formatted.Append(current);

            if (IsVisibleSeparator(current) &&
                index + 1 < text.Length &&
                text[index + 1] != InvisibleBreak &&
                !char.IsWhiteSpace(text[index + 1]))
            {
                formatted.Append(InvisibleBreak);
            }
        }

        return formatted.ToString();
    }

    private static bool ShouldBreakBefore(string text, int index)
    {
        if (index == 0 || text[index] == InvisibleBreak)
        {
            return false;
        }

        var current = text[index];
        var previous = text[index - 1];
        if (!char.IsUpper(current))
        {
            return false;
        }

        if (char.IsLower(previous))
        {
            return true;
        }

        return char.IsUpper(previous) &&
               index + 1 < text.Length &&
               char.IsLower(text[index + 1]);
    }

    private static bool IsVisibleSeparator(char character) =>
        character is '_' or '-' or '/' or '\\';
}
