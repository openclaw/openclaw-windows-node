using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// Reads one Control UI file from the connected Gateway: <paramref name="path"/> is relative to the Control UI root
/// (<c>""</c> for index.html, <c>themes/tide.css</c>) or root-absolute as index.html serves it (<c>/assets/x.css</c>).
/// Returns null when the file is unavailable, is not <paramref name="mediaType"/>, or exceeds
/// <paramref name="maxBytes"/>. <see cref="IOperatorGatewayClient.ReadControlUiTextAsync"/> is the production reader.
/// </summary>
public delegate Task<string?> ControlUiTextReader(string path, string mediaType, int maxBytes, CancellationToken cancellationToken);

/// <summary>What index.html names: its inline styles and linked stylesheets in document order, and its build id.</summary>
public sealed record ServedControlUiIndex(ImmutableArray<ServedControlUiIndex.Sheet> Stylesheets, string? BuildId)
{
    /// <summary>An inline <c>&lt;style&gt;</c> (<see cref="InlineCss"/>) or a linked <c>assets/</c> stylesheet (<see cref="Href"/>).</summary>
    public sealed record Sheet(string? InlineCss, string? Href);
}

/// <summary>One stylesheet of the probe document, in cascade order.</summary>
public sealed record ServedStylesheet(string Source, string Css, bool IsThemeSheet);

/// <summary>
/// The Control UI stylesheets one Gateway build serves, ready for <see cref="ThemeProbe"/>. <see cref="Key"/> identifies
/// the build: the build id index.html carries (Gateway 2026.9+), else a SHA-256 of every stylesheet, so a retune
/// always changes it and identical CSS shares one cache entry.
/// </summary>
public sealed record ServedThemeStylesheets(
    ImmutableArray<ServedStylesheet> Stylesheets,
    ImmutableArray<string> MissingThemeIds,
    string Key);

/// <summary>
/// Reads the built-in theme stylesheets the connected Gateway's static Control UI handler serves. These are internal
/// build artifacts, not a Gateway contract, so the reader follows what the browser applies rather than fixed names:
/// inline <c>&lt;style&gt;</c> blocks plus <c>stylesheet</c> and <c>preload as=style</c> links under <c>assets/</c>, in
/// document order, then <c>themes/&lt;id&gt;.css</c> for each built-in (claw's palette is the core styles). Anything
/// unexpected returns null so callers fall back to the bundled table.
/// <example><code>
/// var index = await ServedThemeStylesheetReader.ReadIndexAsync(client.ReadControlUiTextAsync, ct);
/// var sheets = index is null ? null : await ServedThemeStylesheetReader.ReadAsync(client.ReadControlUiTextAsync, index, ids, ct);
/// var html = ServedThemeStylesheetReader.BuildProbeDocument(sheets!);
/// </code></example>
/// </summary>
public static partial class ServedThemeStylesheetReader
{
    public const int MaxIndexBytes = 1024 * 1024;
    public const int MaxStylesheetBytes = 4 * 1024 * 1024;
    public const int MaxStylesheets = 32;
    public const int MaxThemeIds = 64;

    [GeneratedRegex("""<style\b[^>]*>(?<css>[\s\S]*?)</style\s*>|<link\b(?<attrs>[^>]*)>""", RegexOptions.IgnoreCase)]
    private static partial Regex StyleOrLink();

