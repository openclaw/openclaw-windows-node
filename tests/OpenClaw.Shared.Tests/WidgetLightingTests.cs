using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared.Widgets;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class WidgetLightingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "widget-lighting-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("""{"operation":"color","color":"#12ab34"}""", "SET_ALL #12ab34")]
    [InlineData("""{"operation":"preset","preset":"wave","color":"#123456","accentColor":"#abcdef"}""",
        "CREATE_EFFECT wave base_color=#123456 accent_color=#abcdef")]
    [InlineData("""{"operation":"status"}""", "GET_ALL_LAYOUTS")]
    [InlineData("""{"operation":"release"}""", "QUIT")]
    public void LiteralActions_MapToFixedDriverProtocol(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, WidgetLightingAction.Parse(document.RootElement).Command);
    }

    [Theory]
    [InlineData("""{"operation":"color","color":"${state.color}"}""")]
    [InlineData("""{"operation":"color","color":"#123456\nDIAGNOSE"}""")]
    [InlineData("""{"operation":"status","command":"DIAGNOSE"}""")]
    [InlineData("""{"operation":"preset","preset":"script","color":"#123456"}""")]
    [InlineData("""{"operation":"preset","preset":"wave","color":"#123456","layers":"[]"}""")]
    [InlineData("""{"operation":"release","operation":"status"}""")]
    public void LiteralActions_RejectGeneratedCommandsInputsAndAmbiguity(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WidgetLightingAction.Parse(document.RootElement));
    }

    [Fact]
    public void PresentationAdmission_BindsControlsToAdmittedLiteralActionsAndKeepsFeedbackOutOfSourceFacts()
    {
        var card = JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.6", body = new object[]
            {
                new { type = "ActionSet", actions = new[]
                {
                    new { type = "Action.Execute", title = "Blue", verb = "widget.lighting",
                        associatedInputs = "none", data = new { operation = "color", color = "#123456" } }
                } }
            }
        });
        var presentation = new WidgetPresentation(card);
        presentation.Validate();
        Assert.True(WidgetLightingAction.IsDeclared(presentation, new("color", "#123456")));
        Assert.False(WidgetLightingAction.IsDeclared(presentation, new("color", "#ffffff")));
        var snapshot = new WidgetSnapshot("lighting", "Lighting", "Controls", "Driver status is not hardware proof.",
            "https://example.com/driver", DateTimeOffset.UtcNow, Presentation: presentation);
        var initial = new WidgetInstanceState(snapshot.Id, new(), new(WidgetLightingOutcome.Acknowledged));
        var recovered = WidgetInstanceState.Parse(initial.Serialize(), [snapshot]);
        Assert.Equal(WidgetLightingOutcome.Acknowledged, recovered.Lighting!.Outcome);
        Assert.Empty(recovered.Values);
        using var input = JsonDocument.Parse("""{"operation":"color","color":"#123456"}""");
        Assert.Throws<ArgumentException>(() => recovered.Apply(presentation, "widget.lighting", input.RootElement));
        var payload = WidgetCard.Render(snapshot, [snapshot], new("Title", "Empty", "Select", "Show", "Stale", "Source"),
            DateTimeOffset.UtcNow, recovered.Values, "Acknowledged, not verified.");
        using var data = JsonDocument.Parse(payload.Data);
        Assert.Equal("Controls", data.RootElement.GetProperty("value").GetString());
        Assert.Equal("Acknowledged, not verified.", data.RootElement.GetProperty("lighting").GetProperty("message").GetString());
        Assert.Contains("openclaw-lighting-feedback", payload.Template);
        var unsafeCard = card.GetRawText().Replace("\"associatedInputs\":\"none\"", "\"associatedInputs\":\"auto\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new WidgetPresentation(JsonSerializer.Deserialize<JsonElement>(unsafeCard)).Validate());
    }

    [Fact]
    public void Driver_RequiresLocalConsentBeforeProcessLaunchAndRejectsForgedRecordCommands()
    {
        using var driver = new WidgetLightingDriver(_directory, NullLogger.Instance,
            () => throw new Xunit.Sdk.XunitException("A disabled driver must not launch."));
        Assert.Equal(WidgetLightingOutcome.Disabled, driver.Execute(new("status")).Outcome);
        Assert.Throws<ArgumentException>(() => driver.Execute(new("color", "#123456\nDIAGNOSE")));
    }

    [Fact]
    public void Driver_KeepsAnOwnedSessionAliveSerializesCommandsAndReleasesItOnConsentRevocation()
    {
        Consent(true);
        var log = Path.Combine(_directory, "protocol.log");
        using var driver = Driver(log);
        Assert.Equal(new WidgetLightingFeedback(WidgetLightingOutcome.Ready, 1), driver.Execute(new("status")));
        using var process = Process.GetProcessById(int.Parse(File.ReadLines(log).First()["START ".Length..]));
        Assert.False(process.HasExited);
        Assert.Equal(WidgetLightingOutcome.Acknowledged, driver.Execute(new("preset", "#123456", "wave")).Outcome);
        using var competing = Driver(Path.Combine(_directory, "other.log"));
        Assert.Equal(WidgetLightingOutcome.Busy, competing.Execute(new("status")).Outcome);
        Assert.False(File.Exists(Path.Combine(_directory, "other.log")));
        Consent(false);
        driver.ReconcileConsent();
        Assert.True(process.HasExited);
        Assert.Equal(new[] { $"START {process.Id}", "GET_ALL_LAYOUTS", "CREATE_EFFECT wave base_color=#123456", "QUIT" },
            File.ReadAllLines(log));
        Assert.Equal(WidgetLightingOutcome.Disabled, driver.Execute(new("status")).Outcome);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("oversized")]
    [InlineData("quiet")]
    public void Driver_ReportsProtocolFailureAndStopsOnlyItsOwnedProcess(string mode)
    {
        Consent(true);
        var log = Path.Combine(_directory, "failure.log");
        using var driver = Driver(log, mode);
        Assert.Equal(WidgetLightingOutcome.Failed, driver.Execute(new("status")).Outcome);
        var processId = int.Parse(File.ReadLines(log).First()["START ".Length..]);
        Process? process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { process = null; } // The owned process has already left the process table.
        using (process)
            if (process is not null) Assert.True(process.HasExited);
        using var lease = new FileStream(Path.Combine(_directory, "widget-lighting.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    private WidgetLightingDriver Driver(string log, string mode = "normal") => new(_directory, NullLogger.Instance, () =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var executable = Path.Combine(directory.FullName, "tests", "OpenClaw.Shared.TestHost", "bin", configuration,
            "net10.0", OperatingSystem.IsWindows() ? "OpenClaw.Shared.TestHost.exe" : "OpenClaw.Shared.TestHost");
        Assert.True(File.Exists(executable), $"Process fixture must be built: {executable}");
        var start = new ProcessStartInfo(executable);
        start.ArgumentList.Add("--widget-lighting-fixture");
        start.ArgumentList.Add(log);
        start.ArgumentList.Add(mode);
        return start;
    }, TimeSpan.FromSeconds(2));

    private void Consent(bool enabled)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, WidgetLightingDriver.ConsentFileName),
            JsonSerializer.Serialize(new { enabled }, JsonOptions));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
