using System.Text;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Allocates stable window ids from window titles. Pure logic — the live
/// <see cref="WindowRegistry"/> owns the Window instance map and calls in here
/// to derive ids on Window.Activated.
///
/// Rules (spec §2.3):
///   1. Slugify the title; if the slug is new and non-empty, take it.
///   2. Otherwise (empty title, or slug collision), fall back to <c>Win1</c>,
///      <c>Win2</c>, … counting from 1.
///   3. Once assigned, an id is reserved forever — a later window with the same
///      title gets a <c>&lt;slug&gt;-2</c>, <c>&lt;slug&gt;-3</c>, … suffix.
/// </summary>
internal sealed class WindowIdAllocator
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private int _anonymousCounter;

    public string Allocate(string? title)
    {
        var slug = Slugify(title);

        if (string.IsNullOrEmpty(slug))
        {
            return Reserve($"Win{++_anonymousCounter}");
        }

        if (!_used.Contains(slug))
            return Reserve(slug);

        // Collision: suffix with -2, -3, …
        for (int n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!_used.Contains(candidate))
                return Reserve(candidate);
        }
    }

    // Exposed to callers (WindowRegistry) that want to pin an explicit id
    // instead of going through the title-based allocator.
    internal string Reserve(string id)
    {
        if (_used.Contains(id))
        {
            // Collision on an explicit id — disambiguate the same way the
            // title path does rather than silently returning the existing
            // handle of a different window.
            for (int n = 2; ; n++)
            {
                var candidate = $"{id}-{n}";
                if (!_used.Contains(candidate))
                {
                    _used.Add(candidate);
                    return candidate;
                }
            }
        }
        _used.Add(id);
        return id;
    }

    internal static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var sb = new StringBuilder(title.Length);
        bool lastDash = false;
        foreach (var raw in title.Trim())
        {
            var ch = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is ' ' or '-' or '_')
            {
                if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            // Drop everything else (punctuation, em dashes, etc.)
        }
        // Trim trailing dash.
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }
}
