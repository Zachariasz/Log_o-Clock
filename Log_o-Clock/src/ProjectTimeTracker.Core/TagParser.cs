using System.Text;
using System.Text.RegularExpressions;

namespace ProjectTimeTracker.Core;

public static partial class TagParser
{
    public sealed record Token(int Start, int Length, string Name);

    public static IReadOnlyList<string> Extract(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        return TagExpression()
            .Matches(description)
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool Contains(string? description, string? tag)
    {
        tag = Normalize(tag);
        return tag is not null && Extract(description).Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    public static string? Normalize(string? tag)
    {
        tag = tag?.Trim().TrimStart('#');
        return string.IsNullOrWhiteSpace(tag) || !ValidTagNameExpression().IsMatch(tag)
            ? null
            : tag.ToLowerInvariant();
    }

    public static IReadOnlyList<Token> FindTokens(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return [];
        }

        return TagExpression()
            .Matches(description)
            .Select(match => new Token(
                match.Index,
                match.Length,
                match.Groups[1].Value.ToLowerInvariant()))
            .ToArray();
    }

    public static string? Rename(string? description, string? oldName, string? newName)
    {
        var oldTag = Normalize(oldName);
        var newTag = Normalize(newName);
        if (description is null || oldTag is null || newTag is null ||
            string.Equals(oldTag, newTag, StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }

        var tokens = FindTokens(description);
        if (!tokens.Any(token => string.Equals(token.Name, oldTag, StringComparison.OrdinalIgnoreCase)))
        {
            return description;
        }

        var result = new StringBuilder(description.Length + 8);
        var position = 0;
        foreach (var token in tokens)
        {
            result.Append(description, position, token.Start - position);
            result.Append(string.Equals(token.Name, oldTag, StringComparison.OrdinalIgnoreCase)
                ? $"#{newTag}"
                : description.Substring(token.Start, token.Length));
            position = token.Start + token.Length;
        }

        result.Append(description, position, description.Length - position);
        return result.ToString();
    }

    public static string? ConvertToText(string? description, string? tag)
    {
        var normalizedTag = Normalize(tag);
        if (description is null || normalizedTag is null)
        {
            return description;
        }

        var tokens = FindTokens(description);
        if (!tokens.Any(token => string.Equals(token.Name, normalizedTag, StringComparison.OrdinalIgnoreCase)))
        {
            return description;
        }

        var result = new StringBuilder(description.Length);
        var position = 0;
        foreach (var token in tokens)
        {
            result.Append(description, position, token.Start - position);
            if (string.Equals(token.Name, normalizedTag, StringComparison.OrdinalIgnoreCase))
            {
                result.Append(description, token.Start + 1, token.Length - 1);
            }
            else
            {
                result.Append(description, token.Start, token.Length);
            }

            position = token.Start + token.Length;
        }

        result.Append(description, position, description.Length - position);
        return result.ToString();
    }

    public static string? AppendBracketedTags(string? description, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var existing = Extract(description).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = tags
            .Select(Normalize)
            .Where(tag => tag is not null && !existing.Contains(tag))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (additions.Length == 0)
        {
            return trimmed;
        }

        var suffix = $"[{string.Join(' ', additions.Select(tag => $"#{tag}"))}]";
        return trimmed is null ? suffix : $"{trimmed} {suffix}";
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])#([\p{L}\p{N}_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagExpression();

    [GeneratedRegex(@"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidTagNameExpression();
}
