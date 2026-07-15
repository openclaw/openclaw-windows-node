using System.Text.Json;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for <see cref="JsonReadHelpers"/> — the canonical, total
/// <see cref="JsonElement"/> coercion helpers. Covers missing property, null
/// value, wrong JSON kind, valid values, numeric clamp/truncation, empty arrays,
/// non-object parents, and <c>FirstNonEmpty</c> selection.
/// </summary>
public class JsonReadHelpersTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // Clone so the element stays valid after the JsonDocument is disposed.
        return doc.RootElement.Clone();
    }

    // ── GetString ───────────────────────────────────────────────────

    /// <summary>Guard test referenced by the architecture ledger (json-read-helpers).</summary>
    [Fact]
    public void GetString_ReturnsNull_WhenPropertyMissing()
    {
        var el = Parse("""{ "a": "x" }""");
        Assert.Null(JsonReadHelpers.GetString(el, "missing"));
    }

    [Fact]
    public void GetString_ReturnsNull_ForJsonNullValue()
    {
        var el = Parse("""{ "a": null }""");
        Assert.Null(JsonReadHelpers.GetString(el, "a"));
    }

    [Theory]
    [InlineData("""{ "a": 42 }""")]
    [InlineData("""{ "a": true }""")]
    [InlineData("""{ "a": [1, 2] }""")]
    [InlineData("""{ "a": { "b": 1 } }""")]
    public void GetString_ReturnsNull_ForWrongKind(string json)
    {
        var el = Parse(json);
        Assert.Null(JsonReadHelpers.GetString(el, "a"));
    }

    [Fact]
    public void GetString_ReturnsValue_ForString()
    {
        var el = Parse("""{ "a": "hello" }""");
        Assert.Equal("hello", JsonReadHelpers.GetString(el, "a"));
    }

    [Fact]
    public void GetString_ReturnsEmptyString_ForEmptyStringValue()
    {
        // An empty string is a valid string; only missing/non-string collapses to null.
        var el = Parse("""{ "a": "" }""");
        Assert.Equal("", JsonReadHelpers.GetString(el, "a"));
    }

    [Fact]
    public void GetString_ReturnsNull_ForNonObjectParent()
    {
        var arr = Parse("""[1, 2, 3]""");
        Assert.Null(JsonReadHelpers.GetString(arr, "a"));
    }

    // ── TryGetString ────────────────────────────────────────────────

    [Fact]
    public void TryGetString_ReturnsTrueAndValue_ForString()
    {
        var el = Parse("""{ "a": "hello" }""");
        Assert.True(JsonReadHelpers.TryGetString(el, "a", out var value));
        Assert.Equal("hello", value);
    }

    [Fact]
    public void TryGetString_ReturnsTrue_ForEmptyString()
    {
        var el = Parse("""{ "a": "" }""");
        Assert.True(JsonReadHelpers.TryGetString(el, "a", out var value));
        Assert.Equal("", value);
    }

    [Theory]
    [InlineData("""{ "a": 1 }""")]
    [InlineData("""{ "a": null }""")]
    [InlineData("""{ "b": "x" }""")]
    public void TryGetString_ReturnsFalseAndNull_ForMissingOrWrongKind(string json)
    {
        var el = Parse(json);
        Assert.False(JsonReadHelpers.TryGetString(el, "a", out var value));
        Assert.Null(value);
    }

    // ── GetInt ──────────────────────────────────────────────────────

    [Fact]
    public void GetInt_ReturnsValue_ForNumber()
    {
        var el = Parse("""{ "a": 7 }""");
        Assert.Equal(7, JsonReadHelpers.GetInt(el, "a"));
    }

    [Fact]
    public void GetInt_ReturnsZero_ForMissingByDefault()
    {
        var el = Parse("""{ "a": 1 }""");
        Assert.Equal(0, JsonReadHelpers.GetInt(el, "missing"));
    }

    [Fact]
    public void GetInt_ReturnsFallback_ForMissing()
    {
        var el = Parse("""{ "a": 1 }""");
        Assert.Equal(-1, JsonReadHelpers.GetInt(el, "missing", fallback: -1));
    }

    [Theory]
    [InlineData("""{ "a": "7" }""")]
    [InlineData("""{ "a": true }""")]
    [InlineData("""{ "a": null }""")]
    public void GetInt_ReturnsFallback_ForWrongKind(string json)
    {
        var el = Parse(json);
        Assert.Equal(99, JsonReadHelpers.GetInt(el, "a", fallback: 99));
    }

    [Fact]
    public void GetInt_ClampsOutOfRangeToIntMax()
    {
        var el = Parse("""{ "a": 9999999999 }"""); // > int.MaxValue
        Assert.Equal(int.MaxValue, JsonReadHelpers.GetInt(el, "a"));
    }

    [Fact]
    public void GetInt_ClampsOutOfRangeToIntMin()
    {
        var el = Parse("""{ "a": -9999999999 }"""); // < int.MinValue
        Assert.Equal(int.MinValue, JsonReadHelpers.GetInt(el, "a"));
    }

    [Fact]
    public void GetInt_ReturnsFallback_ForNonObjectParent()
    {
        var arr = Parse("""[1, 2]""");
        Assert.Equal(3, JsonReadHelpers.GetInt(arr, "a", fallback: 3));
    }

    // ── GetLong ─────────────────────────────────────────────────────

    [Fact]
    public void GetLong_ReturnsValue_ForLargeNumber()
    {
        var el = Parse("""{ "a": 9999999999 }""");
        Assert.Equal(9999999999L, JsonReadHelpers.GetLong(el, "a"));
    }

    [Fact]
    public void GetLong_TruncatesFractionalNumber()
    {
        var el = Parse("""{ "a": 3.9 }""");
        Assert.Equal(3L, JsonReadHelpers.GetLong(el, "a"));
    }

    [Fact]
    public void GetLong_ReturnsFallback_ForMissing()
    {
        var el = Parse("""{ "a": 1 }""");
        Assert.Equal(5L, JsonReadHelpers.GetLong(el, "missing", fallback: 5));
    }

    // ── GetDouble ───────────────────────────────────────────────────

    [Fact]
    public void GetDouble_ReturnsValue_ForFractionalNumber()
    {
        var el = Parse("""{ "a": 2.5 }""");
        Assert.Equal(2.5, JsonReadHelpers.GetDouble(el, "a"));
    }

    [Fact]
    public void GetDouble_ReturnsValue_ForIntegerNumber()
    {
        var el = Parse("""{ "a": 4 }""");
        Assert.Equal(4.0, JsonReadHelpers.GetDouble(el, "a"));
    }

    [Fact]
    public void GetDouble_ReturnsFallback_ForWrongKind()
    {
        var el = Parse("""{ "a": "2.5" }""");
        Assert.Equal(1.5, JsonReadHelpers.GetDouble(el, "a", fallback: 1.5));
    }

    // ── GetArrayLength ──────────────────────────────────────────────

    [Fact]
    public void GetArrayLength_ReturnsCount_ForArray()
    {
        var el = Parse("""{ "a": [1, 2, 3] }""");
        Assert.Equal(3, JsonReadHelpers.GetArrayLength(el, "a"));
    }

    [Fact]
    public void GetArrayLength_ReturnsZero_ForEmptyArray()
    {
        var el = Parse("""{ "a": [] }""");
        Assert.Equal(0, JsonReadHelpers.GetArrayLength(el, "a"));
    }

    [Theory]
    [InlineData("""{ "a": 5 }""")]
    [InlineData("""{ "a": "x" }""")]
    [InlineData("""{ "b": [1] }""")]
    public void GetArrayLength_ReturnsZero_ForMissingOrWrongKind(string json)
    {
        var el = Parse(json);
        Assert.Equal(0, JsonReadHelpers.GetArrayLength(el, "a"));
    }

    // ── FirstNonEmpty ───────────────────────────────────────────────

    [Fact]
    public void FirstNonEmpty_ReturnsFirstNonEmpty()
    {
        Assert.Equal("second", JsonReadHelpers.FirstNonEmpty(null, "", "second", "third"));
    }

    [Fact]
    public void FirstNonEmpty_SkipsWhitespaceOnly()
    {
        Assert.Equal("real", JsonReadHelpers.FirstNonEmpty("   ", "\t", "real"));
    }

    [Fact]
    public void FirstNonEmpty_ReturnsValueVerbatim_WithoutTrimming()
    {
        Assert.Equal("  padded  ", JsonReadHelpers.FirstNonEmpty(null, "  padded  "));
    }

    [Fact]
    public void FirstNonEmpty_ReturnsNull_WhenAllEmpty()
    {
        Assert.Null(JsonReadHelpers.FirstNonEmpty(null, "", "   "));
    }

    [Fact]
    public void FirstNonEmpty_ReturnsNull_ForNoArguments()
    {
        Assert.Null(JsonReadHelpers.FirstNonEmpty());
    }

    [Fact]
    public void FirstNonEmpty_ReturnsNull_ForNullArray()
    {
        // A single bare null binds to the params array itself; the canonical
        // helper is hardened to return null rather than throw.
        Assert.Null(JsonReadHelpers.FirstNonEmpty(null!));
    }
}
