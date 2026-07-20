using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Canonical home for the <em>non-nullable, fallback-returning</em> family of
/// <see cref="JsonElement"/> coercion helpers: the gateway-client-style getters
/// (<c>GetString</c> returning <c>null</c> when absent, numeric getters returning
/// a caller-supplied fallback, <c>GetArrayLength</c>, and a verbatim
/// <c>FirstNonEmpty</c>) plus the defensive non-object guard that the
/// <c>GetStringSafe</c> variant added. See docs/ARCHITECTURE.md →
/// <c>json-read-helpers</c>.
///
/// All accessors are <em>total</em> (they never throw): a missing property, a
/// JSON null, a value of the wrong <see cref="JsonValueKind"/>, or a non-object
/// <paramref name="parent"/> yields the documented fallback (<c>null</c> / 0 /
/// the caller-supplied fallback) rather than an exception — including a non-object
/// element, which <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
/// would otherwise throw on.
///
/// <para><b>Scope — do not blindly migrate.</b> This is deliberately NOT a
/// drop-in for every private JSON helper in the repo. Several existing helpers
/// have different contracts; routing them through these methods changes behavior
/// and needs a call-site guard or a dedicated variant:</para>
/// <list type="bullet">
/// <item><c>Models.GetInt</c> / <c>Models.GetLong</c> return <c>int?</c> /
/// <c>long?</c> (a null sentinel means "missing"), and <c>Models.GetLong</c> has
/// no double-to-long fallback — the canonical <see cref="GetLong"/> instead
/// truncates a fractional number.</item>
/// <item><c>WorkspaceFilesModel.GetLong</c> rejects negatives (requires
/// <c>&gt;= 0</c>); the canonical getters accept negative numbers.</item>
/// <item><c>WindowsNodeClient.TryGetString</c> treats empty/whitespace as
/// <em>absent</em>; the canonical <see cref="TryGetString"/> returns <c>true</c>
/// with an empty string ("" is present).</item>
/// <item><c>Mxc.MxcAvailability.FirstNonEmpty</c> trims its result, and
/// <c>SetupSteps.FirstNonEmpty</c> trims and falls back to <c>"no output"</c>;
/// the canonical <see cref="FirstNonEmpty"/> returns the first value verbatim, or
/// <c>null</c>.</item>
/// <item>Some older getters throw on a non-object <paramref name="parent"/>
/// (failing the parse); the canonical getters are total and return the fallback.</item>
/// </list>
/// </summary>
internal static class JsonReadHelpers
{
    /// <summary>
    /// Returns the string value of <paramref name="property"/>, or <c>null</c>
    /// when <paramref name="parent"/> is not an object, the property is absent, or
    /// the value is not a JSON string. The returned string may be empty; only
    /// missing / non-string values collapse to <c>null</c>.
    /// </summary>
    internal static string? GetString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    /// <summary>
    /// Try-pattern companion to <see cref="GetString"/>. Returns <c>true</c> and
    /// sets <paramref name="value"/> when the property exists and is a JSON string
    /// (the string may be empty); otherwise returns <c>false</c> with
    /// <paramref name="value"/> set to <c>null</c>.
    /// </summary>
    internal static bool TryGetString(JsonElement parent, string property, [NotNullWhen(true)] out string? value)
    {
        value = GetString(parent, property);
        return value is not null;
    }

    /// <summary>
    /// Returns the <see cref="int"/> value of <paramref name="property"/>, or
    /// <paramref name="fallback"/> when it is missing or not a JSON number. A
    /// number within <see cref="long"/> range that exceeds the <see cref="int"/>
    /// range is clamped to the <see cref="int"/> range; a number beyond
    /// <see cref="long"/> range returns <paramref name="fallback"/> (matching the
    /// prior private helper).
    /// </summary>
    internal static int GetInt(JsonElement parent, string property, int fallback = 0)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        if (value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetInt64(out var longValue))
        {
            return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
        }

        return fallback;
    }

    /// <summary>
    /// Returns the <see cref="long"/> value of <paramref name="property"/>, or
    /// <paramref name="fallback"/> when it is missing or not a JSON number. A
    /// fractional number is truncated toward zero.
    /// </summary>
    internal static long GetLong(JsonElement parent, string property, long fallback = 0)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        if (value.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetDouble(out var doubleValue))
        {
            return (long)doubleValue;
        }

        return fallback;
    }

    /// <summary>
    /// Returns the <see cref="double"/> value of <paramref name="property"/>, or
    /// <paramref name="fallback"/> when it is missing or not a JSON number.
    /// </summary>
    internal static double GetDouble(JsonElement parent, string property, double fallback = 0)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return value.TryGetDouble(out var doubleValue) ? doubleValue : fallback;
    }

    /// <summary>
    /// Returns the number of elements in the array at <paramref name="property"/>,
    /// or 0 when it is missing or not a JSON array.
    /// </summary>
    internal static int GetArrayLength(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return value.GetArrayLength();
    }

    /// <summary>
    /// Returns the first value that is not <c>null</c>, empty, or whitespace, or
    /// <c>null</c> when none qualify. Values are returned verbatim (not trimmed).
    /// </summary>
    internal static string? FirstNonEmpty(params string?[] values)
    {
        // A bare `null` argument binds to the params array itself rather than a
        // one-element array; guard so the canonical helper never throws.
        if (values is null)
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
