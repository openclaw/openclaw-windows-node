using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Provides locale-aware message formatting, number/date formatting,
/// and text direction for the active locale. Obtained via UseIntl() hook.
/// </summary>
public sealed class IntlAccessor
{
    private readonly IStringResourceProvider _resourceProvider;
    private readonly MessageCache _messageCache;
    private readonly string _defaultLocale;
    private readonly CultureInfo _culture;
    private readonly bool _pseudoLocalize;
    private readonly Dictionary<string, string> _assetCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();

    public IntlAccessor(
        string locale,
        IStringResourceProvider resourceProvider,
        MessageCache messageCache,
        string defaultLocale = "en-US",
        bool pseudoLocalize = false)
    {
        Locale = locale;
        _resourceProvider = resourceProvider;
        _messageCache = messageCache;
        _defaultLocale = defaultLocale;
        _pseudoLocalize = pseudoLocalize;
        _culture = new CultureInfo(locale);
        Direction = RtlHelper.IsRtlLocale(locale)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    /// <summary>Current locale (e.g., "en-US", "ar-SA").</summary>
    public string Locale { get; }

    /// <summary>Current text direction.</summary>
    public FlowDirection Direction { get; }

    /// <summary>True if the current locale is right-to-left.</summary>
    public bool IsRtl => Direction == FlowDirection.RightToLeft;

    /// <summary>
    /// Loads a string resource by key, then formats it with ICU MessageFormat.
    /// Falls back to the default locale if the key is missing in the current locale.
    /// </summary>
    public string Message(MessageKey key, object? args = null)
    {
        var pattern = ResolvePattern(key);
        if (pattern is null)
            return _pseudoLocalize ? PseudoLocalizer.MissingKeyMarker(key) : $"[?? {key} ??]";

        string result;
        if (args is null)
            result = _messageCache.Format(Locale, pattern);
        else
        {
            var dict = ToArgsDictionary(args);
            result = _messageCache.Format(Locale, pattern, dict);
        }

        return _pseudoLocalize ? PseudoLocalizer.Transform(result) : result;
    }

    /// <summary>
    /// Formats a message that contains rich text tags (e.g., &lt;bold&gt;text&lt;/bold&gt;),
    /// mapping each tag to an element factory. Returns a GroupElement containing the
    /// resulting child elements (text spans + wrapped elements).
    /// </summary>
    /// <remarks>
    /// The .resw value uses XML-like tags: "Click &lt;link&gt;here&lt;/link&gt; to read the &lt;bold&gt;docs&lt;/bold&gt;."
    /// Tags are mapped via the <paramref name="tags"/> dictionary. Unrecognized tags are
    /// rendered as plain text (tag markers stripped). Nested tags are not supported — only
    /// the outermost tag is processed.
    /// </remarks>
    public Element RichMessage(MessageKey key, object? args = null,
        Dictionary<string, Func<string, Element>>? tags = null)
    {
        var pattern = ResolvePattern(key);
        if (pattern is null)
        {
            var marker = _pseudoLocalize ? PseudoLocalizer.MissingKeyMarker(key) : $"[?? {key} ??]";
            return new TextBlockElement(marker);
        }

        string formatted;
        if (args is null)
            formatted = _messageCache.Format(Locale, pattern);
        else
        {
            var dict = ToArgsDictionary(args);
            formatted = _messageCache.Format(Locale, pattern, dict);
        }

        if (_pseudoLocalize)
            formatted = PseudoLocalizer.Transform(formatted);

        if (tags is null || tags.Count == 0)
            return new TextBlockElement(formatted);

        return ParseRichText(formatted, tags);
    }

    /// <summary>
    /// Resolves a locale-qualified asset path. Falls back to the unqualified path
    /// if no locale-specific asset exists.
    /// </summary>
    /// <param name="path">The unqualified asset path (e.g., "Assets/hero-banner.png").</param>
    /// <returns>The locale-qualified path (e.g., "Assets/en-US/hero-banner.png") if it exists,
    /// otherwise the original unqualified path.</returns>
    public string Asset(string path)
    {
        if (_assetCache.TryGetValue(path, out var cached))
            return cached;

        // Build locale-qualified path: insert locale before filename
        // e.g., "Assets/hero-banner.png" -> "Assets/en-US/hero-banner.png"
        var dir = global::System.IO.Path.GetDirectoryName(path) ?? "";
        var fileName = global::System.IO.Path.GetFileName(path);
        var localePath = string.IsNullOrEmpty(dir)
            ? global::System.IO.Path.Combine(Locale, fileName)
            : global::System.IO.Path.Combine(dir, Locale, fileName);

        // Check if locale-specific asset exists
        if (global::System.IO.File.Exists(localePath))
        {
            _assetCache[path] = localePath;
            return localePath;
        }

        // Try base language (e.g., "en" from "en-US")
        var baseLang = Locale.Split('-')[0];
        if (baseLang != Locale)
        {
            var basePath = string.IsNullOrEmpty(dir)
                ? global::System.IO.Path.Combine(baseLang, fileName)
                : global::System.IO.Path.Combine(dir, baseLang, fileName);
            if (global::System.IO.File.Exists(basePath))
            {
                _assetCache[path] = basePath;
                return basePath;
            }
        }

        // Fall back to unqualified path
        _assetCache[path] = path;
        return path;
    }

    /// <summary>
    /// Formats a number for the current locale.
    /// </summary>
    public string FormatNumber(double value, NumberFormatOptions? options = null)
    {
        var style = options?.Style ?? NumberStyle.Default;
        var nfi = (NumberFormatInfo)_culture.NumberFormat.Clone();

        if (options?.MinimumFractionDigits is int min && options?.MaximumFractionDigits is int max)
        {
            var effectiveMin = Math.Min(min, max);
            var effectiveMax = Math.Max(min, max);
            nfi.NumberDecimalDigits = Math.Clamp(nfi.NumberDecimalDigits, effectiveMin, effectiveMax);
        }
        else if (options?.MinimumFractionDigits is int minOnly)
            nfi.NumberDecimalDigits = Math.Max(nfi.NumberDecimalDigits, minOnly);
        else if (options?.MaximumFractionDigits is int maxOnly)
            nfi.NumberDecimalDigits = Math.Min(nfi.NumberDecimalDigits, maxOnly);

        return style switch
        {
            NumberStyle.Currency => value.ToString("C", nfi),
            NumberStyle.Percent => value.ToString("P", nfi),
            _ => value.ToString("N", nfi)
        };
    }

    /// <summary>
    /// Formats a date for the current locale.
    /// </summary>
    public string FormatDate(DateTimeOffset value, DateFormatOptions? options = null)
    {
        var style = options?.Style ?? DateStyle.Default;
        var format = style switch
        {
            DateStyle.Short => "d",   // 1/15/2026
            DateStyle.Long => "D",    // Thursday, January 15, 2026
            DateStyle.Full => "F",    // Thursday, January 15, 2026 2:30:00 PM
            _ => "G"                  // 1/15/2026 2:30:00 PM
        };

        return value.ToString(format, _culture);
    }

    /// <summary>
    /// Formats a list of strings with locale-aware joining (e.g., "A, B, and C").
    /// </summary>
    public string FormatList(IEnumerable<string> values, ListFormatType type = ListFormatType.Conjunction)
    {
        var list = values.ToList();

        if (list.Count == 0) return string.Empty;
        if (list.Count == 1) return list[0];
        if (list.Count == 2)
        {
            var joiner = type == ListFormatType.Conjunction ? GetAndWord() : GetOrWord();
            return $"{list[0]} {joiner} {list[1]}";
        }

        // 3+: "A, B, and C" or "A, B, or C"
        var lastJoiner = type == ListFormatType.Conjunction ? GetAndWord() : GetOrWord();
        var head = string.Join(", ", list.Take(list.Count - 1));
        return $"{head}, {lastJoiner} {list[^1]}";
    }

    // Matches <tagName>content</tagName> — non-greedy, no nesting.
    private static readonly Regex TagPattern = new(@"<(\w+)>(.*?)</\1>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static Element ParseRichText(string formatted, Dictionary<string, Func<string, Element>> tags)
    {
        var elements = new List<Element>();
        int lastIndex = 0;

        foreach (Match match in TagPattern.Matches(formatted))
        {
            // Add plain text before this tag
            if (match.Index > lastIndex)
            {
                elements.Add(new TextBlockElement(formatted[lastIndex..match.Index]));
            }

            var tagName = match.Groups[1].Value;
            var tagContent = match.Groups[2].Value;

            if (tags.TryGetValue(tagName, out var factory))
            {
                elements.Add(factory(tagContent));
            }
            else
            {
                // Unknown tag — render content as plain text
                elements.Add(new TextBlockElement(tagContent));
            }

            lastIndex = match.Index + match.Length;
        }

        // Add trailing plain text
        if (lastIndex < formatted.Length)
        {
            elements.Add(new TextBlockElement(formatted[lastIndex..]));
        }

        // Single element: return it directly. Multiple: wrap in GroupElement.
        return elements.Count == 1 ? elements[0] : new GroupElement(elements.ToArray());
    }

    private string? ResolvePattern(MessageKey key)
    {
        // Try current locale first
        var pattern = _resourceProvider.GetString(Locale, key.Namespace, key.Key);
        if (pattern is not null)
            return pattern;

        // Fallback to default locale
        if (!string.Equals(Locale, _defaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"[Reactor.Intl] Missing key '{key}' for locale '{Locale}', falling back to {_defaultLocale}");
            pattern = _resourceProvider.GetString(_defaultLocale, key.Namespace, key.Key);
            if (pattern is not null)
                return pattern;
        }

        Debug.WriteLine($"[Reactor.Intl] Missing key '{key}' for locale '{Locale}' — no fallback available");
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ToArgsDictionary uses reflection on anonymous types for localization args.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "ToArgsDictionary uses reflection on anonymous types for localization args.")]
    private static IDictionary<string, object> ToArgsDictionary(object args)
    {
        if (args is IDictionary<string, object> dict)
            return dict;

        // Convert anonymous object to dictionary via reflection
        var result = new Dictionary<string, object>();
        var props = _propertyCache.GetOrAdd(args.GetType(),
            t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        foreach (var prop in props)
        {
            var value = prop.GetValue(args);
            if (value is not null)
                result[prop.Name] = value;
        }
        return result;
    }

    /// <summary>
    /// Gets the locale-appropriate "and" conjunction word.
    /// </summary>
    private string GetAndWord()
    {
        var lang = Locale.Split('-')[0].ToLowerInvariant();
        return lang switch
        {
            "es" => "y",
            "fr" => "et",
            "de" => "und",
            "it" => "e",
            "pt" => "e",
            "ja" => "と",
            "ko" => "그리고",
            "zh" => "和",
            "ar" => "و",
            "he" => "ו",
            "ru" => "и",
            "nl" => "en",
            "pl" => "i",
            "tr" => "ve",
            _ => "and"
        };
    }

    /// <summary>
    /// Gets the locale-appropriate "or" disjunction word.
    /// </summary>
    private string GetOrWord()
    {
        var lang = Locale.Split('-')[0].ToLowerInvariant();
        return lang switch
        {
            "es" => "o",
            "fr" => "ou",
            "de" => "oder",
            "it" => "o",
            "pt" => "ou",
            "ja" => "または",
            "ko" => "또는",
            "zh" => "或",
            "ar" => "أو",
            "he" => "או",
            "ru" => "или",
            "nl" => "of",
            "pl" => "lub",
            "tr" => "veya",
            _ => "or"
        };
    }
}
