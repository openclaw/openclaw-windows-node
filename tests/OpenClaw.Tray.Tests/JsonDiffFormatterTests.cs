using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class JsonDiffFormatterTests
{
    [Fact]
    public void CreateDiff_WhenArrayItemAdded_DoesNotMarkFollowingItemsAsChanged()
    {
        var original = """
        {
          "items": [
            "alpha",
            "charlie"
          ]
        }
        """;
        var proposed = """
        {
          "items": [
            "alpha",
            "bravo",
            "charlie"
          ]
        }
        """;

        var diff = JsonDiffFormatter.CreateDiff(original, proposed);

        Assert.Contains(diff, line => line.Kind == JsonDiffLineKind.Added && line.Text.Trim() == "\"bravo\",");
        Assert.Contains(diff, line => line.Kind == JsonDiffLineKind.Unchanged && line.Text.Trim() == "\"charlie\"");
        Assert.DoesNotContain(diff, line => line.Kind != JsonDiffLineKind.Unchanged && line.Text.Trim() == "\"charlie\"");
    }

    [Fact]
    public void CreateDiff_WhenArrayItemRemoved_DoesNotMarkFollowingItemsAsChanged()
    {
        var original = """
        {
          "items": [
            "alpha",
            "bravo",
            "charlie"
          ]
        }
        """;
        var proposed = """
        {
          "items": [
            "alpha",
            "charlie"
          ]
        }
        """;

        var diff = JsonDiffFormatter.CreateDiff(original, proposed);

        Assert.Contains(diff, line => line.Kind == JsonDiffLineKind.Removed && line.Text.Trim() == "\"bravo\",");
        Assert.Contains(diff, line => line.Kind == JsonDiffLineKind.Unchanged && line.Text.Trim() == "\"charlie\"");
        Assert.DoesNotContain(diff, line => line.Kind != JsonDiffLineKind.Unchanged && line.Text.Trim() == "\"charlie\"");
    }

    [Fact]
    public void CreateDiff_WhenStringChanged_ProducesInlineChangedSegment()
    {
        var original = """
        {
          "value": "screen.record"
        }
        """;
        var proposed = """
        {
          "value": "screen.snapshot"
        }
        """;

        var diff = JsonDiffFormatter.CreateDiff(original, proposed);
        var removed = Assert.Single(diff, line => line.Kind == JsonDiffLineKind.Removed);
        var added = Assert.Single(diff, line => line.Kind == JsonDiffLineKind.Added);

        Assert.Contains(removed.Segments, segment => segment.IsChanged && segment.Text.Contains("record", StringComparison.Ordinal));
        Assert.Contains(added.Segments, segment => segment.IsChanged && segment.Text.Contains("snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateDiff_WhenNoChanges_ReturnsOnlyUnchangedLines()
    {
        var json = """
        {
          "value": true
        }
        """;

        var diff = JsonDiffFormatter.CreateDiff(json, json);

        Assert.All(diff, line => Assert.Equal(JsonDiffLineKind.Unchanged, line.Kind));
    }
}
