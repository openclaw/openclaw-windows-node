namespace OpenClaw.SetupEngine.UI;

internal static class NativeGatewayEmphasisRanges
{
    internal static bool TryGet(
        string text,
        IEnumerable<string> emphases,
        out List<(int Start, int Length)> ranges)
    {
        ranges = [];
        foreach (string emphasis in emphases)
        {
            if (string.IsNullOrWhiteSpace(emphasis))
                return false;

            int start = text.IndexOf(emphasis, StringComparison.Ordinal);
            if (start < 0)
                return false;

            ranges.Add((start, emphasis.Length));
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        return ranges.Zip(ranges.Skip(1), (left, right) =>
                left.Start + left.Length <= right.Start)
            .All(nonOverlapping => nonOverlapping);
    }
}
