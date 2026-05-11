using OpenClaw.Shared;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Dialogs;

// Bug #3 (manual test 2026-05-05): QuickSendDialog used to capture the App's
// gateway client at constructor time into a readonly field. After autopair (or
// any other path that swapped App._gatewayClient — SSH tunnel restart, manual
// ConnectionPage re-pair, onboarding completion), the dialog kept sending into
// the stale instance which still reported NOT_PAIRED, triggering the
// "copy pair command to clipboard" remediation toast against a perfectly
// paired live client.
//
// This file extracts the per-Send logic into a pure, UI-free coordinator that:
//   1. Resolves the live gateway client from a Func<> provider on every Send.
//   2. Defines explicit behavior for null / disposed / swap-window cases.
//   3. Returns a discriminated outcome the dialog renders.
//
// RubberDucky closure conditions #1 (scope), #2 (lifetime contract) and #3
// (genuine-unpaired regression test) are all satisfied by tests over this
// coordinator (see tests/OpenClaw.Tray.Tests/QuickSendCoordinatorTests.cs).

/// <summary>
/// Minimal gateway surface QuickSend needs. Wrapping the real
/// <see cref="OpenClawGatewayClient"/> behind this interface keeps
/// <see cref="QuickSendCoordinator"/> testable without spinning up a real
/// WebSocket client.
/// </summary>
public interface IQuickSendGateway
{
    bool IsConnectedToGateway { get; }
    Task ConnectAsync();
    Task SendChatMessageAsync(string message);
    string BuildPairingApprovalFixCommands();
    string BuildMissingScopeFixCommands(string missingScope);
}

/// <summary>
/// Adapter that exposes the live <see cref="OpenClawGatewayClient"/> through
/// <see cref="IQuickSendGateway"/> for the production wiring.
/// </summary>
public sealed class OpenClawGatewayClientAdapter : IQuickSendGateway
{
    private readonly OpenClawGatewayClient _client;

    public OpenClawGatewayClientAdapter(OpenClawGatewayClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnectedToGateway => _client.IsConnectedToGateway;
    public Task ConnectAsync() => _client.ConnectAsync();
    public Task SendChatMessageAsync(string message) => _client.SendChatMessageAsync(message);
    public string BuildPairingApprovalFixCommands() => _client.BuildPairingApprovalFixCommands();
    public string BuildMissingScopeFixCommands(string missingScope) => _client.BuildMissingScopeFixCommands(missingScope);
}

/// <summary>
/// Discriminated outcome of a single Send attempt. The dialog renders the
/// outcome; the coordinator never touches UI.
/// </summary>
public abstract record QuickSendOutcome
{
    /// <summary>Message accepted by the gateway.</summary>
    public sealed record Sent : QuickSendOutcome;

    /// <summary>
    /// Gateway client provider returned null (or a previously-disposed
    /// instance was detected) — the App is mid-swap (init, restart, autopair
    /// reinit). DO NOT show the clipboard-pairing remediation; show a
    /// "still initializing" message and let the user retry.
    /// </summary>
    public sealed record GatewayInitializing(string Message) : QuickSendOutcome;

    /// <summary>
    /// Live current client genuinely reports NOT_PAIRED. Clipboard remediation
    /// MUST still fire — this is the path Mike explicitly does not want
    /// suppressed.
    /// </summary>
    public sealed record PairingRequired(string Commands) : QuickSendOutcome;

    /// <summary>Live current client is missing a required operator scope.</summary>
    public sealed record MissingScope(string Scope, string Commands) : QuickSendOutcome;

