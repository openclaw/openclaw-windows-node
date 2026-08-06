using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeToolIdentityScreenshotCollection :
    ICollectionFixture<NativeToolIdentityScreenshotFixture>
{
    public const string Name = "Native tool identity screenshot";
}

public sealed class NativeToolIdentityScreenshotFixture : IDisposable
{
    private readonly AccessibilityAppFixture _app = new(initializeAxe: false);

    public IntPtr HubWindowHandle => _app.HubWindowHandle;

    public Task NavigateAsync(string pageTag, string pageMarkerAutomationId) =>
        _app.NavigateAsync(pageTag, pageMarkerAutomationId);

    public string? CaptureNativeChatVisualIfRequested() =>
        _app.CaptureNativeChatVisualIfRequested();

    public void Dispose() => _app.Dispose();
}

[Collection(NativeToolIdentityScreenshotCollection.Name)]
public sealed class NativeToolIdentityScreenshotProofTests
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly NativeToolIdentityScreenshotFixture _app;
    private readonly ITestOutputHelper _output;

    public NativeToolIdentityScreenshotProofTests(
        NativeToolIdentityScreenshotFixture app,
        ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task SyntheticNativeRows_RenderTrustedIdentitySafeInputAndTruthfulFallback()
    {
        await _app.NavigateAsync("chat", "ChatComposerInput");

        var proof = new List<string>
        {
            $"head={Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_HEAD") ?? "local"}",
        };

        ExpandTool("Tool call: Bash, Done", proof);
        ExpandTool("Tool call: Apply Patch, Done", proof);
        ExpandTool("Tool call: Tool, Done", proof);

        var names = WaitForExpectedText();
        Assert.Contains(
            "command: powershell -NoProfile -Command Get-ChildItem .\\src",
            names);
        Assert.Contains(
            "file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs",
            names);
        Assert.Contains("command: [redacted]", names);
        Assert.Contains("Tool input", names);
        Assert.DoesNotContain(
            names,
            name => name.Contains("proof-run-", StringComparison.Ordinal));
        Assert.DoesNotContain(
            names,
            name => name.Contains("super-secret-value", StringComparison.Ordinal));

        proof.Add("UIA header=\"Bash · Done\"");
        proof.Add("UIA input=\"command: powershell -NoProfile -Command Get-ChildItem .\\src\"");
        proof.Add("UIA header=\"Apply Patch · Done\"");
        proof.Add("UIA input=\"file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs\"");
        proof.Add("UIA header=\"Tool · Done\"");
        proof.Add("UIA input=\"command: [redacted]\"");
        proof.Add("forbidden proof-run-=absent");
        proof.Add("forbidden super-secret-value=absent");

        if (_app.CaptureNativeChatVisualIfRequested() is { } screenshotPath)
        {
            proof.Add(
                $"screenshot={Path.GetFileName(screenshotPath)} " +
                $"bytes={new FileInfo(screenshotPath).Length}");
        }

        proof.Add("result=pass");
        foreach (var line in proof)
            _output.WriteLine(line);
        WriteProofArtifactIfRequested(proof);
    }

    private void ExpandTool(string automationName, ICollection<string> proof)
    {
        var element = WaitForElement(new PropertyCondition(
            AutomationElement.NameProperty,
            automationName));
        Assert.True(
            element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawPattern),
            $"{automationName} did not expose ExpandCollapsePattern.");
        var pattern = Assert.IsType<ExpandCollapsePattern>(rawPattern);
        if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            pattern.Expand();
        proof.Add($"UIA expanded=\"{automationName}\"");
    }

    private HashSet<string> WaitForExpectedText()
    {
        HashSet<string>? names = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            names = hub.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Select(element => element.Current.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);
            return names.Contains("Tool input")
                && names.Contains("command: powershell -NoProfile -Command Get-ChildItem .\\src")
                && names.Contains("file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs")
                && names.Contains("command: [redacted]");
        }, "expanded native tool inputs to appear");
        return names!;
    }

    private AutomationElement WaitForElement(Condition condition)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            element = hub.FindFirst(TreeScope.Descendants, condition);
            return element is not null;
        }, "native tool row to appear");
        return element!;
    }

    private static void WaitUntil(Func<bool> predicate, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < UiTimeout)
        {
            try
            {
                if (predicate())
                    return;
            }
            catch (ElementNotAvailableException)
            {
                // React navigation and flyouts replace their automation subtrees.
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static void WriteProofArtifactIfRequested(IEnumerable<string> proof)
    {
        var path = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_ARTIFACT_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = Path.GetFullPath(path, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, proof);
    }
}
