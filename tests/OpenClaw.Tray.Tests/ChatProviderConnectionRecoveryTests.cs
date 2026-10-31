using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed partial class ChatProviderConnectionSecurityTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("gateway")]
    [InlineData("profile")]
    [InlineData("auth")]
    [InlineData("dispose")]
    public async Task ExplicitRetryCannotCrossItsCanceledAuthorityLifetime(string boundary)
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        using var timeout = new EnvironmentScope("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", "150");
        h.Server.HoldUserProfiles();
        var held = true;
        try
        {
            h.Lifetime.SetClient(null);
            h.Lifetime.SetClient(h.Client);
            await h.PumpUntilAsync(() => h.Lifetime.VerificationState == ChatVerificationState.Failed);
            timeout.Set("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", null);
            Assert.True(h.Lifetime.RetryVerification());
            Assert.False(h.Lifetime.RetryVerification());
            switch (boundary)
            {
                case "disconnect": h.Lifetime.SetClient(null); break;
                case "gateway":
                    h.Registry.AddOrUpdate(new GatewayRecord { Id = "other", Url = "wss://fixture-other.invalid" });
                    h.Registry.SetActive("other");
                    Assert.False(h.Lifetime.RetryVerification());
                    break;
                case "profile":
                    h.Server.SetUserProfile("fixture-retry-new-profile");
                    await h.Server.NotifyProfileIdentityChangedAsync();
                    await h.PumpUntilAsync(() => h.Lifetime.Provider is null);
                    break;
                case "auth": h.Lifetime.ObserveConnection(new GatewayConnectionSnapshot { GatewayId = h.Record.Id, OperatorErrorKind = GatewayErrorKind.Auth }); break;
                case "dispose": h.Lifetime.Dispose(); break;
            }
            h.Drain();
            h.Server.ReleaseUserProfiles();
            held = false;
            await h.Client.SendWizardRequestAsync("health", new { }).WaitAsync(Deadline);
            h.Drain();
            if (boundary == "profile")
            {
                await h.PumpUntilAsync(() => h.IsBound);
                Assert.NotSame(retained, h.Lifetime.Provider);
            }
            else
            {
                Assert.False(h.IsBound);
                Assert.False(h.Lifetime.RetryVerification());
            }
            Assert.DoesNotContain(h.Server.Requests, request => request.Method == "chat.send");
        }
        finally { if (held) h.Server.ReleaseUserProfiles(); }
    }

    [Fact]
    public async Task KnownForeignProfileDiscardsHistoryBeforeItsRosterTimesOut()
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        using var timeout = new EnvironmentScope("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", "150");
        h.Lifetime.SetClient(null);
        h.Server.SetUserProfile("fixture-different-profile-without-push");
        var captured = h.Server.HoldNextSessionList();
        var held = true;
        try
        {
            h.Lifetime.SetClient(h.Client);
            await captured.WaitAsync(Deadline);
            await h.PumpUntilAsync(() => h.Lifetime.Provider is null);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.LoadAsync());
            await h.PumpUntilAsync(() => h.Lifetime.VerificationState == ChatVerificationState.Failed);
            Assert.True(h.Client.IsConnectedToGateway);
            h.Server.ReleaseSessionList();
            held = false;
            timeout.Set("OPENCLAW_CHAT_RECOVERY_TIMEOUT_MS", null);
            Assert.True(h.Lifetime.RetryVerification());
            await h.PumpUntilAsync(() => h.IsBound);
            Assert.NotSame(retained, h.Lifetime.Provider);
        }
        finally { if (held) h.Server.ReleaseSessionList(); }
    }

    [Fact]
    public async Task RejectedInitialChatCanRetryFreshWithoutReusingDiscardedHistory()
    {
        await using var h = await Harness.StartAsync();
        var retained = h.Lifetime.Provider!;
        h.Server.SetUserProfilePayload(JsonSerializer.Deserialize<JsonElement>("{}"));
        await h.Server.NotifyProfileIdentityChangedAsync();
        await h.PumpUntilAsync(() => h.Lifetime.VerificationState == ChatVerificationState.Failed);
        Assert.Null(h.Lifetime.Provider);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.LoadAsync());
        h.Server.SetUserProfilePayload(JsonSerializer.SerializeToElement(new { profile = new { id = "fixture-recovered-fresh-profile" } }));
        Assert.True(h.Lifetime.RetryVerification());
        await h.PumpUntilAsync(() => h.IsBound);
        Assert.NotSame(retained, h.Lifetime.Provider);
        await h.Lifetime.Provider!.LoadAsync();
        await h.Lifetime.Provider.LoadHistoryAsync(GatewayScenario.OtherSessionKey);
        await h.Lifetime.Provider.SendMessageAsync(GatewayScenario.OtherSessionKey, "Fresh profile retry control.");
        Assert.Equal("ok", (await h.Server.WaitForCompletedRequestAsync("chat.send", GatewayScenario.OtherSessionKey).WaitAsync(Deadline)).Outcome);
    }
}
