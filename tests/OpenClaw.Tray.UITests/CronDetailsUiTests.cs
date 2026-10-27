using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task CronDetails_JobScratchConflictAndRecordedTranscriptUseProductionRequests()
    {
        var scenario = GatewayScenario.CreateCronManagement();
        await WithAppAsync(async run =>
        {
            await OpenCronPageAsync(run);
            await EditCronAsync(run, "listed");
            Invoke(CronControl(run, "FormCancelButton"));
            Invoke(CronControl(run, "CronDetails:listed"));
            await WaitUiAsync(run, () => DetailsText(run)?.Contains("\"id\": \"listed\"", StringComparison.Ordinal) == true,
                "Gateway job details");
            await CaptureCronDetailsAsync(run, "cron-job-details.png");
            CloseCronDialog(run);
            await WaitUiAsync(run, () => FindCronControl(run, "CronDetailsText") is null, "closed details");

            Invoke(CronControl(run, "CronScratch:listed"));
            await WaitUiAsync(run, () => FindButton(run, "Save changes")?.Current.IsEnabled == true,
                "loaded private scratch");
            SetCronText(run, "CronDetailsText", "Synthetic private state");
            Invoke(FindButton(run, "Save changes")!);
            await WaitUiAsync(run, () => FindCronControl(run, "CronDetailsText") is null, "accepted scratch save");
            Invoke(CronControl(run, "CronScratch:listed"));
            await WaitUiAsync(run, () => DetailsText(run) == "Synthetic private state", "persisted private state");
            SetCronText(run, "CronDetailsText", "Draft retained after conflict");
            // A second authenticated real client updates the same fixture job, not the app's draft.
            var registry = new OpenClaw.Connection.GatewayRegistry(run.Profile.DataDirectory);
            registry.Load();
            using (var concurrent = new OpenClaw.Shared.OpenClawGatewayClient(run.Gateway.Endpoint.ToString(),
                registry.GetActive()!.SharedGatewayToken!, identityPath: Path.Combine(run.Profile.RunDirectory, "cron-second-client")))
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                concurrent.HandshakeSucceeded += (_, _) => ready.TrySetResult();
                await concurrent.ConnectAsync();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var epoch = concurrent.SessionMutationConnectionEpoch!.Value;
                Assert.True((await concurrent.SetCronScratchAsync("listed", "Concurrent state", 1, epoch)).Ok);
                await concurrent.DisconnectAsync();
            }
            Invoke(FindButton(run, "Save changes")!);
            await WaitUiAsync(run, () => FindText(run,
                "Private scratch changed on the Gateway. Your draft is preserved here. Copy it, close this dialog and reopen to review the latest version before saving.") is not null,
                "visible scratch revision conflict");
            Assert.Equal("Draft retained after conflict", DetailsText(run));
            Assert.False(FindButton(run, "Save changes")!.Current.IsEnabled);
            await CaptureCronDetailsAsync(run, "cron-scratch-conflict.png");
            CloseCronDialog(run);
            await WaitUiAsync(run, () => FindCronControl(run, "CronDetailsText") is null, "closed scratch conflict");
            Invoke(CronControl(run, "CronScratch:listed"));
            await WaitUiAsync(run, () => DetailsText(run) == "Concurrent state", "latest scratch after reopen");
            CloseCronDialog(run);
            await WaitUiAsync(run, () => FindCronControl(run, "CronDetailsText") is null, "closed scratch");

            Invoke(CronControl(run, "CronHistory:listed"));
            await WaitUiAsync(run, () => FindCronControl(run, "CronTranscript:listed") is not null, "run transcript action");
            Invoke(CronControl(run, "CronTranscript:listed"));
            await WaitUiAsync(run, () => DetailsText(run)?.Contains("Synthetic cron request.", StringComparison.Ordinal) == true,
                "first exact recorded transcript page");
            await CaptureCronDetailsAsync(run, "cron-transcript-first.png");
            Invoke(FindButton(run, "Next transcript page")!);
            await WaitUiAsync(run, () => DetailsText(run)?.Contains("Synthetic cron response.", StringComparison.Ordinal) == true,
                "next recorded transcript page");
            Assert.False(FindButton(run, "Next transcript page")!.Current.IsEnabled);
            await CaptureCronDetailsAsync(run, "cron-transcript-last.png");
            CloseCronDialog(run);
            Assert.Contains(run.Gateway.Requests, request => request.Method == "cron.get" && request.Outcome == "ok");
            Assert.Equal(2, run.Gateway.Requests.Count(request => request.Method == "cron.history" && request.Outcome == "ok"));
            Assert.Empty(run.Gateway.UnexpectedRequests);
        }, scenario: scenario);
    }

    private static string? DetailsText(GatewayFixtureRun run) =>
        FindCronControl(run, "CronDetailsText") is { } text
            ? ((ValuePattern)text.GetCurrentPattern(ValuePattern.Pattern)).Current.Value : null;

    private static void CloseCronDialog(GatewayFixtureRun run) => Invoke(FindButton(run, "Close")!);

    private static Task CaptureCronDetailsAsync(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            OwnedWindowCapture.Save(run, CronWindow(run), name, CronControl(run, "CronDetailsText"));
        return Task.CompletedTask;
    }
}
