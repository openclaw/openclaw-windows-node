using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Themes;

namespace OpenClaw.TestSupport.Themes;

/// <summary>
/// A real Gateway's Control UI theme files and its dashboard's own resolved tokens, recorded from a running Gateway by
/// <c>scripts/Record-ControlUiThemeFixture.ps1</c> (see ControlUiRecordings/README.md). <see cref="Read"/> serves the
/// recorded files to <see cref="ServedThemeStylesheetReader"/> exactly as the Gateway served them; <see cref="Dashboard"/>
/// is what the dashboard page computed for each built-in and mode, the oracle for runtime resolution.
/// <example><code>
/// var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
/// var index = await ServedThemeStylesheetReader.ReadIndexAsync(recording.Read, ct);
/// Assert.Equal(recording.Dashboard["tide"].Dark.GetProperty("Canvas").GetString(), resolvedHex);
/// </code></example>
/// </summary>
public sealed class ControlUiThemeRecording
{
    /// <summary>The stable packaged Gateway release when recorded.</summary>
    public const string Stable = "gateway-2026.11.4";
    /// <summary>An older release whose stylesheets predate the composer-surface and dark content tokens.</summary>
    public const string Older = "gateway-2026.9.7";
    /// <summary>A newer prerelease whose retuned surfaces differ from the bundled table.</summary>
    public const string Newer = "gateway-2026.11.5-beta.4";

    private readonly ImmutableDictionary<string, string> _files;

    private ControlUiThemeRecording(string name, ImmutableDictionary<string, string> files)
    {
        Name = name;
        _files = files;
        using var document = JsonDocument.Parse(files["dashboard.json"]);
        var recordedFrom = document.RootElement.GetProperty("recordedFrom").GetProperty("buildId");
        BuildId = recordedFrom.ValueKind == JsonValueKind.String ? recordedFrom.GetString() : null;
        Dashboard = document.RootElement.GetProperty("themes").EnumerateObject().ToImmutableDictionary(
            theme => theme.Name,
            theme => new DashboardTheme(
                theme.Value.GetProperty("light").Clone(),
                theme.Value.GetProperty("dark").Clone(),
                theme.Value.GetProperty("painted").GetProperty("light").GetString()!,
                theme.Value.GetProperty("painted").GetProperty("dark").GetString()!),
            StringComparer.Ordinal);
    }

    public string Name { get; }

    /// <summary>The <c>data-openclaw-control-ui-build-id</c> the Gateway served, or null for releases without one.</summary>
    public string? BuildId { get; }

    /// <summary>Per built-in: the palettes the dashboard page resolved, and the page background it painted per mode.</summary>
    public ImmutableDictionary<string, DashboardTheme> Dashboard { get; }

    /// <summary>The recorded built-in ids.</summary>
    public IReadOnlyList<string> ThemeIds => [.. Dashboard.Keys];

    /// <summary>The paths <see cref="Read"/> was asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    public static ControlUiThemeRecording Load(string name)
    {
        var resource = $"OpenClaw.TestSupport.Themes.ControlUiRecordings.{name}.zip";
        using var stream = typeof(ControlUiThemeRecording).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"No recording {resource}.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(entry => entry.Length > 0))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[entry.FullName.Replace('\\', '/')] = reader.ReadToEnd();
        }
        return new ControlUiThemeRecording(name, files.ToImmutable());
    }

    /// <summary>
    /// Serves a recorded file at a Control UI path (relative, or root-absolute as index.html links it) when asked for the
    /// media type the Gateway sent; anything not recorded is unavailable.
    /// </summary>
    public Task<string?> Read(string path, string mediaType, int maxBytes, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(path);
        var key = path.Length == 0 ? "index.html" : path.TrimStart('.', '/');
        var assets = key.IndexOf("assets/", StringComparison.Ordinal);
        if (assets > 0)
            key = key[assets..];
        var type = key.EndsWith(".html", StringComparison.Ordinal) ? "text/html" : "text/css";
        return Task.FromResult(_files.TryGetValue(key, out var text) && type == mediaType && Encoding.UTF8.GetByteCount(text) <= maxBytes
            ? text
            : null);
    }

    /// <summary>One built-in as the dashboard computed it.</summary>
    public sealed record DashboardTheme(JsonElement Light, JsonElement Dark, string PaintedLight, string PaintedDark)
    {
        public JsonElement For(ThemeColorMode mode) => mode == ThemeColorMode.Light ? Light : Dark;
        public string PaintedFor(ThemeColorMode mode) => mode == ThemeColorMode.Light ? PaintedLight : PaintedDark;
    }
}
