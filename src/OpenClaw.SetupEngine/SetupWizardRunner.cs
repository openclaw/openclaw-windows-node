using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class SetupWizardRunner
{
    private const int MaxWizardSteps = 50;
    private const int MaxSameStepVisits = 3;
    private static readonly Regex s_normalizeKeyRegex = new("[^a-z0-9]+", RegexOptions.Compiled);

    // Progress steps can repeat while background work runs; keep bounded caps
    // so setup fails with a diagnostic instead of hanging.

    private readonly SetupContext _ctx;
    private bool _reloadSuspended;
    private bool _reloadRecoveryRecorded;

    public SetupWizardRunner(SetupContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<StepResult> RunAsync(CancellationToken ct)
    {
        var recoveryResult = await ReconcilePendingReloadRecoveryAsync();
        if (!recoveryResult.IsSuccess)
            return recoveryResult;

        if (_ctx.Config.SkipWizard)
            return StepResult.Skip("Gateway wizard skipped by configuration");

        return await RunWithReloadRestorationAsync(() => RunCoreAsync(ct));
    }

    internal async Task<StepResult> RunWithReloadRestorationAsync(Func<Task<StepResult>> runWizard)
    {
        StepResult? wizardResult = null;
        Exception? wizardException = null;
        StepResult? restoreResult = null;
        try
        {
            wizardResult = await runWizard();
        }
        catch (Exception ex)
        {
            wizardException = ex;
        }

        if (_reloadSuspended)
        {
            restoreResult = await RestoreReloadModeIfNeededAsync();
            if (!restoreResult.IsSuccess)
                _ctx.Logger.Error($"Gateway reload restoration failed after setup wizard exit: {restoreResult.Message}");
        }

        if (restoreResult?.IsSuccess == false)
        {
            if (wizardException is null)
                return restoreResult;

            var restorationException = new InvalidOperationException(restoreResult.Message, restoreResult.Error);
            return StepResult.Fail(
                $"{restoreResult.Message} The wizard also exited with {wizardException.GetType().Name}.",
                new AggregateException(restorationException, wizardException));
        }

        if (wizardException is not null)
            ExceptionDispatchInfo.Capture(wizardException).Throw();

        return wizardResult!;
    }

    internal void MarkReloadSuspended() => _reloadSuspended = true;

    internal async Task<StepResult> RestoreReloadModeIfNeededAsync()
    {
        if (!_reloadSuspended)
            return StepResult.Ok("Gateway reload was not suspended");

        var result = await RestoreReloadModeAsync();
        if (result.IsSuccess)
        {
            _reloadSuspended = false;
            _reloadRecoveryRecorded = false;
        }
        return result;
    }

    internal async Task<StepResult> SuspendReloadModeAsync()
    {
        try
        {
            var readResult = await _ctx.Commands.RunInWslAsync(
                _ctx.DistroName!,
                $"{_ctx.WslPathPrefix}\nopenclaw config get gateway.reload.mode --json",
                TimeSpan.FromSeconds(15),
                ct: CancellationToken.None,
                inputViaStdin: true);
            if (readResult.TimedOut)
                return StepResult.Fail("Failed to read gateway.reload.mode before wizard suspension.");

            var pathWasAbsent = readResult.ExitCode != 0 && readResult.Stderr.Contains(
                "Config path not found: gateway.reload.mode",
                StringComparison.Ordinal);
            var reloadMode = readResult.ExitCode == 0 ? ExtractReloadMode(readResult.Stdout) : null;
            if (!pathWasAbsent && (reloadMode is null || !GatewayReloadRecoveryStore.IsSupportedReloadMode(reloadMode)))
                return StepResult.Fail("Failed to resolve gateway.reload.mode before wizard suspension.");

            GatewayReloadRecoveryStore.Save(_ctx, reloadMode);
            _reloadRecoveryRecorded = true;

            var result = await _ctx.Commands.RunInWslAsync(
                _ctx.DistroName!,
                $"{_ctx.WslPathPrefix} && openclaw config set gateway.reload.mode off",
                TimeSpan.FromSeconds(15),
                ct: CancellationToken.None);

            if (result.ExitCode != 0)
                return StepResult.Fail($"Failed to suspend gateway reload for wizard (exit {result.ExitCode}): {result.Stderr.Trim()}");

            _ctx.Logger.Info("Configured gateway reload suspension; restarting gateway before wizard");
            return await StartGatewayStep.RestartAndWaitForHealthAsync(_ctx, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Failed to suspend gateway reload for wizard: {ex.Message}", ex);
        }
    }

    internal static string? ExtractReloadMode(string stdout)
    {
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var candidate = line.Trim();
            if (candidate.StartsWith('"') && candidate.EndsWith('"'))
            {
                try
                {
                    candidate = JsonSerializer.Deserialize<string>(candidate) ?? string.Empty;
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            if (GatewayReloadRecoveryStore.IsSupportedReloadMode(candidate))
                return candidate;
        }

        return null;
    }

    internal async Task<StepResult> BeginReloadSuspensionAsync()
    {
        var recoveryResult = await ReconcilePendingReloadRecoveryAsync();
        if (!recoveryResult.IsSuccess)
            return recoveryResult;

        var suspendResult = await SuspendReloadModeAsync();
        if (suspendResult.IsSuccess)
        {
            MarkReloadSuspended();
            return suspendResult;
        }

        if (!_reloadRecoveryRecorded)
            return suspendResult;

        MarkReloadSuspended();
        var restoreResult = await RestoreReloadModeIfNeededAsync();
        return restoreResult.IsSuccess ? suspendResult : restoreResult;
    }

    internal async Task<StepResult> ReconcilePendingReloadRecoveryAsync()
    {
        GatewayReloadRecoveryState? recovery;
        try
        {
            recovery = GatewayReloadRecoveryStore.Load(_ctx);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Cannot reconcile gateway reload recovery state: {ex.Message}", ex);
        }

        if (recovery is null)
            return StepResult.Ok("No pending gateway reload recovery state");

        if (!string.Equals(recovery.DistroName, _ctx.DistroName, StringComparison.OrdinalIgnoreCase))
        {
            return StepResult.Fail(
                $"Gateway reload recovery marker targets distro '{recovery.DistroName}', " +
                $"but setup targets '{_ctx.DistroName}'. Refusing to mutate either distro.");
        }

        _ctx.Logger.Warn("Pending gateway reload recovery state found; restoring before setup continues");
        return await RestoreReloadModeAsync(recovery);
    }

    private async Task<StepResult> RunCoreAsync(CancellationToken ct)
    {
        var registry = new GatewayRegistry(_ctx.DataDir, logger: new SetupOpenClawLogger(_ctx.Logger));
        registry.Load();

        var record = !string.IsNullOrWhiteSpace(_ctx.GatewayRecordId)
            ? registry.GetById(_ctx.GatewayRecordId)
            : registry.GetActive();

        if (record == null)
            return StepResult.Fail("Cannot run gateway wizard because no active gateway record was found.");

        var identityPath = registry.GetIdentityDirectory(record.Id);
        var storedDeviceToken = DeviceIdentity.TryReadStoredDeviceToken(identityPath, new SetupOpenClawLogger(_ctx.Logger));
        var credential = storedDeviceToken
            ?? _ctx.SharedGatewayToken
            ?? record.SharedGatewayToken
            ?? _ctx.BootstrapToken
            ?? record.BootstrapToken;

        if (string.IsNullOrWhiteSpace(credential))
            return StepResult.Fail("Cannot run gateway wizard because no operator credential is available.");

        _ctx.SharedGatewayToken ??= record.SharedGatewayToken;
        _ctx.BootstrapToken ??= record.BootstrapToken;

        if (string.IsNullOrWhiteSpace(storedDeviceToken)
            && !string.IsNullOrWhiteSpace(record.SharedGatewayToken)
            && string.Equals(credential, record.SharedGatewayToken, StringComparison.Ordinal))
            identityPath = Path.Combine(identityPath, "setup-wizard");

        var wsLogger = new SetupOpenClawLogger(_ctx.Logger);
        OpenClawGatewayClient? client = null;

        var sessionId = "";
        var wizardStarted = false;
        var wizardCompleted = false;
        var discoveredSteps = new List<WizardTemplateStep>();

        var suspendResult = await BeginReloadSuspensionAsync();
        if (!suspendResult.IsSuccess)
            return suspendResult;

        try
        {
            client = CreateWizardClient(credential, identityPath, wsLogger);
            var connection = await PairOperatorStep.WaitForConnectionOrPairing(
                client,
                _ctx,
                TimeSpan.FromSeconds(20),
                ct,
                waitThroughTransientErrors: true);
            if (connection == PairOperatorStep.ConnectionOutcome.PairingRequired && _ctx.Config.AutoApprovePairing)
            {
                _ctx.Logger.Info("Wizard operator pairing required — auto-approving");
                await client.DisconnectAsync();
                client.Dispose();

                var approval = await PairOperatorStep.AutoApprovePairing(_ctx, ct);
                if (!approval.IsSuccess)
                    return approval;

                await Task.Delay(2000, ct);
                client = CreateWizardClient(credential, identityPath, wsLogger);
                connection = await PairOperatorStep.WaitForConnectionOrPairing(client, _ctx, TimeSpan.FromSeconds(20), ct);
            }

            if (connection != PairOperatorStep.ConnectionOutcome.Connected)
                return StepResult.Fail($"Cannot run gateway wizard because operator connection failed: {connection}");

            _ctx.Logger.Info("Starting gateway wizard");
            var payload = await client.SendWizardRequestAsync(
                "wizard.start",
                timeoutMs: 30_000);
            wizardStarted = true;

            var visits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var restartAttempts = 0;
            var progressPolls = 0;
            var totalProgressPolls = 0;
            var lastProgressStepId = "";
            var interactiveSteps = 0;

            // A reconnect restarts the wizard session, so reset replay-scoped
            // counters before processing the replacement start payload.
            async Task<JsonElement> SendWizardNextAsync(object parameters, int timeoutMs)
            {
                try
                {
                    return await client!.SendWizardRequestAsync("wizard.next", parameters, timeoutMs);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsRestartLikeWizardDisconnect(ex) && restartAttempts < 2)
                {
                    restartAttempts++;
                    _ctx.Logger.Warn($"Gateway restarted during wizard; reconnecting and replaying answers (attempt {restartAttempts}/2): {ex.Message}");

                    try { await client!.DisconnectAsync(); } catch { }
                    client!.Dispose();

                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    client = CreateWizardClient(credential, identityPath, wsLogger);
                    var reconnect = await PairOperatorStep.WaitForConnectionOrPairing(
                        client,
                        _ctx,
                        TimeSpan.FromSeconds(30),
                        ct,
                        waitThroughTransientErrors: true);
                    if (reconnect != PairOperatorStep.ConnectionOutcome.Connected)
                        throw new WizardFatalException($"Gateway wizard reconnect failed after restart: {reconnect}");

                    sessionId = "";
                    visits.Clear();
                    discoveredSteps.Clear();
                    interactiveSteps = 0;
                    progressPolls = 0;
                    totalProgressPolls = 0;
                    lastProgressStepId = "";
                    return await client.SendWizardRequestAsync(
                        "wizard.start",
                        timeoutMs: 30_000);
                }
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var parsed = WizardPayload.Parse(payload);
                if (parsed.IsDone)
                {
                    if (!string.IsNullOrWhiteSpace(parsed.Error))
                    {
                        if (IsKnownGatewayFinalizationPromptBug(parsed.Error))
                        {
                            wizardCompleted = true;
                            _ctx.Logger.Warn($"Gateway wizard ended after applying setup but hit known finalization prompt bug: {parsed.Error}");
                            return StepResult.Ok("Gateway wizard completed with non-fatal finalization prompt warning");
                        }

                        return StepResult.Fail($"Gateway wizard failed: {parsed.Error}");
                    }

                    if (discoveredSteps.Count > 0)
                        WriteAnswerTemplate(discoveredSteps, missingStep: null);

                    wizardCompleted = true;
                    _ctx.Logger.Info("Gateway wizard completed");
                    return StepResult.Ok("Gateway wizard completed");
                }

                if (!string.IsNullOrWhiteSpace(parsed.Error))
                    return StepResult.Fail($"Gateway wizard returned an invalid step: {parsed.Error}");

                if (!string.IsNullOrWhiteSpace(parsed.SessionId))
                    sessionId = parsed.SessionId;

                if (string.IsNullOrWhiteSpace(sessionId))
                    return StepResult.Fail("Gateway wizard did not provide a session id.");

                if (string.IsNullOrWhiteSpace(parsed.StepId))
                    return StepResult.Fail("Gateway wizard step is missing an id.");

                var category = WizardStepClassifier.Categorize(parsed.StepType, parsed.Options.Count > 0);

                // Progress carries no answer; poll until the gateway emits the
                // next interactive step or reaches a bounded failure.
                if (category == WizardStepCategory.Progress)
                {
                    if (!string.Equals(parsed.StepId, lastProgressStepId, StringComparison.Ordinal))
                    {
                        lastProgressStepId = parsed.StepId;
                        progressPolls = 0;
                    }

                    progressPolls++;
                    totalProgressPolls++;
                    if (progressPolls > WizardTimeouts.MaxProgressPollsPerStep)
                        return StepResult.Fail($"Gateway wizard progress step '{parsed.StepId}' did not complete after {WizardTimeouts.MaxProgressPollsPerStep} polls.");
                    if (totalProgressPolls > WizardTimeouts.MaxTotalProgressPolls)
                        return StepResult.Fail($"Gateway wizard did not finish after {WizardTimeouts.MaxTotalProgressPolls} progress updates.");

                    var progressText = $"{parsed.Title} {parsed.Message}".Trim();
                    _ctx.Logger.Info(string.IsNullOrWhiteSpace(progressText)
                        ? $"Wizard progress step '{parsed.StepId}' — polling for next step"
                        : $"Wizard progress: {progressText}");

                    await Task.Delay(WizardTimeouts.ProgressPollDelay, ct);
                    payload = await SendWizardNextAsync(WizardNextPayload.Acknowledge(sessionId, parsed.StepId), TimeoutFor(parsed));
                    continue;
                }

                interactiveSteps++;
                if (interactiveSteps > MaxWizardSteps)
                    return StepResult.Fail($"Gateway wizard exceeded {MaxWizardSteps} steps.");

                var visitKey = $"{parsed.StepId}:{parsed.StepIndex}";
                visits.TryGetValue(visitKey, out var visitCount);
                visits[visitKey] = visitCount + 1;
                if (visits[visitKey] > MaxSameStepVisits)
                {
                    var templatePath = WriteAnswerTemplate(discoveredSteps, parsed);
                    return StepResult.Fail($"Gateway wizard repeated step '{parsed.StepId}' too many times. A wizard answer template was written to: {templatePath}");
                }

                discoveredSteps.Add(WizardTemplateStep.From(parsed));
                var answerResult = ResolveAnswer(parsed, _ctx.Config.WizardAnswers);
                if (!answerResult.Success)
                {
                    var templatePath = WriteAnswerTemplate(discoveredSteps, parsed);
                    return StepResult.Fail($"{answerResult.Error} A wizard answer template was written to: {templatePath}");
                }

                _ctx.Logger.Info(answerResult.HasAnswer
                    ? $"Wizard step '{parsed.StepId}' ({parsed.StepType}, key={StableAnswerKey(parsed.Title, parsed.Message, parsed.StepId)}) answered with {(parsed.Sensitive ? "[sensitive]" : $"'{answerResult.Answer}'")}"
                    : $"Wizard step '{parsed.StepId}' ({parsed.StepType}, {category}) continuing without explicit answer");

                object parameters = answerResult.HasAnswer
                    ? new
                    {
                        sessionId,
                        answer = new
                        {
                            stepId = parsed.StepId,
                            value = AnswerValueForWire(parsed, answerResult.Answer)
                        }
                    }
                    : WizardNextPayload.Acknowledge(sessionId, parsed.StepId);

                payload = await SendWizardNextAsync(parameters, TimeoutFor(parsed, answerResult.Answer));
            }
        }
        catch (OperationCanceledException)
        {
            if (client is not null && wizardStarted && !string.IsNullOrWhiteSpace(sessionId))
                await TryCancelWizardAsync(client, sessionId);
            throw;
        }
        catch (WizardFatalException ex)
        {
            return StepResult.Fail(ex.Message, ex);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Gateway wizard failed: {ex.Message}", ex);
        }
        finally
        {
            if (client is not null && wizardStarted && !wizardCompleted && !string.IsNullOrWhiteSpace(sessionId))
                await TryCancelWizardAsync(client, sessionId);

            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    private OpenClawGatewayClient CreateWizardClient(string credential, string identityPath, IOpenClawLogger wsLogger)
    {
        return new OpenClawGatewayClient(_ctx.GatewayUrl!, credential, logger: wsLogger, identityPath: identityPath)
        {
            UseV2Signature = true
        };
    }

    private async Task TryCancelWizardAsync(OpenClawGatewayClient client, string sessionId)
    {
        try
        {
            _ctx.Logger.Warn("Cancelling gateway wizard session");
            await client.SendWizardRequestAsync("wizard.cancel", new { sessionId }, timeoutMs: 10_000);
        }

        catch (Exception ex)
        {
            _ctx.Logger.Warn($"Failed to cancel gateway wizard session: {ex.Message}");
        }
    }

    internal async Task<StepResult> RestoreReloadModeAsync(GatewayReloadRecoveryState? recoveryOverride = null)
    {
        try
        {
            var recovery = recoveryOverride ?? GatewayReloadRecoveryStore.Load(_ctx);
            if (recovery is not null && !string.Equals(recovery.DistroName, _ctx.DistroName, StringComparison.OrdinalIgnoreCase))
            {
                return StepResult.Fail(
                    $"Gateway reload recovery marker targets distro '{recovery.DistroName}', " +
                    $"but setup targets '{_ctx.DistroName}'. Refusing to mutate either distro.");
            }

            var hadExplicitReloadMode = recovery is null || recovery.ReloadMode is not null;
            var reloadMode = recovery?.ReloadMode is { } recoveredReloadMode
                ? GatewayReloadModeConfig.Resolve(_ctx.Config.Gateway.Version, recoveredReloadMode)
                : ConfigureGatewayStep.GetEffectiveReloadMode(_ctx.Config.Gateway);
            var restoreCommand = hadExplicitReloadMode
                ? $"openclaw config set gateway.reload.mode {WslShellQuoting.QuotePosixSingleQuote(reloadMode)}"
                : "openclaw config unset gateway.reload.mode";
            var result = await _ctx.Commands.RunInWslAsync(
                _ctx.DistroName!,
                $"{_ctx.WslPathPrefix} && {restoreCommand}",
                TimeSpan.FromSeconds(15),
                ct: CancellationToken.None);

            var absentStateAlreadyRestored = !hadExplicitReloadMode && IsMissingReloadModePath(result);
            if (result.ExitCode != 0 && !absentStateAlreadyRestored)
                return StepResult.Fail($"Failed to restore gateway.reload.mode after wizard (exit {result.ExitCode}): {result.Stderr.Trim()}");

            var restoredState = hadExplicitReloadMode ? reloadMode : "its previous absent state";
            _ctx.Logger.Info($"Restored gateway.reload.mode to {restoredState} after wizard; restarting gateway to apply wizard configuration");
            var restartResult = await StartGatewayStep.RestartAndWaitForHealthAsync(_ctx, CancellationToken.None);
            if (!restartResult.IsSuccess)
                return StepResult.Fail($"Gateway restart after wizard failed: {restartResult.Message}", restartResult.Error);

            GatewayReloadRecoveryStore.Clear(_ctx);
            _reloadSuspended = false;
            _reloadRecoveryRecorded = false;

            return StepResult.Ok($"Restored gateway.reload.mode to {restoredState} and restarted gateway");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Failed to restore gateway.reload.mode after wizard: {ex.Message}", ex);
        }
    }

    private static bool IsMissingReloadModePath(CommandResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = $"{result.Stdout}\n{result.Stderr}";
        return output.Contains("Config path not found", StringComparison.OrdinalIgnoreCase)
            && output.Contains("gateway.reload.mode", StringComparison.Ordinal);
    }

    private string WriteAnswerTemplate(IReadOnlyList<WizardTemplateStep> discoveredSteps, WizardPayload? missingStep)
    {
        var logPath = _ctx.Config.LogPath;
        var basePath = !string.IsNullOrWhiteSpace(logPath)
            ? Path.ChangeExtension(logPath, ".wizard-answers.template.json")
            : Path.Combine(_ctx.DataDir, "Logs", "Setup", $"setup-engine-{_ctx.Logger.RunId}.wizard-answers.template.json");

        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in discoveredSteps)
        {
            var key = StableAnswerKey(step.Title, step.Message, step.StepId);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            answers.TryAdd(key, missingStep != null && step.StepId == missingStep.StepId
                ? AnswerPlaceholderFor(step)
                : step.SuggestedAnswer ?? AnswerPlaceholderFor(step));
        }

        var template = new
        {
            _instructions = "Copy WizardAnswers into your setup config, fill required values, then rerun setup.",
            WizardAnswers = answers,
            Steps = discoveredSteps
        };

        var json = JsonSerializer.Serialize(template, SetupConfig.JsonWriteOptions);
        AtomicFile.WriteAllText(basePath, json);
        _ctx.Logger.Info($"Wizard answer template written: {basePath}");
        return basePath;
    }

    private static AnswerResolution ResolveAnswer(WizardPayload step, Dictionary<string, string>? configuredAnswers)
    {
        if (TryGetConfiguredAnswer(step, configuredAnswers, out var configured))
            return ValidateAnswer(step, configured, configuredAnswer: true);

        var category = WizardStepClassifier.Categorize(step.StepType, step.Options.Count > 0);
        if (WizardStepClassifier.ContinuesWithoutAnswer(category))
        {
            return AnswerResolution.Continue();
        }

        switch (category)
        {
            case WizardStepCategory.Acknowledge:
                return AnswerResolution.Ok("true");

            case WizardStepCategory.Confirm:
                return ValidateAnswer(step, InferConfirmAnswer(step), configuredAnswer: false);
        }

        // Unknown types with options are choice prompts for wire-shaping purposes.
        var inferred = step.Options.Count > 0 && step.StepType != "text"
            ? InferOptionAnswer(step)
            : step.StepType switch
            {
                "select" or "multiselect" => InferOptionAnswer(step),
                "text" => InferTextAnswer(step),
                _ => !string.IsNullOrWhiteSpace(step.InitialValue) ? step.InitialValue : null
            };

        if (inferred == null)
        {
            return AnswerResolution.Fail($"Gateway wizard step '{step.StepId}' ({step.StepType}) requires a value that was not provided.");
        }

        return ValidateAnswer(step, inferred, configuredAnswer: false);
    }

    private static string? InferOptionAnswer(WizardPayload step)
    {
        if (!string.IsNullOrWhiteSpace(step.InitialValue))
            return step.InitialValue;

        var preferred = new[] { "__skip__", "skip", "__keep__", "keep" };
        foreach (var value in preferred)
        {
            if (step.Options.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal)))
                return value;
        }

        return step.Options.FirstOrDefault()?.Value;
    }

    private static string InferConfirmAnswer(WizardPayload step)
    {
        var text = $"{step.Title} {step.Message}";
        if (text.Contains("skill", StringComparison.OrdinalIgnoreCase)
            || text.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
            || text.Contains("API key", StringComparison.OrdinalIgnoreCase))
            return "false";

        return "true";
    }

    private static object AnswerValueForWire(WizardPayload step, string answer)
    {
        // Preserve the selected option's raw JSON value for unknown choice-style steps.
        var effectiveType = step.Options.Count > 0 && step.StepType is not ("select" or "multiselect" or "text")
            ? "select"
            : step.StepType;
        return WizardAnswerBuilder.BuildWireValue(effectiveType, answer, step.Options);
    }

    private static string? InferTextAnswer(WizardPayload step)
    {
        if (!string.IsNullOrWhiteSpace(step.InitialValue))
            return step.InitialValue;

        if (step.Sensitive && step.Message.Contains("API_KEY", StringComparison.OrdinalIgnoreCase))
            return "";

        return null;
    }

    private static AnswerResolution ValidateAnswer(WizardPayload step, string answer, bool configuredAnswer)
    {
        if (step.StepType == "select")
        {
            if (!WizardAnswerBuilder.TryFindOption(step.Options, answer, out _))
            {
                var source = configuredAnswer ? "configured" : "default";
                return AnswerResolution.Fail($"The {source} answer for wizard step '{step.StepId}' is not one of the gateway-provided options.");
            }
        }
        else if (step.StepType == "multiselect")
        {
            if (!WizardAnswerBuilder.TryResolveOptions(step.Options, answer, out var values))
                return AnswerResolution.Fail($"Gateway wizard step '{step.StepId}' requires at least one selected value.");

            if (values.Length == 0)
                return AnswerResolution.Fail($"Gateway wizard step '{step.StepId}' requires at least one selected value.");
        }

        return AnswerResolution.Ok(answer);
    }

    private static bool TryGetConfiguredAnswer(WizardPayload step, Dictionary<string, string>? answers, out string answer)
    {
        answer = "";
        if (answers is not { Count: > 0 })
            return false;

        var keys = new[]
        {
            step.StepId,
            NormalizeKey(step.StepId),
            step.Title,
            NormalizeKey(step.Title),
            step.Message,
            NormalizeKey(step.Message)
        }.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            foreach (var (configuredKey, configuredValue) in answers)
            {
                if (string.Equals(configuredKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    answer = configuredValue;
                    return true;
                }
            }
        }

        return false;
    }

    private static int TimeoutFor(WizardPayload step, string? answer = null)
        => WizardTimeouts.ForGatewayStep(
            step.Title,
            step.Message,
            step.StepId,
            step.StepType,
            step.Options,
            answer);

    private static bool IsRestartLikeWizardDisconnect(Exception ex)
    {
        return ex.Message.Contains("connection lost", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("gateway restarting", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("service restart", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownGatewayFinalizationPromptBug(string error)
    {
        return error.Contains("this.prompt is not a function", StringComparison.OrdinalIgnoreCase);
    }

    private static string AnswerPlaceholderFor(WizardTemplateStep step)
    {
        return step.Type switch
        {
            "select" => step.Options.FirstOrDefault()?.Value ?? "<select one option value>",
            "multiselect" => "<comma-separated option values>",
            "text" => step.Sensitive
                ? "<sensitive value>"
                : step.AuthUrls.Count > 0
                    ? $"<value obtained from: {step.AuthUrls[0]}>"
                    : "<text value>",
            _ => step.SuggestedAnswer ?? "true"
        };
    }

    private static string StableAnswerKey(string? title, string? message, string stepId)
    {
        var titleKey = NormalizeKey(title);
        if (!string.IsNullOrWhiteSpace(titleKey))
            return titleKey;

        var messageKey = NormalizeKey(message);
        return !string.IsNullOrWhiteSpace(messageKey) ? messageKey : stepId;
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return s_normalizeKeyRegex.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
    }

    private sealed record AnswerResolution(bool Success, bool HasAnswer, string Answer, string? Error)
    {
        public static AnswerResolution Ok(string answer) => new(true, true, answer, null);
        public static AnswerResolution Fail(string error) => new(false, false, "", error);

        public static AnswerResolution Continue() => new(true, false, "", null);
    }

    private sealed class WizardFatalException(string message) : Exception(message);

    private sealed record WizardPayload(
        bool IsDone,
        string? SessionId,
        string StepId,
        string StepType,
        string Title,
        string Message,
        string InitialValue,
        bool Sensitive,
        int StepIndex,
        int TotalSteps,
        IReadOnlyList<WizardOptionValue> Options,
        string? Error)
    {
        public static WizardPayload Parse(JsonElement payload)
        {
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return ErrorPayload("Gateway returned an empty wizard response.");

            try
            {
                var sessionId = payload.TryGetProperty("sessionId", out var sessionIdProperty)
                    ? sessionIdProperty.GetString()
                    : null;

                if (payload.TryGetProperty("done", out var doneProperty) && doneProperty.ValueKind == JsonValueKind.True)
                {
                    var status = payload.TryGetProperty("status", out var statusProperty) ? statusProperty.ToString() : "";
                    var error = payload.TryGetProperty("error", out var errorProperty) ? errorProperty.ToString() : null;
                    return new(true, sessionId, "", "", "", "", "", false, 0, 0, [], 
                        string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ? error ?? "Wizard returned error status." : null);
                }

                if (!payload.TryGetProperty("step", out var step) || step.ValueKind != JsonValueKind.Object)
                    return ErrorPayload("Gateway wizard response is missing a step object.");

                var rawType = step.TryGetProperty("type", out var typeProperty) ? typeProperty.ToString() : "note";
                var type = string.IsNullOrWhiteSpace(rawType) ? "note" : rawType.Trim().ToLowerInvariant();
                var title = step.TryGetProperty("title", out var titleProperty) ? titleProperty.ToString() : "";
                var message = step.TryGetProperty("message", out var messageProperty) ? messageProperty.ToString() : "";
                var stepId = step.TryGetProperty("id", out var idProperty) ? idProperty.ToString() : "";
                var initialValue = step.TryGetProperty("initialValue", out var initialProperty)
                    ? WizardAnswerBuilder.ValueKey(initialProperty)
                    : "";
                var sensitive = step.TryGetProperty("sensitive", out var sensitiveProperty) && sensitiveProperty.ValueKind == JsonValueKind.True;
                var stepIndex = payload.TryGetProperty("stepIndex", out var indexProperty) && indexProperty.TryGetInt32(out var index) ? index : 0;
                var totalSteps = payload.TryGetProperty("totalSteps", out var totalProperty) && totalProperty.TryGetInt32(out var total) ? total : 0;

                var options = WizardAnswerBuilder.ReadOptions(step);

                return new(false, sessionId, stepId, type, title, message, initialValue, sensitive, stepIndex, totalSteps, options, null);
            }
            catch (Exception ex)
            {
                return ErrorPayload($"Could not parse gateway wizard response: {ex.Message}");
            }
        }

        private static WizardPayload ErrorPayload(string error) => new(false, null, "", "", "", "", "", false, 0, 0, [], error);
    }

    private sealed record WizardTemplateStep(
        string StepId,
        string Type,
        string Title,
        string Message,
        bool Sensitive,
        string? SuggestedAnswer,
        IReadOnlyList<WizardOptionValue> Options,
        IReadOnlyList<string> AuthUrls)
    {
        public static WizardTemplateStep From(WizardPayload payload)
        {
            var suggested = payload.StepType switch
            {
                "note" or "confirm" => "true",
                "select" => !string.IsNullOrWhiteSpace(payload.InitialValue)
                    ? payload.InitialValue
                    : null,
                "multiselect" or "text" => !string.IsNullOrWhiteSpace(payload.InitialValue)
                    ? payload.InitialValue
                    : null,
                _ => !string.IsNullOrWhiteSpace(payload.InitialValue) ? payload.InitialValue : null
            };

            if (payload.Sensitive && !string.IsNullOrWhiteSpace(suggested))
                suggested = "<sensitive value>";

            var authUrls = WizardMessageFormatting.ExtractUrls($"{payload.Title}\n{payload.Message}");

            return new(payload.StepId, payload.StepType, payload.Title, payload.Message, payload.Sensitive, suggested, payload.Options, authUrls);
        }
    }
}
