using OpenClaw.Connection;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public static class OnboardingChatBootstrapper
{
    private static int s_inFlight;
    private static readonly TimeSpan ExistingWorkspaceProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly HashSet<string> ExistingWorkspaceMarkerFiles = new(StringComparer.Ordinal)
    {
        "SOUL.md",
        "IDENTITY.md",
        "USER.md",
        "HEARTBEAT.md",
        "MEMORY.md",
    };

    public const string Message =
        "Hi! I just installed OpenClaw and you're my brand-new agent. " +
        "Please start the first-run ritual from BOOTSTRAP.md, ask one question at a time, " +
        "and before we talk about WhatsApp/Telegram, visit soul.md with me to craft SOUL.md: " +
        "ask what matters to me and how you should be. Then guide me through choosing " +
        "how we should talk (web-only, WhatsApp, or Telegram).";

    public static bool ShouldBootstrap(SettingsManager settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.HasInjectedFirstRunBootstrap;
    }

    public static void MarkBootstrapped(SettingsManager settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.HasInjectedFirstRunBootstrap) return;
        settings.HasInjectedFirstRunBootstrap = true;
        settings.Save();
    }

    public static async Task<bool> BootstrapAsync(
        IOperatorGatewayClient? client,
        SettingsManager settings,
        TimeSpan? completionTimeout = null,
        CancellationToken cancellationToken = default,
        GatewayRegistry? registry = null,
        TimeSpan? existingWorkspaceProbeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.HasInjectedFirstRunBootstrap)
            return true;

        if (client == null || !client.IsConnectedToGateway)
            return false;

        // A saved gateway credential is not enough to suppress hatching: fresh local setup
        // creates registry-backed credentials before the first-run prompt has been sent.
        // Only consume the gate when the connected workspace already contains durable
        // OpenClaw state that the bootstrap ritual would otherwise rewrite.
        if (registry is not null &&
            SetupExistingGatewayClassifier.HasAnyExistingGatewayConnection(
                registry,
                settings,
                SettingsManager.SettingsDirectoryPath) &&
            await HasExistingWorkspaceStateAsync(
                client,
                existingWorkspaceProbeTimeout ?? ExistingWorkspaceProbeTimeout,
                cancellationToken).ConfigureAwait(true))
        {
            MarkBootstrapped(settings);
            Logger.Info("[OnboardingChatBootstrapper] Existing OpenClaw workspace state detected; skipping first-run bootstrap prompt.");
            return true;
        }

        if (Interlocked.CompareExchange(ref s_inFlight, 1, 0) != 0)
        {
            Logger.Info("[OnboardingChatBootstrapper] Bootstrap skipped because another gateway send is in flight");
            return false;
        }

        try
        {
            if (settings.HasInjectedFirstRunBootstrap)
                return true;

            var timeout = completionTimeout ?? TimeSpan.FromSeconds(90);
            var timeoutAt = DateTimeOffset.UtcNow + timeout;
            using var runCompletion = new RunCompletionObserver(client);

            Logger.Info("[OnboardingChatBootstrapper] Sending hatching bootstrap through gateway chat.send");
            var result = await client.SendChatMessageForRunAsync(Message).ConfigureAwait(true);
            if (settings.HasInjectedFirstRunBootstrap)
                return true;

            var completed = await runCompletion.WaitForCompletionAsync(
                result.RunId,
                timeoutAt,
                cancellationToken).ConfigureAwait(true);

            if (!completed)
            {
                Logger.Warn($"[OnboardingChatBootstrapper] chat.send acknowledged but run completion was not observed (runId={result.RunId ?? "<none>"})");
                return false;
            }

            MarkBootstrapped(settings);
            Logger.Info($"[OnboardingChatBootstrapper] Hatching bootstrap completed via gateway (runId={result.RunId ?? "<none>"})");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingChatBootstrapper] Gateway bootstrap failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref s_inFlight, 0);
        }
    }

    private static async Task<bool> HasExistingWorkspaceStateAsync(
        IOperatorGatewayClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const string agentId = "main";
        using var observer = new AgentFilesListObserver(client, agentId);
        try
        {
            await client.RequestAgentFilesListAsync(agentId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[OnboardingChatBootstrapper] Workspace state probe failed: {ex.Message}");
            return false;
        }

        var payload = await observer.WaitForFilesListAsync(
            DateTimeOffset.UtcNow + timeout,
            cancellationToken).ConfigureAwait(true);

        if (payload is null)
        {
            Logger.Info("[OnboardingChatBootstrapper] Workspace state probe returned no file list; preserving first-run bootstrap path.");
            return false;
        }

        return ContainsExistingWorkspaceMarker(payload.Value);
    }

    private static bool ContainsExistingWorkspaceMarker(JsonElement payload)
    {
        if (!payload.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var fileEl in filesEl.EnumerateArray())
        {
            var exists = !fileEl.TryGetProperty("exists", out var existsEl) ||
                         existsEl.ValueKind != JsonValueKind.False;
            if (!exists)
                continue;

            if (!fileEl.TryGetProperty("name", out var nameEl))
                continue;

            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (ExistingWorkspaceMarkerFiles.Contains(Path.GetFileName(name)))
                return true;
        }

        return false;
    }

    private sealed class AgentFilesListObserver : IDisposable
    {
        private readonly IOperatorGatewayClient _client;
        private readonly string _agentId;
        private readonly TaskCompletionSource<JsonElement?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentFilesListObserver(IOperatorGatewayClient client, string agentId)
        {
            _client = client;
            _agentId = agentId;
            _client.AgentFilesListUpdated += OnAgentFilesListUpdated;
        }

        public async Task<JsonElement?> WaitForFilesListAsync(
            DateTimeOffset timeoutAt,
            CancellationToken cancellationToken)
        {
            if (_completion.Task.IsCompleted)
                return await _completion.Task.ConfigureAwait(true);

            var remaining = timeoutAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return null;

            var completed = await Task.WhenAny(_completion.Task, Task.Delay(remaining, cancellationToken)).ConfigureAwait(true);
            if (completed != _completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            return await _completion.Task.ConfigureAwait(true);
        }

        public void Dispose()
        {
            _client.AgentFilesListUpdated -= OnAgentFilesListUpdated;
        }

        private void OnAgentFilesListUpdated(object? sender, JsonElement payload)
        {
            if (sender != _client)
                return;

            if (payload.TryGetProperty("agentId", out var agentIdEl) &&
                !string.Equals(agentIdEl.GetString(), _agentId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _completion.TrySetResult(payload.Clone());
        }
    }

    private sealed class RunCompletionObserver : IDisposable
    {
        private readonly IOperatorGatewayClient _client;
        private readonly object _gate = new();
        private readonly HashSet<string> _completedRunIds = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _runId;

        public RunCompletionObserver(IOperatorGatewayClient client)
        {
            _client = client;
            _client.AgentEventReceived += OnEventReceived;
            _client.ChatEventReceived += OnEventReceived;
        }

        public async Task<bool> WaitForCompletionAsync(
            string? runId,
            DateTimeOffset timeoutAt,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runId))
                return true;

            lock (_gate)
            {
                _runId = runId;
                if (_completedRunIds.Contains(runId))
                {
                    _completion.TrySetResult(true);
                }
            }

            if (_completion.Task.IsCompleted)
                return await _completion.Task.ConfigureAwait(true);

            var remaining = timeoutAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            var completed = await Task.WhenAny(_completion.Task, Task.Delay(remaining, cancellationToken)).ConfigureAwait(true);
            if (completed != _completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            return await _completion.Task.ConfigureAwait(true);
        }

        public void Dispose()
        {
            _client.AgentEventReceived -= OnEventReceived;
            _client.ChatEventReceived -= OnEventReceived;
        }

        private void OnEventReceived(object? sender, AgentEventInfo evt)
        {
            if (string.IsNullOrWhiteSpace(evt.RunId))
                return;
            if (!IsFinalAssistantEvent(evt) && !IsLifecycleFinalEvent(evt))
                return;

            lock (_gate)
            {
                if (_runId == null)
                {
                    _completedRunIds.Add(evt.RunId);
                    return;
                }

                if (string.Equals(evt.RunId, _runId, StringComparison.Ordinal))
                {
                    _completion.TrySetResult(true);
                }
            }
        }
    }

    private static bool IsFinalAssistantEvent(AgentEventInfo evt)
    {
        if (!string.Equals(evt.Stream, "assistant", StringComparison.OrdinalIgnoreCase))
            return false;
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return false;
        if (evt.Data.TryGetProperty("state", out var state) &&
            string.Equals(state.GetString(), "final", StringComparison.OrdinalIgnoreCase))
            return true;
        return evt.Data.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), "final", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleFinalEvent(AgentEventInfo evt)
    {
        if (string.Equals(evt.Stream, "final", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Stream, "done", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase))
            return false;
        if (evt.Data.ValueKind != JsonValueKind.Object)
            return false;
        if (evt.Data.TryGetProperty("state", out var state) &&
            string.Equals(state.GetString(), "final", StringComparison.OrdinalIgnoreCase))
            return true;
        return evt.Data.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), "session.completed", StringComparison.OrdinalIgnoreCase);
    }
}
