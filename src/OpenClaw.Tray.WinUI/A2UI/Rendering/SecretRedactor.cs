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
        // Any ancestor of the path counts: obscuring "/credentials" should hide "/credentials/password" too.
        foreach (var prefix in registered)
        {
            if (prefix.Length == 0 || prefix == "/") continue;
            if (normalized.StartsWith(prefix + "/", StringComparison.Ordinal)) return true;
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
        return RedactNode(root.DeepClone(), "/", registered);
    }

    /// <summary>
    /// Same as <see cref="Redact"/>, but mutates the supplied node in place.
    /// Caller is responsible for passing a clone if they want to preserve the original.
    /// </summary>
    public static JsonNode? RedactInPlace(JsonNode? root, IReadOnlySet<string> registered)
    {
        if (root == null) return null;
        return RedactNode(root, "/", registered);
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
                var replaced = RedactNode(current, childPath, registered);
                if (!ReferenceEquals(replaced, current)) arr[i] = replaced;
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

    private static string EncodeSegment(string key) =>
        key.Replace("~", "~0").Replace("/", "~1");
}
