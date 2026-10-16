using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SetupHandoffReceiptCompatibilityTests
{
    [Theory]
    [InlineData(SetupNativeDestination.Chat, false)]
    [InlineData(SetupNativeDestination.Chat, true)]
    [InlineData(SetupNativeDestination.WhatsApp, false)]
    [InlineData(SetupNativeDestination.WhatsApp, true)]
    [InlineData(SetupNativeDestination.Telegram, false)]
    [InlineData(SetupNativeDestination.Telegram, true)]
    [InlineData(SetupNativeDestination.Channels, false)]
    [InlineData(SetupNativeDestination.Channels, true)]
    [InlineData(SetupNativeDestination.Skills, false)]
    [InlineData(SetupNativeDestination.Skills, true)]
    public async Task CurrentReaderPreservesKindlessDestinationAndRetainedRetry(
        SetupNativeDestination destination, bool retainedRetry)
    {
        using var directory = new TempDirectory();
        var clock = new ManualTimeProvider();
        var gateway = new GatewayRecord { Id = "compatibility", Url = "wss://gateway.example/control/" };
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            gateway.Id, GatewayDashboardBinding.Capture(gateway), "provider/model", "primary", 4,
            IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main");
        var target = new SetupNativeTarget(destination, proof.SessionKey!);
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(new(proof, target));
        var recovery = new NativeRestartRecoveryStore(directory.Path);
        recovery.Save(handle);
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");

        if (retainedRetry)
        {
            using (var lease = store.Acquire(handle).Lease!)
                lease.RetainForExplicitRetry();
            clock.Advance(TimeSpan.FromMinutes(6));
        }

        // This is a current-reader schema fixture, not evidence from an older signed package.
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        record.Remove("Kind");
        record.Remove("PreparationSession");
        if (!retainedRetry)
        {
            record.Remove("ExecutionStartedUtc");
            record.Remove("ExecutionExpiresUtc");
        }
        File.WriteAllText(path, record.ToJsonString());
        var before = File.ReadAllBytes(path);
        var executionDeadline = record["ExecutionExpiresUtc"]?.GetValue<DateTimeOffset>();
        var reopenedStore = new SetupDashboardHandoffStore(directory.Path, clock);
        var reopenedRecovery = new NativeRestartRecoveryStore(directory.Path);
        var calls = new List<string>();
        var launcher = new SetupNativeHandoffLauncher(() => gateway,
            (receipt, _) =>
            {
                Assert.Equal(proof, receipt);
                calls.Add("verify");
                return Task.FromResult(new SetupVerifiedNativeRoute(proof, proof.SessionKey!));
            },
            (completion, _) =>
            {
                Assert.Equal(target, completion.Target);
                Assert.Equal(proof, completion.Verification);
                if (retainedRetry)
                {
                    var inflight = JsonNode.Parse(File.ReadAllText(path))!;
                    Assert.Equal(executionDeadline, inflight["ExecutionExpiresUtc"]!.GetValue<DateTimeOffset>());
                }
                calls.Add("destination");
                return Task.CompletedTask;
            },
            _ => throw new Exception("Compatible destination receipts must not fail."),
            clock,
            showPreparing: (_, _) => throw new Exception("A kindless destination cannot become preparation."),
            showReady: (_, _) => throw new Exception("A kindless destination cannot mount Ready."),
            readyConsumed: () => throw new Exception("A destination cannot consume a preparation."));

        Assert.Equal(handle, reopenedRecovery.Read());
        if (retainedRetry)
        {
            Assert.False(await launcher.OpenAsync(reopenedStore, handle, restartRecovery: reopenedRecovery));
            Assert.Empty(calls);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(handle, reopenedRecovery.Read());
        }

        Assert.True(await launcher.OpenAsync(reopenedStore, handle,
            explicitRetry: retainedRetry, restartRecovery: reopenedRecovery));
        Assert.Equal(["verify", "destination"], calls);
        Assert.Null(reopenedRecovery.Read());
        Assert.False(File.Exists(path));
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid,
            reopenedStore.Acquire(handle, explicitRetry: true).Status);
    }

    [Fact]
    public void RemovingPreparationKindCannotReinterpretItAsLegacyDestination()
    {
        using var directory = new TempDirectory();
        var gateway = new GatewayRecord { Id = "compatibility", Url = "wss://gateway.example/control/" };
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.CustodianOnboarding,
            gateway.Id, GatewayDashboardBinding.Capture(gateway), "provider/model", "primary", 4,
            IdentityBinding: new string('B', 64), SessionKey: "agent:primary:main");
        var store = new SetupDashboardHandoffStore(directory.Path);
        var handle = store.IssuePreparation(new(proof, proof.SessionKey!));
        var path = Path.Combine(directory.Path, "setup-dashboard-handoff", "pending.json");
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        record.Remove("Kind");
        record.Remove("PreparationSession");
        File.WriteAllText(path, record.ToJsonString());

        var reopened = new SetupDashboardHandoffStore(directory.Path);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, reopened.Acquire(handle).Status);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, reopened.Acquire(handle, explicitRetry: true).Status);
    }
}
