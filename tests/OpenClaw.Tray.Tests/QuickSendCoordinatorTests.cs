using OpenClawTray.Dialogs;
using System;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug #3 (manual test 2026-05-05): QuickSend dialog used to capture the App's
/// gateway client at constructor time and continue sending against it after
/// the App swapped in a freshly-paired instance — producing a spurious
/// "copy pair command to clipboard" toast against a perfectly paired live
/// client. These tests cover the per-Send resolver behavior and the
/// null/disposed lifetime contract (RubberDucky closure conditions #2 + #3).
/// </summary>
public class QuickSendCoordinatorTests
{
    private sealed class FakeGateway : IQuickSendGateway
    {
        public string Name { get; }
        public bool IsConnectedToGateway { get; set; } = true;
        public int ConnectCount;
        public int SendCount;
        public string? LastSent;
        public Exception? SendThrows;

        public string PairingCommands { get; set; } = "PAIR-COMMANDS-DEFAULT";
        public string MissingScopeCommandsTemplate { get; set; } = "SCOPE-COMMANDS-FOR:{0}";

        public FakeGateway(string name = "fake") { Name = name; }

        public Task ConnectAsync()
        {
            ConnectCount++;
            IsConnectedToGateway = true;
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(string message)
        {
            SendCount++;
            LastSent = message;
            if (SendThrows != null) throw SendThrows;
            return Task.CompletedTask;
        }

        public string BuildPairingApprovalFixCommands() => $"{PairingCommands}|from={Name}";
        public string BuildMissingScopeFixCommands(string missingScope) =>
            string.Format(MissingScopeCommandsTemplate, missingScope) + $"|from={Name}";
    }

    private static QuickSendCoordinator NewCoordinator(Func<IQuickSendGateway?> provider)
        // Tight timings keep tests fast; behavior identical to production defaults.
        => new(provider, connectTimeoutMs: 200, providerRetryDelayMs: 5,
               delayAsync: _ => Task.CompletedTask);

    // -- Closure condition #1 stale-snapshot scenarios --------------------

    [Fact]
    public async Task Send_AfterClientReinitialized_UsesFreshClient()
    {
        // Open dialog while App._gatewayClient = clientA (e.g., still unpaired
        // bootstrap-token instance). Autopair completes → App reassigns to
        // clientB (paired). Per-send resolver must observe clientB.
        var clientA = new FakeGateway("A");
        var clientB = new FakeGateway("B");
        IQuickSendGateway? live = clientA;
        var coord = NewCoordinator(() => live);

        live = clientB; // App swapped underneath the dialog
        var outcome = await coord.SendAsync("hello");

        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(0, clientA.SendCount);
        Assert.Equal(1, clientB.SendCount);
        Assert.Equal("hello", clientB.LastSent);
    }

    [Fact]
    public async Task ReusedDialog_AfterClientSwap_UsesNewClient()
    {
        // Mirrors App.ShowQuickSend's _quickSendDialog reactivation path:
        // the dialog instance lives across many Sends and across A→B swap.
        var clientA = new FakeGateway("A");
        var clientB = new FakeGateway("B");
        IQuickSendGateway? live = clientA;
        var coord = NewCoordinator(() => live);

        var firstOutcome = await coord.SendAsync("ping-1");
        Assert.IsType<QuickSendOutcome.Sent>(firstOutcome);
        Assert.Equal(1, clientA.SendCount);

        live = clientB;
        var secondOutcome = await coord.SendAsync("ping-2");
        Assert.IsType<QuickSendOutcome.Sent>(secondOutcome);
        Assert.Equal(1, clientA.SendCount); // not re-used
        Assert.Equal(1, clientB.SendCount);
        Assert.Equal("ping-2", clientB.LastSent);
    }

    // -- Closure condition #2 lifetime contract: null + disposed ---------

