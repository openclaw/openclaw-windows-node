using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public static class OnboardingChatBootstrapper
{
    private static int s_inFlight;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.HasInjectedFirstRunBootstrap)
            return true;
        if (client == null || !client.IsConnectedToGateway)
            return false;
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
