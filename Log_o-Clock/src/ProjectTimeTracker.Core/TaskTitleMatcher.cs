using System.Text;

namespace ProjectTimeTracker.Core;

public sealed class TaskTitleMatcher
{
    public SavedTask? Match(string windowTitle, IReadOnlyList<SavedTask> projectTasks)
    {
        ArgumentNullException.ThrowIfNull(windowTitle);
        ArgumentNullException.ThrowIfNull(projectTasks);

        var title = TitleParts.Create(windowTitle);
        if (title.Compact.Length == 0)
        {
            return null;
        }

        var matches = projectTasks
            .Where(task => !task.IsArchived)
            .Select(task => new
            {
                Task = task,
                Parts = TitleParts.Create(task.Name),
            })
            .Where(match => match.Parts.Compact.Length >= 2)
            .Where(match => IsMatch(title, match.Parts))
            .Select(match => new
            {
                match.Task,
                Score = match.Parts.Compact.Length,
            })
            .ToArray();

        if (matches.Length == 0)
        {
            return null;
        }

        var bestScore = matches.Max(match => match.Score);
        var bestMatches = matches
            .Where(match => match.Score == bestScore)
            .Select(match => match.Task)
            .DistinctBy(task => task.Id)
            .Take(2)
            .ToArray();
        return bestMatches.Length == 1 ? bestMatches[0] : null;
    }

    private static bool IsMatch(TitleParts title, TitleParts task) =>
        ContainsConsecutiveWords(title.Words, task.Words) ||
        ContainsExactChunkRun(title.Chunks, task.Compact);

    private static bool ContainsConsecutiveWords(
        IReadOnlyList<string> titleWords,
        IReadOnlyList<string> taskWords)
    {
        if (taskWords.Count == 0 || taskWords.Count > titleWords.Count)
        {
            return false;
        }

        for (var titleStart = 0; titleStart <= titleWords.Count - taskWords.Count; titleStart++)
        {
            var allWordsMatch = true;
            for (var offset = 0; offset < taskWords.Count; offset++)
            {
                if (!string.Equals(
                        titleWords[titleStart + offset],
                        taskWords[offset],
                        StringComparison.Ordinal))
                {
                    allWordsMatch = false;
                    break;
                }
            }

            if (allWordsMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExactChunkRun(
        IReadOnlyList<string> titleChunks,
        string taskCompact)
    {
        for (var start = 0; start < titleChunks.Count; start++)
        {
            var candidate = new StringBuilder(taskCompact.Length);
            for (var end = start; end < titleChunks.Count; end++)
            {
                candidate.Append(titleChunks[end]);
                if (candidate.Length > taskCompact.Length)
                {
                    break;
                }

                if (candidate.Length == taskCompact.Length &&
                    string.Equals(candidate.ToString(), taskCompact, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record TitleParts(
        IReadOnlyList<string> Words,
        IReadOnlyList<string> Chunks,
        string Compact)
    {
        public static TitleParts Create(string value)
        {
            var sourceChunks = SplitAtDelimiters(value);
            var chunks = sourceChunks
                .Select(Normalize)
                .ToArray();
            var words = sourceChunks
                .SelectMany(SplitAtWordBoundaries)
                .Select(Normalize)
                .ToArray();
            return new TitleParts(
                words,
                chunks,
                string.Concat(chunks));
        }

        private static IReadOnlyList<string> SplitAtDelimiters(string value)
        {
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    current.Append(character);
                    continue;
                }

                AddCurrentChunk(chunks, current);
            }

            AddCurrentChunk(chunks, current);
            return chunks;
        }

        private static string Normalize(string value) => value.ToUpperInvariant();

        private static IReadOnlyList<string> SplitAtWordBoundaries(string chunk)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            for (var index = 0; index < chunk.Length; index++)
            {
                var character = chunk[index];
                if (current.Length > 0 &&
                    IsWordBoundary(chunk[index - 1], character, index + 1 < chunk.Length ? chunk[index + 1] : null))
                {
                    AddCurrentChunk(words, current);
                }

                current.Append(character);
            }

            AddCurrentChunk(words, current);
            return words;
        }

        private static bool IsWordBoundary(char previous, char current, char? next) =>
            char.IsDigit(previous) != char.IsDigit(current) ||
            (char.IsLower(previous) && char.IsUpper(current)) ||
            (char.IsUpper(previous) && char.IsUpper(current) && next is { } nextCharacter && char.IsLower(nextCharacter));

        private static void AddCurrentChunk(ICollection<string> target, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            target.Add(current.ToString());
            current.Clear();
        }
    }
}
