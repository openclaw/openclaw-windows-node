using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Runtime defense for secrets in the surface data model. Combines two
/// signals: an explicit registry of secret paths (populated when an
/// obscured TextField binds there) and a key-name denylist matching
/// <c>password*</c>, <c>secret*</c>, <c>token*</c> case-insensitively.
/// Used for canvas.a2ui.dump output, action context scoping, and log redaction.
/// </summary>
internal static class SecretRedactor
{
    /// <summary>
    /// Path-segment substring denylist. Matches when a single JSON Pointer
    /// segment contains any of these (case-insensitive). Use substring rather
    /// than prefix so <c>/auth/sessionToken</c> and <c>/loginPassword</c> are
    /// caught alongside the obvious <c>/password</c>. False positives (e.g.
    /// "private" matching "/privateBeta") are preferred to false negatives:
    /// hiding a non-secret leaks nothing, leaking a secret is the failure mode.
    /// </summary>
    private static readonly string[] s_denylist =
    {
        "password",
        "secret",
        "token",
        "apikey",
        "bearer",
        "authorization",
        "pin",
        "otp",
        "mfa",
        "credential",
        "session",
        "cookie",
        "auth",
        "refresh",
        "private",
        "access",
    };

    /// <summary>
    /// True if <paramref name="path"/> is registered as a secret path or any
    /// segment matches the denylist.
    /// </summary>
    public static bool IsSecret(string? path, IReadOnlySet<string> registered)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = Normalize(path);
        if (registered.Contains(normalized)) return true;

        var canonical = CanonicalizeLenientArrayIndices(normalized);
        if (!string.Equals(canonical, normalized, StringComparison.Ordinal)
            && registered.Contains(canonical))
        {
            return true;
        }

        // Any ancestor of the path counts: obscuring "/credentials" should hide "/credentials/password" too.
        foreach (var prefix in registered)
        {
            var normalizedPrefix = Normalize(prefix);
            if (normalizedPrefix.Length == 0 || normalizedPrefix == "/") continue;
            if (IsPathOrDescendant(normalized, normalizedPrefix)) return true;

            var canonicalPrefix = CanonicalizeLenientArrayIndices(normalizedPrefix);
            if (!string.Equals(canonicalPrefix, normalizedPrefix, StringComparison.Ordinal)
                && IsPathOrDescendant(normalized, canonicalPrefix))
            {
                return true;
            }
        }
        return MatchesDenylist(normalized);
    }

    /// <summary>
    /// Deep-clone <paramref name="root"/> with values at registered or
    /// denylisted paths replaced with <c>"[REDACTED]"</c>.
    /// </summary>
    public static JsonNode? Redact(JsonNode? root, IReadOnlySet<string> registered)
    {
        if (root == null) return null;
        if (IsSecret("/", registered)) return JsonValue.Create("[REDACTED]");
        return RedactNode(root.DeepClone(), "/", registered);
    }

    /// <summary>
    /// Same as <see cref="Redact"/>, but mutates the supplied node in place.
    /// Caller is responsible for passing a clone if they want to preserve the original.
    /// </summary>
    public static JsonNode? RedactInPlace(JsonNode? root, IReadOnlySet<string> registered)
    {
        if (root == null) return null;
        if (IsSecret("/", registered)) return JsonValue.Create("[REDACTED]");
        return RedactNode(root, "/", registered);
    }

    public static JsonNode? RedactInPlace(JsonNode? root, string rootPath, IReadOnlySet<string> registered)
    {
        if (root == null) return null;

        var normalizedRootPath = Normalize(rootPath);
        if (IsSecret(normalizedRootPath, registered))
            return JsonValue.Create("[REDACTED]");

        return RedactNode(root, normalizedRootPath, registered);
    }

    private static JsonNode? RedactNode(JsonNode? node, string path, IReadOnlySet<string> registered)
    {
        if (node is JsonObject obj)
        {
            var keys = new List<string>(obj.Count);
            foreach (var kv in obj) keys.Add(kv.Key);
            foreach (var key in keys)
            {
                var childPath = path == "/" ? "/" + EncodeSegment(key) : path + "/" + EncodeSegment(key);
                var current = obj[key];
                if (IsSecret(childPath, registered) || MatchesKey(key))
                {
                    obj[key] = JsonValue.Create("[REDACTED]");
                }
                else
                {
                    var replaced = RedactNode(current, childPath, registered);
                    if (!ReferenceEquals(replaced, current)) obj[key] = replaced;
                }
            }
            return obj;
        }
        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var childPath = path == "/" ? "/" + i : path + "/" + i;
                var current = arr[i];
                // Mirror the object branch: a registered/denylisted element path
                // (e.g. an obscured TextField bound to "/codes/0") must be
                // redacted, not just recursed into - a scalar element would
                // otherwise pass through unchanged and leak via canvas.a2ui.dump.
                if (IsSecret(childPath, registered))
                {
                    arr[i] = JsonValue.Create("[REDACTED]");
                }
                else
                {
                    var replaced = RedactNode(current, childPath, registered);
                    if (!ReferenceEquals(replaced, current)) arr[i] = replaced;
                }
            }
            return arr;
        }
        return node;
    }

    private static bool MatchesDenylist(string path)
    {
        // Walk segments; denylist match on any segment.
        var span = path.AsSpan();
        if (span.Length > 0 && span[0] == '/') span = span.Slice(1);
        while (!span.IsEmpty)
        {
            var slash = span.IndexOf('/');
            var segment = slash < 0 ? span : span.Slice(0, slash);
            if (MatchesSegment(segment)) return true;
            if (slash < 0) break;
            span = span.Slice(slash + 1);
        }
        return false;
    }

    private static bool MatchesKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return MatchesSegment(key.AsSpan());
    }

    private static bool MatchesSegment(ReadOnlySpan<char> segment)
    {
        foreach (var bad in s_denylist)
            if (segment.Contains(bad.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Normalize(string p) =>
        string.IsNullOrEmpty(p) ? "/" : (p[0] == '/' ? p : "/" + p);

    private static bool IsPathOrDescendant(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.Ordinal)
        || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    internal static string CanonicalizeLenientArrayIndices(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/") return normalized;

        var parts = normalized.Substring(1).Split('/');
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var decoded = parts[i].Replace("~1", "/").Replace("~0", "~");
            if (!int.TryParse(
                    decoded,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index)
                || index < 0)
            {
                continue;
            }

            var canonical = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(decoded, canonical, StringComparison.Ordinal))
                continue;

            parts[i] = EncodeSegment(canonical);
            changed = true;
        }

        return changed ? "/" + string.Join("/", parts) : normalized;
    }

    private static string EncodeSegment(string key) =>
        key.Replace("~", "~0").Replace("/", "~1");
}
