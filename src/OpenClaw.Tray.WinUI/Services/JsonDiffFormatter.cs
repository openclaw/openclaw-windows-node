namespace OpenClawTray.Services;

public enum JsonDiffLineKind
{
    Unchanged,
    Removed,
    Added,
}

public sealed record JsonDiffSegment(string Text, bool IsChanged);

public sealed record JsonDiffLine(JsonDiffLineKind Kind, IReadOnlyList<JsonDiffSegment> Segments)
{
    public string Prefix => Kind switch
    {
        JsonDiffLineKind.Removed => "- ",
        JsonDiffLineKind.Added => "+ ",
        _ => "  "
    };

    public string Text => string.Concat(Segments.Select(s => s.Text));

    public string CopyText => Prefix + Text;
}

public static class JsonDiffFormatter
{
    public static IReadOnlyList<JsonDiffLine> CreateDiff(string originalJson, string proposedJson)
    {
        var diffOps = BuildLineDiff(
            originalJson.Replace("\r\n", "\n").Split('\n'),
            proposedJson.Replace("\r\n", "\n").Split('\n'));
        var lines = new List<JsonDiffLine>(diffOps.Count);

        for (var i = 0; i < diffOps.Count; i++)
        {
            var op = diffOps[i];
            if (op.Kind == JsonDiffLineKind.Unchanged)
            {
                lines.Add(CreateLine(op.Kind, [new JsonDiffSegment(op.Text, false)]));
                continue;
            }

            if (op.Kind == JsonDiffLineKind.Removed &&
                i + 1 < diffOps.Count &&
                diffOps[i + 1].Kind == JsonDiffLineKind.Added &&
                LooksLikeReplacement(op.Text, diffOps[i + 1].Text))
            {
                var added = diffOps[++i];
                lines.Add(CreateLine(JsonDiffLineKind.Removed, BuildInlineSegments(op.Text, added.Text)));
                lines.Add(CreateLine(JsonDiffLineKind.Added, BuildInlineSegments(added.Text, op.Text)));
                continue;
            }

            lines.Add(CreateLine(op.Kind, [new JsonDiffSegment(op.Text, true)]));
        }

        return lines;
    }

    private static JsonDiffLine CreateLine(JsonDiffLineKind kind, IReadOnlyList<JsonDiffSegment> segments) =>
        new(kind, segments);

    private static IReadOnlyList<JsonDiffSegment> BuildInlineSegments(string text, string comparison)
    {
        var sharedPrefix = 0;
        while (sharedPrefix < text.Length &&
               sharedPrefix < comparison.Length &&
               text[sharedPrefix] == comparison[sharedPrefix])
        {
            sharedPrefix++;
        }

        var sharedSuffix = 0;
        while (sharedSuffix < text.Length - sharedPrefix &&
               sharedSuffix < comparison.Length - sharedPrefix &&
               text[text.Length - 1 - sharedSuffix] == comparison[comparison.Length - 1 - sharedSuffix])
        {
            sharedSuffix++;
        }

        var segments = new List<JsonDiffSegment>(3);
        var changedLength = text.Length - sharedPrefix - sharedSuffix;
        if (sharedPrefix > 0)
            segments.Add(new JsonDiffSegment(text[..sharedPrefix], false));
        if (changedLength > 0)
            segments.Add(new JsonDiffSegment(text.Substring(sharedPrefix, changedLength), true));
        if (sharedSuffix > 0)
            segments.Add(new JsonDiffSegment(text[^sharedSuffix..], false));
        if (segments.Count == 0)
            segments.Add(new JsonDiffSegment(text, false));
        return segments;
    }

    private readonly record struct DiffOp(JsonDiffLineKind Kind, string Text);

    private static List<DiffOp> BuildLineDiff(string[] originalLines, string[] proposedLines)
    {
        var prefix = 0;
        while (prefix < originalLines.Length &&
               prefix < proposedLines.Length &&
               string.Equals(originalLines[prefix], proposedLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < originalLines.Length - prefix &&
               suffix < proposedLines.Length - prefix &&
               string.Equals(
                   originalLines[originalLines.Length - 1 - suffix],
                   proposedLines[proposedLines.Length - 1 - suffix],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var result = new List<DiffOp>(originalLines.Length + proposedLines.Length);
        for (var i = 0; i < prefix; i++)
            result.Add(new DiffOp(JsonDiffLineKind.Unchanged, originalLines[i]));

        var originalLength = originalLines.Length - prefix - suffix;
        var proposedLength = proposedLines.Length - prefix - suffix;
        if ((long)originalLength * proposedLength > 1_000_000)
        {
            for (var i = prefix; i < originalLines.Length - suffix; i++)
                result.Add(new DiffOp(JsonDiffLineKind.Removed, originalLines[i]));
            for (var i = prefix; i < proposedLines.Length - suffix; i++)
                result.Add(new DiffOp(JsonDiffLineKind.Added, proposedLines[i]));
            AppendSuffix(result, originalLines, suffix);
            return result;
        }

        var lcs = new int[originalLength + 1, proposedLength + 1];

        for (var i = originalLength - 1; i >= 0; i--)
        {
            for (var j = proposedLength - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(originalLines[prefix + i], proposedLines[prefix + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var originalIndex = 0;
        var proposedIndex = 0;
        while (originalIndex < originalLength && proposedIndex < proposedLength)
        {
            var originalLine = originalLines[prefix + originalIndex];
            var proposedLine = proposedLines[prefix + proposedIndex];
            if (string.Equals(originalLine, proposedLine, StringComparison.Ordinal))
            {
                result.Add(new DiffOp(JsonDiffLineKind.Unchanged, originalLine));
                originalIndex++;
                proposedIndex++;
            }
            else if (lcs[originalIndex + 1, proposedIndex] >= lcs[originalIndex, proposedIndex + 1])
            {
                result.Add(new DiffOp(JsonDiffLineKind.Removed, originalLine));
                originalIndex++;
            }
            else
            {
                result.Add(new DiffOp(JsonDiffLineKind.Added, proposedLine));
                proposedIndex++;
            }
        }

        while (originalIndex < originalLength)
            result.Add(new DiffOp(JsonDiffLineKind.Removed, originalLines[prefix + originalIndex++]));

        while (proposedIndex < proposedLength)
            result.Add(new DiffOp(JsonDiffLineKind.Added, proposedLines[prefix + proposedIndex++]));

        AppendSuffix(result, originalLines, suffix);
        return result;
    }

    private static void AppendSuffix(List<DiffOp> result, string[] originalLines, int suffix)
    {
        for (var i = originalLines.Length - suffix; i < originalLines.Length; i++)
            result.Add(new DiffOp(JsonDiffLineKind.Unchanged, originalLines[i]));
    }

    private static bool LooksLikeReplacement(string originalLine, string proposedLine)
    {
        var originalTrimmed = originalLine.TrimStart();
        var proposedTrimmed = proposedLine.TrimStart();
        if (originalTrimmed.Length == 0 || proposedTrimmed.Length == 0)
            return false;

        if (originalTrimmed[0] is '{' or '}' or '[' or ']' ||
            proposedTrimmed[0] is '{' or '}' or '[' or ']')
            return false;

        var sharedPrefix = 0;
        while (sharedPrefix < originalLine.Length &&
               sharedPrefix < proposedLine.Length &&
               originalLine[sharedPrefix] == proposedLine[sharedPrefix])
        {
            sharedPrefix++;
        }

        var longest = Math.Max(originalLine.Length, proposedLine.Length);
        return longest > 0 && sharedPrefix >= longest / 2;
    }
}
