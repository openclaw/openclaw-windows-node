using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Dashboards;

public abstract record BoardReportBlock;

public sealed record BoardReportTextBlock(string? Title, string Text) : BoardReportBlock;

public sealed record BoardReportMetric(string Label, string Value, string? Detail);

public sealed record BoardReportMetricsBlock(ImmutableArray<BoardReportMetric> Items) : BoardReportBlock;

public sealed record BoardReportTableBlock(string? Title, ImmutableArray<string> Columns, ImmutableArray<ImmutableArray<string>> Rows) : BoardReportBlock;

public sealed record BoardReportPoint(string Label, double Value);

public sealed record BoardReportChartBlock(string? Title, bool Line, ImmutableArray<BoardReportPoint> Points) : BoardReportBlock;

public sealed record BoardReportLink(string Label, Uri Url, string? Detail);

public sealed record BoardReportLinksBlock(string? Title, ImmutableArray<BoardReportLink> Items) : BoardReportBlock;

public sealed record BoardReport(ImmutableArray<BoardReportBlock> Blocks);

/// <summary>
/// Parses <c>session:report</c> props with the upstream
/// <c>src/boards/board-report.ts</c> schema. Reports are data only: no HTML,
/// scripts, styles, or actions, so they render in native controls. Unknown
/// fields are rejected like the upstream strict schema.
/// </summary>
public static class BoardReportParser
{
    public const int MaxJsonBytes = 8 * 1024;

    public static BoardReport Parse(JsonElement? props)
    {
        if (props is not { ValueKind: JsonValueKind.Object } root)
            throw Invalid("root", "expected an object");
        if (Encoding.UTF8.GetByteCount(root.GetRawText()) > MaxJsonBytes)
            throw new FormatException("Report exceeds 8KB JSON budget");
        RequireOnly(root, "root", "blocks");
        if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw Invalid("blocks", "expected an array");
        var count = blocks.GetArrayLength();
        if (count is < 1 or > 24)
            throw Invalid("blocks", "expected 1 to 24 blocks");

        var result = ImmutableArray.CreateBuilder<BoardReportBlock>(count);
        var index = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            result.Add(ParseBlock(block, $"blocks.{index}"));
            index++;
        }
        return new BoardReport(result.MoveToImmutable());
    }

    private static BoardReportBlock ParseBlock(JsonElement block, string path)
    {
        if (block.ValueKind != JsonValueKind.Object)
            throw Invalid(path, "expected an object");
        var type = String(block, "type", path, 1, 32);
        switch (type)
        {
            case "text":
                RequireOnly(block, path, "type", "title", "text");
                return new BoardReportTextBlock(OptionalString(block, "title", path, 1, 120), String(block, "text", path, 1, 4000));
            case "metrics":
            {
                RequireOnly(block, path, "type", "items");
                var items = Array(block, "items", path, 1, 8).Select((item, i) =>
                {
                    var itemPath = $"{path}.items.{i}";
                    RequireObject(item, itemPath);
                    RequireOnly(item, itemPath, "label", "value", "detail");
                    return new BoardReportMetric(
                        String(item, "label", itemPath, 1, 160),
                        String(item, "value", itemPath, 1, 80),
                        OptionalString(item, "detail", itemPath, 0, 240));
                });
                return new BoardReportMetricsBlock([.. items]);
            }
            case "table":
            {
                RequireOnly(block, path, "type", "title", "columns", "rows");
                var columns = Array(block, "columns", path, 1, 8)
                    .Select((column, i) => StringValue(column, $"{path}.columns.{i}", 1, 160)).ToImmutableArray();
                var rows = Array(block, "rows", path, 0, 40).Select((row, r) =>
                {
                    var rowPath = $"{path}.rows.{r}";
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() > 8)
                        throw Invalid(rowPath, "expected up to 8 cells");
                    var cells = row.EnumerateArray().Select((cell, c) => StringValue(cell, $"{rowPath}.{c}", 0, 500)).ToImmutableArray();
                    if (cells.Length != columns.Length)
                        throw Invalid(path, "Every table row must match the columns");
                    return cells;
                }).ToImmutableArray();
                return new BoardReportTableBlock(OptionalString(block, "title", path, 1, 120), columns, rows);
            }
            case "chart":
            {
                RequireOnly(block, path, "type", "title", "style", "points");
                var style = OptionalString(block, "style", path, 1, 8);
                if (style is not (null or "bar" or "line"))
                    throw Invalid($"{path}.style", "expected bar or line");
                var points = Array(block, "points", path, 1, 40).Select((point, i) =>
                {
                    var pointPath = $"{path}.points.{i}";
                    RequireObject(point, pointPath);
                    RequireOnly(point, pointPath, "label", "value");
                    if (!point.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                        !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                        Math.Abs(number) > 9007199254740991d)
                        throw Invalid($"{pointPath}.value", "expected a number");
                    return new BoardReportPoint(String(point, "label", pointPath, 1, 160), number);
                });
                return new BoardReportChartBlock(OptionalString(block, "title", path, 1, 120), style == "line", [.. points]);
            }
            case "links":
            {
                RequireOnly(block, path, "type", "title", "items");
                var items = Array(block, "items", path, 1, 20).Select((item, i) =>
                {
                    var itemPath = $"{path}.items.{i}";
                    RequireObject(item, itemPath);
                    RequireOnly(item, itemPath, "label", "url", "detail");
                    var url = String(item, "url", itemPath, 1, 2048);
                    if (!BoardWidgetUrlPolicy.IsExternalHttpLink(url, out var link))
                        throw Invalid($"{itemPath}.url", "expected an HTTP(S) URL");
                    return new BoardReportLink(String(item, "label", itemPath, 1, 160), link, OptionalString(item, "detail", itemPath, 0, 240));
                });
                return new BoardReportLinksBlock(OptionalString(block, "title", path, 1, 120), [.. items]);
            }
            default:
                throw Invalid($"{path}.type", "unsupported block type");
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string property, string path, int min, int max)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            throw Invalid($"{path}.{property}", "expected an array");
        var length = array.GetArrayLength();
        if (length < min || length > max)
            throw Invalid($"{path}.{property}", $"expected {min} to {max} entries");
        return array.EnumerateArray().ToArray();
    }

    private static string String(JsonElement parent, string property, string path, int min, int max)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw Invalid($"{path}.{property}", "is required");
        return StringValue(value, $"{path}.{property}", min, max);
    }

    private static string? OptionalString(JsonElement parent, string property, string path, int min, int max) =>
        parent.TryGetProperty(property, out var value) ? StringValue(value, $"{path}.{property}", min, max) : null;

    private static string StringValue(JsonElement value, string path, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(path, "expected a string");
        var text = value.GetString() ?? "";
        if (text.Length < min || text.Length > max)
            throw Invalid(path, $"expected {min} to {max} characters");
        return text;
    }

    private static void RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(path, "expected an object");
    }

    private static void RequireOnly(JsonElement value, string path, params string[] allowed)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (System.Array.IndexOf(allowed, property.Name) < 0)
                throw Invalid(path, $"unrecognized key \"{property.Name}\"");
        }
    }

    private static FormatException Invalid(string path, string message) =>
        new($"Invalid report at {path}: {message}");
}
