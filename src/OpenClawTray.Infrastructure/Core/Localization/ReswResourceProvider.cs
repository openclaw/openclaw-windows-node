using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Production implementation of <see cref="IStringResourceProvider"/> that reads
/// .resw files directly from disk as XML.
///
/// NOTE: This bypasses MRT (Modern Resource Technology) and the Windows PRI
/// resource system. The standard approach would be to use ResourceLoader from
/// Microsoft.global::Windows.ApplicationModel.Resources, which relies on the .resw files
/// being compiled into a .pri (Package Resource Index) at build time. However,
/// ResourceLoader has significant friction in unpackaged app scenarios:
///
///   - ResourceLoader(subtreeName) fails with FileNotFoundException for named
///     .resw files in unpackaged apps, even when the PRI contains the resources.
///   - MRT locale negotiation uses the OS language settings, not the app's
///     explicit locale choice, which conflicts with Reactor's LocaleProvider model
///     where the app controls the active locale at runtime.
///
/// By reading .resw XML directly, we get:
///   - Identical behavior for packaged and unpackaged apps
///   - Locale resolution driven by LocaleProvider, not the OS
///   - No dependency on MRT initialization or PRI packaging
///
/// REVISIT: If Reactor apps move to packaged deployment (MSIX) as the default,
/// or if MRT Core improves unpackaged support, consider switching back to
/// ResourceLoader for better OS integration (e.g., per-language resource packs,
/// system-level font/layout negotiation).
/// </summary>
public sealed class ReswResourceProvider : IStringResourceProvider
{
    // Cache: locale -> namespace -> (key -> value)
    // REVISIT: This loads all keys for a namespace on first access. For apps
    // with very large .resw files, consider lazy or streaming parsing.
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _defaultLocale;
    private readonly string _stringsBasePath;

    /// <summary>
    /// Creates a ReswResourceProvider that reads .resw files from the Strings/
    /// folder relative to the application base directory.
    /// </summary>
    /// <param name="defaultLocale">The default/source locale (e.g., "en-US").</param>
    /// <param name="stringsBasePath">
    /// Absolute path to the Strings folder, or null to auto-detect from the
    /// application base directory. REVISIT: This auto-detection assumes
    /// Strings/ is next to the exe, which may not hold for all deployment layouts.
    /// </param>
    public ReswResourceProvider(string defaultLocale = "en-US", string? stringsBasePath = null)
    {
        _defaultLocale = defaultLocale;
        _stringsBasePath = stringsBasePath
            ?? Path.Combine(AppContext.BaseDirectory, "Strings");
    }

    public string? GetString(string locale, string ns, string key)
    {
        var nsMap = GetOrLoadNamespace(locale, ns);
        if (nsMap is not null && nsMap.TryGetValue(key, out var value))
            return value;

        return null;
    }

    private Dictionary<string, string>? GetOrLoadNamespace(string locale, string ns)
    {
        // Check cache first
        if (_cache.TryGetValue(locale, out var localeMap)
            && localeMap.TryGetValue(ns, out var nsMap))
        {
            return nsMap;
        }

        // Try to load from disk: Strings/{locale}/{ns}.resw
        var reswPath = Path.Combine(_stringsBasePath, locale, $"{ns}.resw");
        if (!File.Exists(reswPath))
        {
            Debug.WriteLine($"[Reactor.Intl] .resw file not found: {reswPath}");
            return null;
        }

        var entries = ParseReswFile(reswPath);
        if (entries is null)
            return null;

        // Store in cache
        if (!_cache.TryGetValue(locale, out localeMap))
        {
            localeMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _cache[locale] = localeMap;
        }
        localeMap[ns] = entries;

        return entries;
    }

    /// <summary>
    /// Parses a .resw XML file into a key-value dictionary.
    /// REVISIT: This duplicates parsing logic from OpenClawTray.Infrastructure.Localization.Generator's
    /// ReswParser. Consider sharing a common parser if the two projects are ever
    /// unified or if the .resw format handling needs to diverge (e.g., supporting
    /// comments or metadata at runtime).
    /// </summary>
    private static Dictionary<string, string>? ParseReswFile(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var data in doc.Descendants("data"))
            {
                var name = data.Attribute("name")?.Value;
                if (name is null) continue;

                var value = data.Element("value")?.Value ?? "";
                entries[name] = value;
            }

            return entries;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Reactor.Intl] Failed to parse .resw file '{path}': {ex.Message}");
            return null;
        }
    }
}
