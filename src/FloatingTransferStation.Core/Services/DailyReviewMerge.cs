using System.Text;

namespace FloatingTransferStation.Services;

public sealed record DailyReviewMergeResult(string Content, bool HasConflicts);

public static class DailyReviewMerge
{
    public static DailyReviewMergeResult Merge(
        string baseContent,
        string localContent,
        string remoteContent)
    {
        if (localContent == baseContent)
        {
            return new DailyReviewMergeResult(remoteContent, false);
        }

        if (remoteContent == baseContent || localContent == remoteContent)
        {
            return new DailyReviewMergeResult(localContent, false);
        }

        var baseLines = SplitLines(baseContent);
        var localEdits = CreateEdits(baseLines, SplitLines(localContent));
        var remoteEdits = CreateEdits(baseLines, SplitLines(remoteContent));
        var output = new StringBuilder();
        var position = 0;
        var localIndex = 0;
        var remoteIndex = 0;
        var hasConflicts = false;

        while (position < baseLines.Count || localIndex < localEdits.Count || remoteIndex < remoteEdits.Count)
        {
            var local = NextEditAt(localEdits, localIndex, position);
            var remote = NextEditAt(remoteEdits, remoteIndex, position);
            if (local is null && remote is null)
            {
                if (position < baseLines.Count)
                {
                    AppendLine(output, baseLines[position++]);
                    continue;
                }

                break;
            }

            if (local is null)
            {
                AppendLines(output, remote!.Replacement);
                position = Math.Max(position, remote.End);
                remoteIndex++;
                continue;
            }

            if (remote is null)
            {
                AppendLines(output, local.Replacement);
                position = Math.Max(position, local.End);
                localIndex++;
                continue;
            }

            if (local.End == remote.End && local.Replacement.SequenceEqual(remote.Replacement))
            {
                AppendLines(output, local.Replacement);
                position = Math.Max(position, local.End);
                localIndex++;
                remoteIndex++;
                continue;
            }

            hasConflicts = true;
            AppendLine(output, "<<<<<<< 本地");
            AppendLines(output, local.Replacement);
            AppendLine(output, "=======");
            AppendLines(output, remote.Replacement);
            AppendLine(output, ">>>>>>> 文件");
            position = Math.Max(position, Math.Max(local.End, remote.End));
            localIndex++;
            remoteIndex++;
        }

        return new DailyReviewMergeResult(output.ToString().TrimEnd('\n'), hasConflicts);
    }

    private static IReadOnlyList<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);

    private static List<LineEdit> CreateEdits(
        IReadOnlyList<string> source,
        IReadOnlyList<string> target)
    {
        var common = new int[source.Count + 1, target.Count + 1];
        for (var sourceIndex = source.Count - 1; sourceIndex >= 0; sourceIndex--)
        {
            for (var targetIndex = target.Count - 1; targetIndex >= 0; targetIndex--)
            {
                common[sourceIndex, targetIndex] = source[sourceIndex] == target[targetIndex]
                    ? common[sourceIndex + 1, targetIndex + 1] + 1
                    : Math.Max(common[sourceIndex + 1, targetIndex], common[sourceIndex, targetIndex + 1]);
            }
        }

        var edits = new List<LineEdit>();
        var sourcePosition = 0;
        var targetPosition = 0;
        while (sourcePosition < source.Count || targetPosition < target.Count)
        {
            var start = sourcePosition;
            var replacement = new List<string>();
            while (sourcePosition < source.Count &&
                   targetPosition < target.Count &&
                   source[sourcePosition] == target[targetPosition])
            {
                if (sourcePosition > start)
                {
                    break;
                }

                sourcePosition++;
                targetPosition++;
                start = sourcePosition;
            }

            while (sourcePosition < source.Count || targetPosition < target.Count)
            {
                if (sourcePosition < source.Count &&
                    targetPosition < target.Count &&
                    source[sourcePosition] == target[targetPosition])
                {
                    break;
                }

                var takeSource = sourcePosition < source.Count &&
                    (targetPosition >= target.Count ||
                     common[sourcePosition + 1, targetPosition] >= common[sourcePosition, targetPosition + 1]);
                if (takeSource)
                {
                    sourcePosition++;
                }
                else
                {
                    replacement.Add(target[targetPosition++]);
                }
            }

            if (sourcePosition != start || replacement.Count > 0)
            {
                edits.Add(new LineEdit(start, sourcePosition, replacement));
            }

            if (sourcePosition < source.Count && targetPosition < target.Count)
            {
                sourcePosition++;
                targetPosition++;
            }
        }

        return edits;
    }

    private static LineEdit? NextEditAt(
        IReadOnlyList<LineEdit> edits,
        int index,
        int position) =>
        index < edits.Count && edits[index].Start <= position ? edits[index] : null;

    private static void AppendLines(StringBuilder output, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            AppendLine(output, line);
        }
    }

    private static void AppendLine(StringBuilder output, string line) =>
        output.Append(line).Append('\n');

    private sealed record LineEdit(int Start, int End, IReadOnlyList<string> Replacement);
}
