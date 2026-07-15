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

[Collection(AccessibilityCollection.Name)]
public sealed class SessionTitleBehaviorProofTests
{
    private const string RawTitle = "OpenClaw Windows Tray";
    private const string ForkTitle = "OpenClaw Windows Tray (main/fork)";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly AccessibilityAppFixture _app;
    private readonly ITestOutputHelper _output;

    public SessionTitleBehaviorProofTests(
        AccessibilityAppFixture app,
        ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task DuplicateTitles_RenderDistinctly_AndOpenChatUsesOriginalKeys()
    {
        var proof = new List<string>
        {
            $"head={Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_HEAD") ?? "local"}",
            $"input key=agent:main:main displayName=\"{RawTitle}\"",
            $"input key=agent:main:fork displayName=\"{RawTitle}\"",
        };

        await _app.NavigateAsync("sessions", "SessionsPageMarker");
        var observedMainTitle = WaitForTitleCount(RawTitle, expectedCount: 1);
        var observedForkTitle = WaitForTitleCount(ForkTitle, expectedCount: 1);
        proof.Add($"UIA title key=agent:main:main value=\"{observedMainTitle}\"");
        proof.Add($"UIA title key=agent:main:fork value=\"{observedForkTitle}\"");
        if (_app.CaptureHubScreenshotIfRequested() is { } screenshotPath)
        {
            proof.Add(
                $"screenshot={Path.GetFileName(screenshotPath)} " +
                $"bytes={new FileInfo(screenshotPath).Length}");
        }

        AssertOpenChatRoute(RawTitle, "agent:main:main", proof);
        await _app.NavigateAsync("sessions", "SessionsPageMarker");
        _ = WaitForTitleCount(ForkTitle, expectedCount: 1);
        AssertOpenChatRoute(ForkTitle, "agent:main:fork", proof);

        proof.Add("result=pass");
        foreach (var line in proof)
            _output.WriteLine(line);

        WriteProofArtifactIfRequested(proof);
    }

    private void AssertOpenChatRoute(
        string visibleTitle,
        string expectedSessionKey,
        ICollection<string> proof)
    {
        InvokeOpenChat(visibleTitle);
        var routeTitle = $"Route target: {expectedSessionKey}";
        var selectedRouteTitle = WaitForSelectedSession(routeTitle);
        proof.Add(
            $"UIA open-chat title=\"{visibleTitle}\" " +
            $"selectedThread=\"{selectedRouteTitle}\" selectedKey={expectedSessionKey}");
    }

    private string WaitForTitleCount(string title, int expectedCount)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, title));

        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            return hub.FindAll(TreeScope.Descendants, condition).Count == expectedCount;
        }, $"UIA title '{title}' to appear exactly {expectedCount} time(s)");

        var finalHub = AutomationElement.FromHandle(_app.HubWindowHandle);
        var matches = finalHub.FindAll(TreeScope.Descendants, condition);
        Assert.Equal(expectedCount, matches.Count);
        return Assert.Single(
            Enumerable.Range(0, matches.Count)
                .Select(index => matches[index].Current.Name));
    }

    private void InvokeOpenChat(string visibleTitle)
    {
        var titleCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, visibleTitle));
        var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
        var titleElement = hub.FindFirst(TreeScope.Descendants, titleCondition);
        Assert.NotNull(titleElement);

        var row = FindAncestor(titleElement!, ControlType.ListItem);
        Assert.NotNull(row);

        var buttonCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, "Open in chat"));
        var button = row!.FindFirst(TreeScope.Descendants, buttonCondition);
        Assert.NotNull(button);
        Assert.True(button!.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern));
        Assert.IsType<InvokePattern>(pattern).Invoke();
    }

    private string WaitForSelectedSession(string expectedRouteTitle)
    {
        var composerCondition = new PropertyCondition(
            AutomationElement.AutomationIdProperty,
            "ChatComposerInput");
        // The redesigned session selector is a subtle menu-flyout Button (not a
        // ComboBox). Its accessible name folds in the current selection as
        // "Session: <route title>", replacing the legacy ComboBox's
        // SelectionPattern. Match on the route title so the exact field-label
        // prefix/separator format stays free to change.
        var buttonCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.Button);

        string? selectedRouteTitle = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            if (hub.FindFirst(TreeScope.Descendants, composerCondition) is null)
                return false;

            var buttons = hub.FindAll(TreeScope.Descendants, buttonCondition);
            for (var i = 0; i < buttons.Count; i++)
            {
                var name = buttons[i].Current.Name;
                if (!string.IsNullOrEmpty(name)
                    && name.Contains(expectedRouteTitle, StringComparison.Ordinal))
                {
                    selectedRouteTitle = name;
                    return true;
                }
            }

            return false;
        }, $"chat Session selector to choose '{expectedRouteTitle}'");

        return Assert.IsType<string>(selectedRouteTitle);
    }

    private static AutomationElement? FindAncestor(
        AutomationElement element,
        ControlType controlType)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; depth < 12; depth++)
        {
            if (current.Current.ControlType == controlType)
                return current;
            current = walker.GetParent(current);
            if (current is null)
                return null;
        }
        return null;
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
                // Navigation replaces the active automation subtree; retry it.
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for {description} after {UiTimeout.TotalSeconds:0} seconds.");
    }

    private static void WriteProofArtifactIfRequested(IReadOnlyCollection<string> proof)
    {
        var configuredPath = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return;

        var path = Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, proof);
    }
}
