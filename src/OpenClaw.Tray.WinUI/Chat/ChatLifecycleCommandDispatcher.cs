using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal enum ChatLifecycleCommandKind
{
    New,
    Reset,
    Compact,
}

internal sealed record ChatLifecycleCommandResult(
    ChatLifecycleCommandKind Command,
    bool Succeeded,
    string? NewSessionKey = null,
    string? Error = null);

internal static class ChatLifecycleCommandParser
{
    public static bool TryParse(
        string? text,
        bool hasAttachments,
        out ChatLifecycleCommandKind command)
    {
        command = default;
        if (hasAttachments || string.IsNullOrWhiteSpace(text))
            return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "/new":
                command = ChatLifecycleCommandKind.New;
                return true;
            case "/reset":
                command = ChatLifecycleCommandKind.Reset;
                return true;
            case "/compact":
                command = ChatLifecycleCommandKind.Compact;
                return true;
            default:
                return false;
        }
    }
}

internal static class ChatLifecycleSelectionPolicy
{
    public static string? RetainPendingForSelection(
        string? pendingSelectedId,
        string? selectedId) =>
        pendingSelectedId is not null &&
        string.Equals(pendingSelectedId, selectedId, StringComparison.Ordinal)
            ? pendingSelectedId
            : null;

    public static bool ShouldFallback(
        string staleSelectedId,
        string? pendingSelectedId,
        string fallbackThreadId) =>
        !string.Equals(pendingSelectedId, staleSelectedId, StringComparison.Ordinal) &&
        !string.Equals(staleSelectedId, fallbackThreadId, StringComparison.Ordinal);
}

internal static class ChatLifecycleCommandExecutionPolicy
{
    public static bool ShouldQueue(ChatLifecycleCommandKind command) =>
        command == ChatLifecycleCommandKind.Compact;
}

internal sealed class ChatLifecycleCommandDispatcher(IChatGatewayBridge bridge)
{
    public async Task<ChatLifecycleCommandResult> ExecuteAsync(
        string sessionKey,
        ChatLifecycleCommandKind command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            return new ChatLifecycleCommandResult(
                command,
                Succeeded: false,
                Error: "No active session is available.");
        }

        return command switch
        {
            ChatLifecycleCommandKind.New => await CreateSessionAsync(sessionKey, cancellationToken).ConfigureAwait(false),
            ChatLifecycleCommandKind.Reset => await ResetSessionAsync(sessionKey, cancellationToken).ConfigureAwait(false),
            ChatLifecycleCommandKind.Compact => await CompactSessionAsync(sessionKey, cancellationToken).ConfigureAwait(false),
            _ => new ChatLifecycleCommandResult(command, Succeeded: false, Error: "Unsupported lifecycle command.")
        };
    }

    private async Task<ChatLifecycleCommandResult> CreateSessionAsync(
        string parentSessionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await bridge.CreateSessionAsync(new SessionCreateRequest
        {
            ParentSessionKey = parentSessionKey,
            EmitCommandHooks = true,
            SucceedsParent = false,
        }).ConfigureAwait(false);

        if (!result.IsSupported)
        {
            return new ChatLifecycleCommandResult(
                ChatLifecycleCommandKind.New,
                Succeeded: false,
                Error: "This gateway does not support creating new sessions. Update the gateway and try again.");
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Key))
        {
            return new ChatLifecycleCommandResult(
                ChatLifecycleCommandKind.New,
                Succeeded: false,
                Error: result.Error ?? "The gateway could not create a new session.");
        }

        var newSessionKey = result.Key.Trim();
        if (string.Equals(newSessionKey, parentSessionKey.Trim(), StringComparison.Ordinal))
        {
            return new ChatLifecycleCommandResult(
                ChatLifecycleCommandKind.New,
                Succeeded: false,
                Error: "The gateway returned the current session instead of creating a new one.");
        }

        return new ChatLifecycleCommandResult(
            ChatLifecycleCommandKind.New,
            Succeeded: true,
            NewSessionKey: newSessionKey);
    }

    private async Task<ChatLifecycleCommandResult> ResetSessionAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await bridge.ResetSessionDetailedAsync(sessionKey).ConfigureAwait(false);
        return result.Ok
            ? new ChatLifecycleCommandResult(ChatLifecycleCommandKind.Reset, Succeeded: true)
            : new ChatLifecycleCommandResult(
                ChatLifecycleCommandKind.Reset,
                Succeeded: false,
                Error: result.Error ?? result.Reason ?? "The gateway could not reset the session.");
    }

    private async Task<ChatLifecycleCommandResult> CompactSessionAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await bridge.CompactSessionDetailedAsync(sessionKey).ConfigureAwait(false);
        return result.Ok
            ? new ChatLifecycleCommandResult(ChatLifecycleCommandKind.Compact, Succeeded: true)
            : new ChatLifecycleCommandResult(
                ChatLifecycleCommandKind.Compact,
                Succeeded: false,
                Error: result.Error ?? result.Reason ?? "The gateway could not compact the session.");
    }
}
