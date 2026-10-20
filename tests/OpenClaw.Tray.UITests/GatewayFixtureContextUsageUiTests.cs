using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ContextFooterReconcilesLiveReplayHistoryAndAuthoritativeUsage()
    {
        var usage = new ContextUsageFixture();
        var scenario = GatewayScenario.CreateContextUsage(usage);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            run.Gateway.HoldHistory(ContextUsageFixture.SessionKey);
            await SelectSessionAsync(run, ContextUsageFixture.Title(ContextUsageListing.Initial),
                ContextUsageFixture.SessionKey, waitForHistory: false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await run.Gateway.WaitForRequestAsync("chat.history", ContextUsageFixture.SessionKey,
                cancellationToken: deadline.Token);

            await SendUsagePromptAsync(run, ContextUsageFixture.FirstPrompt, ContextUsageFixture.FirstReply, 1, "1.0K/5.0K (20%)");
            CaptureUsageFooter(run, "01-first-request.png", "1.0K/5.0K (20%)");
            await SendUsagePromptAsync(run, ContextUsageFixture.SecondPrompt, ContextUsageFixture.FinalReply, 2, "1.5K/5.0K (30%)");
            CaptureUsageFooter(run, "02-repeated-request-and-undated-replay.png", "1.5K/5.0K (30%)");

            // The server has sent both finals and the undated duplicate before this
            // older response. The fixture render acknowledgement proves consumption.
            run.Gateway.ReleaseHistory(ContextUsageFixture.SessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, ContextUsageFixture.SessionKey)
                && IsVisibleInTimeline(run, ContextUsageFixture.FinalReply), "older matching history rendered");
            var historySnapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = ContextUsageFixture.SessionKey });
            var assistants = historySnapshot.GetProperty("selectedTimeline").GetProperty("entries")
                .EnumerateArray().Where(entry => entry.GetProperty("kind").GetString() == "Assistant").ToArray();
            Assert.Equal(2, assistants.Length);
            Assert.Equal(ContextUsageFixture.FirstReply, assistants[0].GetProperty("Text").GetString());
            Assert.Equal(ContextUsageFixture.FinalReply, assistants[1].GetProperty("Text").GetString());
            CaptureUsageFooter(run, "03-older-history-preserves-live-measurement.png", "1.5K/5.0K (30%)");

            await RefreshUsageListingAsync(run, usage, ContextUsageListing.MissingTotal,
                "04-missing-total-does-not-rewind.png", "1.5K/5.0K (30%)");
            await RefreshUsageListingAsync(run, usage, ContextUsageListing.Lower800,
                "05-valid-total-before-ignored-envelope.png", "800/5.0K (16%)");

            usage.SelectListing(ContextUsageListing.Delayed1200);
            var captured = run.Gateway.HoldNextSessionList();
            await run.Gateway.NotifySessionsChangedAsync(deadline.Token);
            await captured.WaitAsync(deadline.Token);
            usage.SelectListing(ContextUsageListing.Lower600);
            await run.Gateway.NotifySessionsChangedAsync(deadline.Token);
            await WaitUsageListingRenderedAsync(run, ContextUsageListing.Lower600);
            await WaitUiAsync(run, () => FindUsageFooter(run, "600/5.0K (12%)") is not null, "newer lower usage rendered");
            CaptureUsageFooter(run, "06-newer-total-lowers-to-600.png", "600/5.0K (12%)");
            var completedListings = run.Gateway.Requests.Count(request => request.Method == "sessions.list");
            run.Gateway.ReleaseSessionList();
            await run.Gateway.WaitForCompletedRequestAsync("sessions.list",
                occurrence: completedListings, cancellationToken: deadline.Token);
            await CaptureSettledUsageListingAsync(run, ContextUsageListing.Delayed1200,
                "07-delayed-listing-cannot-rewind.png", "600/5.0K (12%)");

            Assert.Equal(2, run.Gateway.Requests.Count(request => request.Method == "chat.send" && request.Outcome == "ok"));
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "context-usage-proof.json"),
                JsonSerializer.Serialize(new
                {
                    scenario = scenario.Name, scenario.Sha256,
                    proof = "Synthetic Gateway WebSocket, real client/provider/history loader, mounted native footer",
                    timestamp = ContextUsageFixture.MeasurementTimestamp,
                    olderHistory = new { offset = -1, totalTokens = 1_000, contextPercent = 20 },
                    missingTotal = new { offset = 10, inputTokens = 12, outputTokens = 480, totalTokensFresh = false },
                    accepted800Offset = 5, accepted600Offset = 12, delayed1200Offset = 11,
                    snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = ContextUsageFixture.SessionKey })
                }, new JsonSerializerOptions { WriteIndented = true }));
        }, scenario: scenario);
    }

    private static async Task SendUsagePromptAsync(GatewayFixtureRun run, string prompt, string reply,
        int occurrence, string expectedFooter)
    {
        await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsEnabled == true, "enabled usage composer");
        ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(prompt);
        await WaitUiAsync(run, () => ((ValuePattern)FindById(run, "ChatComposerInput")!
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value == prompt, "exact synthetic composer text");
        await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is
            { Current.IsEnabled: true, Current.Name: "Send" }, "native Send action");
        Invoke(FindById(run, "ChatComposerPrimaryAction")!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await run.Gateway.WaitForCompletedRequestAsync("chat.send", ContextUsageFixture.SessionKey,
            occurrence, deadline.Token);
        await WaitUiAsync(run, () => IsVisibleInTimeline(run, reply)
            && FindUsageFooter(run, expectedFooter) is not null
            && ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern))
                .Current.Value.Length == 0, "completed send, latest request footer and reply");
    }

    private static async Task RefreshUsageListingAsync(GatewayFixtureRun run, ContextUsageFixture usage,
        ContextUsageListing listing, string captureName, string expected)
    {
        var occurrence = run.Gateway.Requests.Count(request => request.Method == "sessions.list") + 1;
        usage.SelectListing(listing);
        await run.Gateway.NotifySessionsChangedAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await run.Gateway.WaitForCompletedRequestAsync("sessions.list",
            occurrence: occurrence, cancellationToken: deadline.Token);
        await CaptureSettledUsageListingAsync(run, listing, captureName, expected);
    }

    private static async Task CaptureSettledUsageListingAsync(GatewayFixtureRun run,
        ContextUsageListing listing, string captureName, string expected)
    {
        // A following sessions.changed is read only after the prior response has
        // completed all client subscribers. Hold its reply so it cannot repair a
        // rewind before the native assertion. This is a wire barrier, not a sleep.
        var occurrence = run.Gateway.Requests.Count(request => request.Method == "sessions.list") + 1;
        var captured = run.Gateway.HoldNextSessionList();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await run.Gateway.NotifySessionsChangedAsync(deadline.Token);
            await captured.WaitAsync(deadline.Token);
            await WaitUsageListingRenderedAsync(run, listing);
            await WaitUiAsync(run, () => FindUsageFooter(run, expected) is not null, $"settled footer {expected}");
            CaptureUsageFooter(run, captureName, expected);
        }
        finally { run.Gateway.ReleaseSessionList(); }
        await run.Gateway.WaitForCompletedRequestAsync("sessions.list",
            occurrence: occurrence, cancellationToken: deadline.Token);
    }

    private static Task WaitUsageListingRenderedAsync(GatewayFixtureRun run, ContextUsageListing listing) =>
        WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{ContextUsageFixture.SessionKey}")?.Current.Name
            == ContextUsageFixture.Title(listing), $"rendered listing {listing}");

    private static AutomationElement? FindUsageFooter(GatewayFixtureRun run, string expected)
    {
        var timeline = FindTimeline(run);
        if (timeline is null) return null;
        return timeline.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().SingleOrDefault(element =>
                !element.Current.IsOffscreen && element.Current.Name.EndsWith(expected, StringComparison.Ordinal)
                && element.Current.Name.Contains("browse", StringComparison.OrdinalIgnoreCase));
    }

    private static void CaptureUsageFooter(GatewayFixtureRun run, string name, string expected)
    {
        var footer = FindUsageFooter(run, expected);
        Assert.NotNull(footer);
        var viewport = FindTimeline(run)!.Current.BoundingRectangle;
        var bounds = footer.Current.BoundingRectangle;
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Top >= viewport.Top &&
            bounds.Bottom <= viewport.Bottom && bounds.Left >= viewport.Left && bounds.Right <= viewport.Right,
            "The asserted usage footer must be visible inside the mounted transcript.");
        Marshal.ThrowExceptionForHR(DwmFlush());
        OwnedWindowCapture.Save(run, FindHub(run), name, footer, FindById(run, "ChatComposerInput")!);
        Assert.NotNull(FindUsageFooter(run, expected));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