    /// <summary>Any other failure (timeout, transport, dispose race, etc.).</summary>
    public sealed record Failed(string ErrorMessage) : QuickSendOutcome;
}

/// <summary>
/// Pure (no UI, no static state) per-Send orchestrator. The dialog passes a
/// <see cref="Func{T}"/> that reads <c>App._gatewayClient</c> on every Send
/// so a swap underneath the dialog is observed before remediation decisions
/// are made.
/// </summary>
public sealed class QuickSendCoordinator
{
    /// <summary>
    /// Provider/lifetime contract — see Bug #3 plan §3 and RubberDucky
    /// closure condition #2:
    ///
    /// (a) Provider returns null  => GatewayInitializing (no clipboard toast).
    ///     Reason: App is between Dispose() and the next assignment of
    ///     _gatewayClient (SSH tunnel restart, onboarding swap), or the field
    ///     has not yet been initialized.
    /// (b) Provider returns a previously-disposed instance => SendChatMessageAsync
    ///     throws "Gateway connection is not open" or ObjectDisposedException;
    ///     coordinator catches and returns Failed (NOT clipboard).
    /// (c) Provider returns a live client that genuinely reports NOT_PAIRED =>
    ///     PairingRequired (clipboard toast STILL fires — built from the
    ///     resolved current client, never a captured stale one).
    /// </summary>
    private readonly Func<IQuickSendGateway?> _provider;
    private readonly int _connectTimeoutMs;
    private readonly int _providerRetryDelayMs;
    private readonly Func<int, Task> _delayAsync;

    public QuickSendCoordinator(
        Func<IQuickSendGateway?> provider,
        int connectTimeoutMs = 3000,
        int providerRetryDelayMs = 100,
        Func<int, Task>? delayAsync = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _connectTimeoutMs = connectTimeoutMs;
        _providerRetryDelayMs = providerRetryDelayMs;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<QuickSendOutcome> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new QuickSendOutcome.Failed("Message is empty.");
        }

        // Resolve live client. If the App is mid-swap (e.g., between Dispose
        // and the next InitializeGatewayClient assignment), the provider
        // returns null briefly. Retry once after a short delay to absorb the
        // window without surfacing a spurious "initializing" message.
        var client = ResolveClient();
        if (client == null)
        {
            await _delayAsync(_providerRetryDelayMs).ConfigureAwait(false);
            client = ResolveClient();
        }

        if (client == null)
        {
            return new QuickSendOutcome.GatewayInitializing(
                "Gateway is still initializing. Please try again in a moment.");
        }

        try
        {
            if (!await EnsureConnectedAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return new QuickSendOutcome.Failed("Gateway connection is not open");
            }

            await client.SendChatMessageAsync(message).ConfigureAwait(false);
            return new QuickSendOutcome.Sent();
        }
        catch (Exception ex)
        {
            return ClassifyFailure(client, ex);
        }
    }

    private IQuickSendGateway? ResolveClient()
    {
        try
        {
            return _provider();
        }
        catch
        {
            // Provider is `() => _gatewayClient` — the field read itself
            // can't throw, but defensive belt-and-braces against future
            // provider implementations.
            return null;
        }
    }

    private async Task<bool> EnsureConnectedAsync(IQuickSendGateway client, CancellationToken cancellationToken)
    {
        if (client.IsConnectedToGateway) return true;

        try
        {
            await client.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Connect errors surface via the subsequent send.
        }

        var deadline = Environment.TickCount64 + _connectTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (client.IsConnectedToGateway) return true;
            await _delayAsync(120).ConfigureAwait(false);
        }

        return client.IsConnectedToGateway;
    }

    private static QuickSendOutcome ClassifyFailure(IQuickSendGateway client, Exception ex)
    {
        // ObjectDisposedException happens when the resolved client was
        // disposed mid-send (case (b) of the lifetime contract). Surface as
        // a clean Failed — never as the clipboard pairing remediation.
        if (ex is ObjectDisposedException)
        {
            return new QuickSendOutcome.Failed(
                "Gateway client was reset mid-send. Please try again.");
        }

        var msg = ex.Message;
        if (IsPairingRequired(msg))
        {
            // Built from the live current client (resolved in this call), not
            // any captured stale snapshot — closes Bug #3 root cause.
            var commands = client.BuildPairingApprovalFixCommands();
            return new QuickSendOutcome.PairingRequired(commands);
        }

        if (TryExtractMissingScope(msg, out var scope))
        {
            var commands = client.BuildMissingScopeFixCommands(scope);
            return new QuickSendOutcome.MissingScope(scope, commands);
        }

        return new QuickSendOutcome.Failed(msg);
    }

    internal static bool IsPairingRequired(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("pairing required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not paired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("NOT_PAIRED", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryExtractMissingScope(string? message, out string scope)
    {
        scope = string.Empty;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var match = Regex.Match(message, @"missing\s+scope\s*:\s*([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        scope = match.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(scope);
    }
}
