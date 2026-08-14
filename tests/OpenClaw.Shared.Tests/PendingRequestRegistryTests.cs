using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class PendingRequestRegistryTests
{
    [Fact]
    public void TrackedRegistration_TakePreservesMethodAndHasNoWaiter()
    {
        var registry = OpenRegistry();
        registry.RegisterTracked("tracked-1", "sessions.list");

        Assert.True(registry.TryTake("tracked-1", out var resolution));
        var tracked = Assert.IsType<TrackedRequestResolution>(resolution);
        Assert.Equal("sessions.list", tracked.Method);
        Assert.Equal(PendingRequestCategory.Tracked, tracked.Category);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ChatRegistration_TakeCompletesExactlyOnce()
    {
        var registry = OpenRegistry();
        var registration = registry.RegisterChatSend("chat-1", "chat.send");

        Assert.True(registry.TryTake("chat-1", out var resolution));
        var chat = Assert.IsType<ChatSendRequestResolution>(resolution);
        Assert.Equal("chat.send", chat.Method);
        Assert.Equal(PendingRequestCategory.ChatSend, chat.Category);
        Assert.True(chat.TryComplete(new ChatSendResult { RunId = "run-1" }));
        Assert.False(chat.TryComplete(new ChatSendResult { RunId = "run-2" }));

        Assert.Equal("run-1", (await registration.Task).RunId);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ChatRegistration_TakeFaultsExactlyOnce()
    {
        var registry = OpenRegistry();
        var registration = registry.RegisterChatSend("chat-1", "chat.send");
        registry.TryTake("chat-1", out var resolution);
        var chat = Assert.IsType<ChatSendRequestResolution>(resolution);

        Assert.True(chat.TryFault(new InvalidOperationException("chat failed")));
        Assert.False(chat.TryFault(new InvalidOperationException("second failure")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registration.Task);
        Assert.Equal("chat failed", exception.Message);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task WizardRegistration_TakeCompletesAndFaultsWithTypedResolution()
    {
        var registry = OpenRegistry();
        var completed = registry.RegisterWizard("wizard-1", "wizard.next");
        var faulted = registry.RegisterWizard("wizard-2", "wizard.cancel");

        Assert.True(registry.TryTake("wizard-1", out var completedResolution));
        var completedWizard = Assert.IsType<WizardRequestResolution>(completedResolution);
        Assert.Equal("wizard.next", completedWizard.Method);
        Assert.Equal(PendingRequestCategory.Wizard, completedWizard.Category);
        Assert.True(completedWizard.TryComplete(Json("""{"step":"done"}""")));

        Assert.True(registry.TryTake("wizard-2", out var faultedResolution));
        var faultedWizard = Assert.IsType<WizardRequestResolution>(faultedResolution);
        Assert.Equal("wizard.cancel", faultedWizard.Method);
        Assert.True(faultedWizard.TryFault(new InvalidOperationException("wizard failed")));

        Assert.Equal("done", (await completed.Task).GetProperty("step").GetString());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await faulted.Task);
        Assert.Equal("wizard failed", exception.Message);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ApprovalRegistration_TakeCompletesAndFaultsWithTypedResolution()
    {
        var registry = OpenRegistry();
        var completed = registry.RegisterApproval("approval-1", "exec.approval.resolve");
        var faulted = registry.RegisterApproval("approval-2", "exec.approval.resolve");

        Assert.True(registry.TryTake("approval-1", out var completedResolution));
        var completedApproval = Assert.IsType<ApprovalRequestResolution>(completedResolution);
        Assert.Equal("exec.approval.resolve", completedApproval.Method);
        Assert.Equal(PendingRequestCategory.Approval, completedApproval.Category);
        Assert.True(completedApproval.TryComplete(true));

        Assert.True(registry.TryTake("approval-2", out var faultedResolution));
        var faultedApproval = Assert.IsType<ApprovalRequestResolution>(faultedResolution);
        Assert.True(faultedApproval.TryFault(new InvalidOperationException("approval failed")));

        Assert.True(await completed.Task);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await faulted.Task);
        Assert.Equal("approval failed", exception.Message);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DuplicateRequestId_IsRejectedWithoutReplacingOriginal()
    {
        var registry = OpenRegistry();
        registry.RegisterTracked("same-id", "health");

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterWizard("same-id", "wizard.next"));

        Assert.Contains("already registered", exception.Message);
        Assert.True(registry.TryTake("same-id", out var resolution));
        Assert.Equal("health", Assert.IsType<TrackedRequestResolution>(resolution).Method);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void UnknownAndLateTake_AreNoOps()
    {
        var registry = OpenRegistry();

        Assert.False(registry.TryTake("unknown", out var unknown));
        Assert.Null(unknown);

        registry.RegisterTracked("known", "health");
        Assert.True(registry.TryTake("known", out _));
        Assert.False(registry.TryTake("known", out var late));
        Assert.Null(late);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task StaleHandle_CannotRemoveReusedIdFromNewGeneration()
    {
        var registry = OpenRegistry();
        var stale = registry.RegisterChatSend("reused", "chat.send");
        registry.Drain();
        var drainException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await stale.Task);
        Assert.Equal("Request canceled", drainException.Message);

        registry.OpenConnection();
        var current = registry.RegisterChatSend("reused", "chat.send");

        Assert.False(registry.TryRemove(stale.Handle));
        Assert.True(registry.TryTake("reused", out var resolution));
        Assert.True(
            Assert.IsType<ChatSendRequestResolution>(resolution)
                .TryComplete(new ChatSendResult { RunId = "current" }));
        Assert.Equal("current", (await current.Task).RunId);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ClosedRegistrationIsRejected_AndReopenAcceptsRegistration()
    {
        var registry = new PendingRequestRegistry();

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterTracked("closed", "health"));
        Assert.Equal("Gateway connection is not open", exception.Message);
        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterChatSend("closed-chat", "chat.send"));
        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterWizard("closed-wizard", "wizard.next"));
        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterApproval("closed-approval", "exec.approval.resolve"));

        registry.OpenConnection();
        registry.RegisterTracked("open", "health");
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryTake("open", out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Drain_FaultsEachWaiterExactlyAndClearsTracked_DoubleDrainIsIdempotent()
    {
        var registry = OpenRegistry();
        registry.RegisterTracked("tracked", "health");
        var chat = registry.RegisterChatSend("chat", "chat.send");
        var wizard = registry.RegisterWizard("wizard", "wizard.status");
        var approval = registry.RegisterApproval("approval", "exec.approval.resolve");

        registry.Drain();
        registry.Drain();

        var chatException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await chat.Task);
        var wizardException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await wizard.Task);
        var approvalException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await approval.Task);
        Assert.Equal("Request canceled", chatException.Message);
        Assert.Equal(
            "Gateway connection lost while waiting for wizard response",
            wizardException.Message);
        Assert.Equal(
            "Gateway connection lost before exec.approval.resolve response",
            approvalException.Message);
        Assert.False(registry.TryTake("tracked", out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Drain_WithConnectionLoss_PreservesWizardCloseMetadataOnly()
    {
        var registry = OpenRegistry();
        var chat = registry.RegisterChatSend("chat", "chat.send");
        var wizard = registry.RegisterWizard("wizard", "wizard.status");
        var approval = registry.RegisterApproval("approval", "exec.approval.resolve");
        var connectionLoss = new GatewayConnectionLostException(1012, "service restart");

        registry.Drain(connectionLoss);

        var chatException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await chat.Task);
        var wizardException = await Assert.ThrowsAsync<GatewayConnectionLostException>(
            async () => await wizard.Task);
        var approvalException = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await approval.Task);
        Assert.Equal("Request canceled", chatException.Message);
        Assert.Same(connectionLoss, wizardException);
        Assert.Equal(1012, wizardException.CloseStatusCode);
        Assert.Equal("service restart", wizardException.CloseStatusDescription);
        Assert.Equal(
            "Gateway connection lost before exec.approval.resolve response",
            approvalException.Message);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ResponseVersusDrain_ExactlyOneTerminalOutcomeWins()
    {
        var registry = OpenRegistry();
        var registration = registry.RegisterChatSend("race", "chat.send");
        using var barrier = new Barrier(3);

        var response = Task.Run(() =>
        {
            barrier.SignalAndWait();
            if (!registry.TryTake("race", out var resolution))
            {
                return false;
            }

            return Assert.IsType<ChatSendRequestResolution>(resolution)
                .TryComplete(new ChatSendResult { RunId = "response" });
        });
        var drain = Task.Run(() =>
        {
            barrier.SignalAndWait();
            registry.Drain();
        });

        barrier.SignalAndWait();
        await Task.WhenAll(response, drain);
        var responseWon = await response;

        if (responseWon)
        {
            Assert.Equal("response", (await registration.Task).RunId);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await registration.Task);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ErrorVersusResponse_ExactlyOneTakeAndCompletionWins()
    {
        var registry = OpenRegistry();
        var registration = registry.RegisterApproval("race", "exec.approval.resolve");
        using var barrier = new Barrier(3);

        var response = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return registry.TryTake("race", out var resolution) &&
                   Assert.IsType<ApprovalRequestResolution>(resolution).TryComplete(true);
        });
        var error = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return registry.TryTake("race", out var resolution) &&
                   Assert.IsType<ApprovalRequestResolution>(resolution)
                       .TryFault(new InvalidOperationException("rejected"));
        });

        barrier.SignalAndWait();
        var outcomes = await Task.WhenAll(response, error);
        var responseWon = outcomes[0];
        var errorWon = outcomes[1];

        Assert.NotEqual(responseWon, errorWon);
        if (responseWon)
        {
            Assert.True(await registration.Task);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await registration.Task);
            Assert.Equal("rejected", exception.Message);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task TimeoutRemoveVersusResponse_ExactlyOneIdentityClaimWins()
    {
        var registry = OpenRegistry();
        var registration = registry.RegisterWizard("race", "wizard.status");
        using var barrier = new Barrier(3);

        var timeoutRemove = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return registry.TryRemove(registration.Handle);
        });
        var response = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return registry.TryTake("race", out var resolution) &&
                   Assert.IsType<WizardRequestResolution>(resolution)
                       .TryComplete(Json("""{"won":"response"}"""));
        });

        barrier.SignalAndWait();
        var outcomes = await Task.WhenAll(timeoutRemove, response);
        var timeoutWon = outcomes[0];
        var responseWon = outcomes[1];

        Assert.NotEqual(timeoutWon, responseWon);
        if (responseWon)
        {
            Assert.Equal(
                "response",
                (await registration.Task).GetProperty("won").GetString());
        }
        else
        {
            Assert.False(registration.Task.IsCompleted);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task RegisterVersusDrain_LinearizesWithoutLeak()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var registry = OpenRegistry();
            using var barrier = new Barrier(3);
            PendingRequestRegistration<JsonElement>? registration = null;
            Exception? registrationException = null;

            var register = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    registration = registry.RegisterWizard(
                        $"wizard-{iteration}",
                        "wizard.next");
                }
                catch (InvalidOperationException ex)
                {
                    registrationException = ex;
                }
            });
            var drain = Task.Run(() =>
            {
                barrier.SignalAndWait();
                registry.Drain();
            });

            barrier.SignalAndWait();
            await Task.WhenAll(register, drain);

            Assert.NotEqual(registration.HasValue, registrationException is not null);
            if (registration.HasValue)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(
                    async () => await registration.Value.Task);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(registrationException);
            }

            Assert.Equal(0, registry.Count);
        }
    }

    private static PendingRequestRegistry OpenRegistry()
    {
        var registry = new PendingRequestRegistry();
        registry.OpenConnection();
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
