// The offscreen WebView2 theme probe against real Gateway stylesheets: what it resolves must be what that Gateway's own
// dashboard computed. Recordings come from scripts/Record-ControlUiThemeFixture.ps1.
using System.Text.Json;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared.Themes;
using OpenClaw.TestSupport.Themes;
using OpenClawTray.Presentation.Adapters;
using OpenClawTray.Theming;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayBuiltinThemeProbeTests(UIThreadFixture ui)
{
    private const string TimeoutVariable = "OPENCLAW_UI_THEME_PROBE_TIMEOUT_SECONDS";
    // The first WebView2 in a test process starts the runtime; on CI that alone has taken 30+ s.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutVariable), out var seconds) && seconds > 0 ? seconds : 180);

    // On a page load the dashboard's boot script links the theme file before the core stylesheet, so a core rule of
    // equal specificity wins; a live theme switch appends it after the core, so the theme file wins. The probe, like
    // the table generator, reads the theme file's own declaration. Where the two disagree (2026.11.5-beta.4's dash.css
    // mixes inline code at 5.5% while the core says 5%), the recorded page load differs by one step per channel.
    private static readonly HashSet<string> KnownDashboardOrderDifferences =
    [
        $"{ControlUiThemeRecording.Newer}:dash/Light/InlineCode",
    ];

    [Theory]
    [InlineData(ControlUiThemeRecording.Stable)]
    [InlineData(ControlUiThemeRecording.Older)]
    [InlineData(ControlUiThemeRecording.Newer)]
    public async Task ProbeResolvesWhatTheGatewaysDashboardComputed(string name)
    {
        var recording = ControlUiThemeRecording.Load(name);
        var sheets = await ReadAsync(recording, recording.ThemeIds);

        var probe = ThemeProbe.Parse(await ProbeAsync(ServedThemeStylesheetReader.BuildProbeDocument(sheets), recording.ThemeIds));

        Assert.Empty(probe.Failures);
        var mismatches = new List<string>();
        foreach (var (id, dashboard) in recording.Dashboard)
        {
            foreach (var mode in new[] { ThemeColorMode.Light, ThemeColorMode.Dark })
            {
                var expected = dashboard.For(mode);
                var actual = probe.Result.GetProperty(id).GetProperty(mode == ThemeColorMode.Light ? "light" : "dark");
                foreach (var field in expected.EnumerateObject())
                {
                    var value = actual.GetProperty(field.Name);
                    var token = $"{id}/{mode}/{field.Name}";
                    if (value.GetRawText() != field.Value.GetRawText()
                        && !(KnownDashboardOrderDifferences.Contains($"{name}:{token}")
                            && WithinOneStep(field.Value.GetString(), value.GetString())))
                        mismatches.Add($"{token}: dashboard {field.Value.GetRawText()}, probe {value.GetRawText()}");
                }
                // The page background the dashboard painted is the canvas the Companion paints.
                var palettes = probe.Palettes[id];
                var canvas = (mode == ThemeColorMode.Light ? palettes.Light : palettes.Dark).Canvas.ToHex();
                if (canvas != dashboard.PaintedFor(mode))
                    mismatches.Add($"{id}/{mode} painted {dashboard.PaintedFor(mode)}, canvas {canvas}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public async Task ProbeResolvesANewlyAddedBuiltin_AndFailsAnIdNoStylesheetStyles()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
        var sheets = await ReadAsync(recording, recording.ThemeIds);
        // A built-in a newer Gateway adds ships its own sheet in the same selector shape as tide.css.
        var tide = sheets.Stylesheets.Single(sheet => sheet.Source == "themes/tide.css").Css;
        var aurora = tide.Replace("\"tide-light\"", "\"aurora-light\"").Replace("\"tide\"", "\"aurora\"")
            .Replace("=tide-light]", "=aurora-light]").Replace("=tide]", "=aurora]");
        sheets = sheets with { Stylesheets = [.. sheets.Stylesheets, new ServedStylesheet("themes/aurora.css", aurora, IsThemeSheet: true)] };

        var probe = ThemeProbe.Parse(await ProbeAsync(
            ServedThemeStylesheetReader.BuildProbeDocument(sheets), ["tide", "aurora", "nosuch"]));

        Assert.Equal(probe.Palettes["tide"], probe.Palettes["aurora"]);
        Assert.False(probe.Palettes.ContainsKey("nosuch"));
        Assert.Contains("nosuch/light/unstyled", probe.Failures);
        Assert.Contains("nosuch/dark/unstyled", probe.Failures);
    }

    private static bool WithinOneStep(string? expected, string? actual) =>
        expected is { Length: 7 } && actual is { Length: 7 } && Enumerable.Range(0, 3).All(channel =>
            Math.Abs(Convert.ToInt32(expected.Substring(1 + channel * 2, 2), 16)
                - Convert.ToInt32(actual.Substring(1 + channel * 2, 2), 16)) <= 1);

    private static async Task<ServedThemeStylesheets> ReadAsync(ControlUiThemeRecording recording, IReadOnlyList<string> ids)
    {
        var index = await ServedThemeStylesheetReader.ReadIndexAsync(recording.Read, CancellationToken.None);
        Assert.NotNull(index);
        var sheets = await ServedThemeStylesheetReader.ReadAsync(recording.Read, index, ids, CancellationToken.None);
        Assert.NotNull(sheets);
        return sheets;
    }

    private Task<string> ProbeAsync(string document, IReadOnlyList<string> ids) => ui.RunOnUIAsync(async () =>
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var probe = new WebViewBuiltinThemeProbe(new WinUIDispatcher(DispatcherQueue.GetForCurrentThread()));
        return await probe.ResolveAsync(document, ids, timeout.Token);
    });
}