    [Fact]
    public async Task Send_WhenProviderReturnsNull_ShowsInitializing_NoClipboard()
    {
        // Provider returns null both on first try and after the short retry
        // delay (true mid-init, not just a swap blip).
        var coord = NewCoordinator(() => null);

        var outcome = await coord.SendAsync("hello");

        var init = Assert.IsType<QuickSendOutcome.GatewayInitializing>(outcome);
        Assert.Contains("initializing", init.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_WhenProviderReturnsNullThenClient_RetriesAndSends()
    {
        // Closes the swap-window race: dispose-then-init briefly leaves the
        // field null. The coordinator retries the provider once after a
        // short delay before declaring "initializing".
        var clientB = new FakeGateway("B");
        var calls = 0;
        IQuickSendGateway? Provider()
        {
            calls++;
            return calls == 1 ? null : clientB;
        }

        var coord = NewCoordinator(Provider);
        var outcome = await coord.SendAsync("hello");

        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(1, clientB.SendCount);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Send_WhenResolvedClientThrowsObjectDisposed_ReturnsFailed_NoClipboard()
    {
        // Provider returns A; A is disposed mid-send and throws
        // ObjectDisposedException from SendChatMessageAsync.
        var clientA = new FakeGateway("A")
        {
            SendThrows = new ObjectDisposedException("WebSocket")
        };
        var coord = NewCoordinator(() => clientA);

        var outcome = await coord.SendAsync("hello");

        var failed = Assert.IsType<QuickSendOutcome.Failed>(outcome);
        // Specifically NOT a clipboard-pairing remediation outcome.
        Assert.IsNotType<QuickSendOutcome.PairingRequired>(outcome);
        Assert.Contains("reset mid-send", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_WhenProviderItselfThrows_ShowsInitializing()
    {
        // Defensive: belt-and-braces against future provider impls. Treat as
        // mid-swap, not as a hard failure.
        var coord = NewCoordinator(() => throw new InvalidOperationException("boom"));

        var outcome = await coord.SendAsync("hello");

        Assert.IsType<QuickSendOutcome.GatewayInitializing>(outcome);
    }

    // -- Closure condition #3 genuine-unpaired regression guard ----------

    [Fact]
    public async Task Send_WhenLiveClientGenuinelyUnpaired_StillFiresClipboardRemediation()
    {
        // The current live client (resolved by the provider on this Send)
        // genuinely reports NOT_PAIRED. Mike explicitly does NOT want this
        // suppressed: clipboard remediation must still fire, and the
        // commands must come from THIS resolved client (not a stale one).
        var live = new FakeGateway("LIVE-but-unpaired")
        {
            PairingCommands = "REAL-PAIR-CMDS",
            SendThrows = new InvalidOperationException("NOT_PAIRED: device not approved"),
        };
        var coord = NewCoordinator(() => live);

        var outcome = await coord.SendAsync("hello");

        var pairing = Assert.IsType<QuickSendOutcome.PairingRequired>(outcome);
        Assert.Contains("REAL-PAIR-CMDS", pairing.Commands);
        Assert.Contains("from=LIVE-but-unpaired", pairing.Commands);
    }

    [Fact]
    public async Task Send_PairingRemediationIsBuiltFromLiveClient_NotStaleSnapshot()
    {
        // Belt-and-braces variant of the regression guard: even when the App
        // swaps A→B mid-flight (B is genuinely unpaired), the remediation
        // commands must come from B (live), not A (stale snapshot).
        var staleA = new FakeGateway("STALE-A") { PairingCommands = "STALE-CMDS" };
        var liveB = new FakeGateway("LIVE-B")
        {
            PairingCommands = "LIVE-CMDS",
            SendThrows = new InvalidOperationException("not paired"),
        };
        IQuickSendGateway? live = staleA;
        var coord = NewCoordinator(() => live);

        live = liveB;
        var outcome = await coord.SendAsync("hello");

        var pairing = Assert.IsType<QuickSendOutcome.PairingRequired>(outcome);
        Assert.Contains("LIVE-CMDS", pairing.Commands);
        Assert.DoesNotContain("STALE-CMDS", pairing.Commands);
        Assert.Contains("from=LIVE-B", pairing.Commands);
    }

    // -- Tunnel restart + manual ConnectionPage reinit (dialog-lifetime) -

    [Fact]
    public async Task Send_AfterSshTunnelRestart_UsesNewClient()
    {
        // SSH tunnel restart in App.RestartSshTunnel disposes _gatewayClient,
        // sets it null, then re-runs InitializeGatewayClient. Provider sees
        // the new instance on the next Send.
        var oldClient = new FakeGateway("OLD");
        var newClient = new FakeGateway("NEW");
        IQuickSendGateway? live = oldClient;
        var coord = NewCoordinator(() => live);

        await coord.SendAsync("before-restart");
        Assert.Equal(1, oldClient.SendCount);

        // Simulate restart: dispose-and-null, then reinit with new instance.
        live = null;
        live = newClient;

        var outcome = await coord.SendAsync("after-restart");
        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(1, newClient.SendCount);
        Assert.Equal(1, oldClient.SendCount); // unchanged
    }

    [Fact]
    public async Task Send_AfterManualConnectionPageReinit_UsesNewClient()
    {
        // ConnectionPage.TestConnection → app.ReinitializeGatewayClient()
        // swaps the App field. QuickSend opened later (or kept open) must
        // observe the new instance.
        var preReinit = new FakeGateway("PRE");
        var postReinit = new FakeGateway("POST");
        IQuickSendGateway? live = preReinit;
        var coord = NewCoordinator(() => live);

        live = postReinit;
        var outcome = await coord.SendAsync("hello");

        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(1, postReinit.SendCount);
        Assert.Equal(0, preReinit.SendCount);
    }

    // -- Autopair end-to-end (integration validation, RubberDucky #3) -----

    [Fact]
    public async Task Autopair_End_To_End_Reinit_Then_QuickSend_Sends_Successfully()
    {
        // Simulates the full front-door autopair sequence at the resolver
        // contract layer (the only layer that determines staleness):
        //
        //   1. App boots with bootstrap-token client A (will report NOT_PAIRED).
        //   2. User opens QuickSend; dialog captures Func<>=()=>field, NOT
        //      a snapshot of A.
        //   3. Local autopair completes; OnboardingCompleted callback
        //      disposes A, sets field=null, calls InitializeGatewayClient
        //      which assigns a freshly-paired client B to field.
        //   4. User clicks Send. Resolver returns B; Send succeeds; NO
        //      clipboard pairing-remediation toast fires.
        //
        // The integration this validates is the same one Mike's manual e2e
        // exercises after the fix lands. A documented manual harness step
        // is in .squad/decisions/inbox/aaron-bug3-implementation.md §Manual
        // E2E for tray-process verification.
        var bootstrapClientA = new FakeGateway("bootstrap-A")
        {
            SendThrows = new InvalidOperationException("NOT_PAIRED"),
        };
        var pairedClientB = new FakeGateway("paired-B");

        IQuickSendGateway? appField = bootstrapClientA;
        var coord = NewCoordinator(() => appField);

        // Phase 1: dialog opens; without the fix, Send here would clipboard-toast.
        // Phase 2: autopair completes — App swaps field A→B (with brief null).
        appField = null;
        appField = pairedClientB;

        // Phase 3: user clicks Send.
        var outcome = await coord.SendAsync("first message after autopair");

        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(0, bootstrapClientA.SendCount);
        Assert.Equal(1, pairedClientB.SendCount);
    }

    // -- Misc behavioral coverage ----------------------------------------

    [Fact]
    public async Task Send_EmptyMessage_ReturnsFailed()
    {
        var coord = NewCoordinator(() => new FakeGateway());
        var outcome = await coord.SendAsync("   ");
        Assert.IsType<QuickSendOutcome.Failed>(outcome);
    }

    [Fact]
    public async Task Send_MissingScope_ReturnsMissingScopeOutcome_FromLiveClient()
    {
        var live = new FakeGateway("scoped")
        {
            MissingScopeCommandsTemplate = "RUN:fix scope {0}",
            SendThrows = new InvalidOperationException("missing scope: operator.write"),
        };
        var coord = NewCoordinator(() => live);

        var outcome = await coord.SendAsync("hello");

        var ms = Assert.IsType<QuickSendOutcome.MissingScope>(outcome);
        Assert.Equal("operator.write", ms.Scope);
        Assert.Contains("RUN:fix scope operator.write", ms.Commands);
        Assert.Contains("from=scoped", ms.Commands);
    }

    [Fact]
    public async Task Send_WhenNotConnected_AttemptsConnect()
    {
        var live = new FakeGateway("disconnected") { IsConnectedToGateway = false };
        var coord = NewCoordinator(() => live);

        var outcome = await coord.SendAsync("hello");

        Assert.IsType<QuickSendOutcome.Sent>(outcome);
        Assert.Equal(1, live.ConnectCount);
        Assert.Equal(1, live.SendCount);
    }

    [Fact]
    public void IsPairingRequired_MatchesAllKnownVariants()
    {
        Assert.True(QuickSendCoordinator.IsPairingRequired("NOT_PAIRED"));
        Assert.True(QuickSendCoordinator.IsPairingRequired("device is not paired"));
        Assert.True(QuickSendCoordinator.IsPairingRequired("pairing required"));
        Assert.False(QuickSendCoordinator.IsPairingRequired("transport closed"));
        Assert.False(QuickSendCoordinator.IsPairingRequired(null));
    }
}