    [GeneratedRegex("""(?<name>[a-z-]+)\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    // As built (./assets/x.css) or as the Gateway serves it, rewritten to its mount (/assets/x.css, /console/assets/x.css).
    [GeneratedRegex("""^(?:\./|(?:/[A-Za-z0-9._~-]+)*/)?assets/[A-Za-z0-9._-]+\.css$""")]
    private static partial Regex AssetStylesheet();

    [GeneratedRegex("data-openclaw-control-ui-build-id\\s*=\\s*\"(?<id>[A-Za-z0-9._:+-]{1,256})\"")]
    private static partial Regex BuildIdAttribute();

    public static ServedControlUiIndex ParseIndex(string html)
    {
        var sheets = ImmutableArray.CreateBuilder<ServedControlUiIndex.Sheet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in StyleOrLink().Matches(html))
        {
            if (match.Groups["css"].Success)
            {
                sheets.Add(new ServedControlUiIndex.Sheet(match.Groups["css"].Value, null));
                continue;
            }
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Attribute().Matches(match.Groups["attrs"].Value))
                attributes.TryAdd(attribute.Groups["name"].Value, attribute.Groups["v"].Value);
            var rel = attributes.GetValueOrDefault("rel", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var isStyle = rel.Contains("stylesheet", StringComparer.OrdinalIgnoreCase) ||
                (rel.Contains("preload", StringComparer.OrdinalIgnoreCase) &&
                 attributes.GetValueOrDefault("as", "").Equals("style", StringComparison.OrdinalIgnoreCase));
            var href = attributes.GetValueOrDefault("href", "");
            if (isStyle && AssetStylesheet().IsMatch(href) && seen.Add(href))
                sheets.Add(new ServedControlUiIndex.Sheet(null, href));
        }
        var buildId = BuildIdAttribute().Match(html) is { Success: true } id ? id.Groups["id"].Value : null;
        return new ServedControlUiIndex(sheets.ToImmutable(), buildId);
    }

    /// <summary>Reads and parses index.html; null when the Control UI is not served (disabled, another mount, an auth proxy).</summary>
    public static async Task<ServedControlUiIndex?> ReadIndexAsync(ControlUiTextReader read, CancellationToken cancellationToken)
    {
        var html = await read("", "text/html", MaxIndexBytes, cancellationToken).ConfigureAwait(false);
        if (html is null)
            return null;
        var index = ParseIndex(html);
        // A page without a linked core stylesheet is not the Control UI (for example a proxy's sign-in page).
        return index.Stylesheets.Length is > 0 and <= MaxStylesheets && index.Stylesheets.Any(sheet => sheet.Href is not null)
            ? index
            : null;
    }

    /// <summary>
    /// Fetches every stylesheet <paramref name="index"/> names and <c>themes/&lt;id&gt;.css</c> for each built-in id.
    /// Returns null when a linked stylesheet is unavailable; an absent theme file is reported in
    /// <see cref="ServedThemeStylesheets.MissingThemeIds"/> and that theme fails its probe as unstyled.
    /// </summary>
    public static async Task<ServedThemeStylesheets?> ReadAsync(
        ControlUiTextReader read, ServedControlUiIndex index, IEnumerable<string> builtinThemeIds, CancellationToken cancellationToken)
    {
        var sheets = ImmutableArray.CreateBuilder<ServedStylesheet>();
        foreach (var sheet in index.Stylesheets)
        {
            if (sheet.InlineCss is { } inline)
            {
                sheets.Add(new ServedStylesheet("index.html", inline, IsThemeSheet: false));
                continue;
            }
            var css = await read(sheet.Href!, "text/css", MaxStylesheetBytes, cancellationToken).ConfigureAwait(false);
            if (css is null)
                return null;
            sheets.Add(new ServedStylesheet(sheet.Href!, css, IsThemeSheet: false));
        }

        var missing = ImmutableArray.CreateBuilder<string>();
        var ids = builtinThemeIds
            .Where(id => id != "claw" && GatewayThemeProtocol.IsThemeId(id) && !id.Contains('/'))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxThemeIds);
        foreach (var id in ids)
        {
            var path = $"themes/{id}.css";
            var css = await read(path, "text/css", MaxStylesheetBytes, cancellationToken).ConfigureAwait(false);
            if (css is null)
                missing.Add(id);
            else
                sheets.Add(new ServedStylesheet(path, css, IsThemeSheet: true));
        }
        var built = sheets.ToImmutable();
        return new ServedThemeStylesheets(built, missing.ToImmutable(), index.BuildId ?? "sha256:" + Hash(built));
    }

    /// <summary>
    /// A static document holding only the stylesheets, theme files marked <c>data-openclaw-theme-sheet</c> as
    /// <c>ThemeProbe.js</c> expects. It loads nothing: the host injects the probe script.
    /// </summary>
    public static string BuildProbeDocument(ServedThemeStylesheets sheets)
    {
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\">");
        foreach (var sheet in sheets.Stylesheets)
        {
            html.Append(sheet.IsThemeSheet ? "<style data-openclaw-theme-sheet>" : "<style>")
                .Append(sheet.Css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase))
                .Append("</style>");
        }
        return html.Append("</head><body></body></html>").ToString();
    }

    private static string Hash(ImmutableArray<ServedStylesheet> sheets)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sheet in sheets)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(sheet.Source + "\n" + sheet.Css + "\n"));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
