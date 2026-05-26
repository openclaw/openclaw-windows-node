using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class SetupWizardRunner
{
    private const int MaxWizardSteps = 50;
    private const int MaxSameStepVisits = 3;
    private static readonly Regex s_normalizeKeyRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private readonly SetupContext _ctx;

    public SetupWizardRunner(SetupContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<StepResult> RunAsync(CancellationToken ct)
    {
        var registry = new GatewayRegistry(_ctx.DataDir);
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

        try
        {
            client = CreateWizardClient(credential, identityPath, wsLogger);
            var connection = await PairOperatorStep.WaitForConnectionOrPairing(client, _ctx, TimeSpan.FromSeconds(_ctx.Config.Pairing.TimeoutSeconds), ct);
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
                connection = await PairOperatorStep.WaitForConnectionOrPairing(client, _ctx, TimeSpan.FromSeconds(_ctx.Config.Pairing.TimeoutSeconds), ct);
            }

            if (connection != PairOperatorStep.ConnectionOutcome.Connected)
                return StepResult.Fail($"Cannot run gateway wizard because operator connection failed: {connection}");

            _ctx.Logger.Info("Starting gateway wizard");
            var payload = await client.SendWizardRequestAsync("wizard.start", timeoutMs: _ctx.Config.Wizard.RequestTimeoutSeconds * 1000);
            wizardStarted = true;

            var visits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var restartAttempts = 0;
            for (var i = 0; i < MaxWizardSteps; i++)
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
                    : $"Wizard step '{parsed.StepId}' ({parsed.StepType}) continuing without explicit answer");

                var parameters = answerResult.HasAnswer
                    ? new
                    {
                        sessionId,
                        answer = new
                        {
                            stepId = parsed.StepId,
                            value = AnswerValueForWire(parsed, answerResult.Answer)
                        }
                    }
                    : (object)new { sessionId };

                try
                {
                    payload = await client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutFor(parsed));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsRestartLikeWizardDisconnect(ex) && restartAttempts < 2)
                {
                    restartAttempts++;
                    _ctx.Logger.Warn($"Gateway restarted during wizard; reconnecting and replaying answers (attempt {restartAttempts}/2): {ex.Message}");

                    try { await client.DisconnectAsync(); } catch { }
                    client.Dispose();

                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    client = CreateWizardClient(credential, identityPath, wsLogger);
                    var reconnect = await PairOperatorStep.WaitForConnectionOrPairing(client, _ctx, TimeSpan.FromSeconds(_ctx.Config.Pairing.TimeoutSeconds), ct);
                    if (reconnect != PairOperatorStep.ConnectionOutcome.Connected)
                        return StepResult.Fail($"Gateway wizard reconnect failed after restart: {reconnect}");

                    sessionId = "";
                    visits.Clear();
                    discoveredSteps.Clear();
                    payload = await client.SendWizardRequestAsync("wizard.start", timeoutMs: _ctx.Config.Wizard.RequestTimeoutSeconds * 1000);
                }
            }

            return StepResult.Fail($"Gateway wizard exceeded {MaxWizardSteps} steps.");
        }
        catch (OperationCanceledException)
        {
            if (client is not null && wizardStarted && !string.IsNullOrWhiteSpace(sessionId))
                await TryCancelWizardAsync(client, sessionId);
            throw;
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Gateway wizard failed: {ex.Message}", ex);
        }
        finally
        {
            if (client is not null && wizardStarted && !wizardCompleted && !string.IsNullOrWhiteSpace(sessionId))
                await TryCancelWizardAsync(client, sessionId);

            if (wizardStarted)
                await TryResetReloadModeAsync();

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
            await client.SendWizardRequestAsync("wizard.cancel", new { sessionId }, timeoutMs: _ctx.Config.Wizard.RequestTimeoutSeconds * 1000);
        }
        catch (Exception ex)
        {
            _ctx.Logger.Warn($"Failed to cancel gateway wizard session: {ex.Message}");
        }
    }

    private async Task TryResetReloadModeAsync()
    {
        try
        {
            var result = await _ctx.Commands.RunInWslAsync(
                _ctx.DistroName!,
                $"{_ctx.WslPathPrefix} && openclaw config set gateway.reload.mode hybrid",
                _ctx.WslCommandTimeout,
                ct: CancellationToken.None);

            if (result.ExitCode == 0)
                _ctx.Logger.Info("Reset gateway.reload.mode to hybrid after wizard");
            else
                _ctx.Logger.Warn($"Failed to reset gateway.reload.mode after wizard (exit {result.ExitCode}): {result.Stderr.Trim()}");
        }
        catch (Exception ex)
        {
            _ctx.Logger.Warn($"Failed to reset gateway.reload.mode after wizard: {ex.Message}");
        }
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

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(basePath, json);
        _ctx.Logger.Info($"Wizard answer template written: {basePath}");
        return basePath;
    }

    private static AnswerResolution ResolveAnswer(WizardPayload step, Dictionary<string, string>? configuredAnswers)
    {
        if (TryGetConfiguredAnswer(step, configuredAnswers, out var configured))
            return ValidateAnswer(step, configured, configuredAnswer: true);

        var inferred = step.StepType switch
        {
            "note" => "true",
            "confirm" => InferConfirmAnswer(step),
            "select" => InferOptionAnswer(step),
            "multiselect" => InferOptionAnswer(step),
            "text" => InferTextAnswer(step),
            _ => !string.IsNullOrWhiteSpace(step.InitialValue) ? step.InitialValue : null
        };

        if (inferred == null)
        {
            return AnswerResolution.Fail($"Gateway wizard step '{step.StepId}' ({step.StepType}) requires a text answer.");
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
        if (step.StepType == "confirm" && bool.TryParse(answer, out var booleanAnswer))
            return booleanAnswer;

        if (step.StepType == "multiselect")
        {
            if (string.Equals(answer, "__skip__", StringComparison.Ordinal))
                return new[] { "__skip__" };

            return SplitMultiSelect(answer);
        }

        return answer;
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
            if (!step.Options.Any(o => string.Equals(o.Value, answer, StringComparison.Ordinal)))
            {
                var source = configuredAnswer ? "configured" : "default";
                return AnswerResolution.Fail($"The {source} answer for wizard step '{step.StepId}' is not one of the gateway-provided options.");
            }
        }
        else if (step.StepType == "multiselect")
        {
            var values = SplitMultiSelect(answer);
            if (values.Length == 0)
                return AnswerResolution.Fail($"Gateway wizard step '{step.StepId}' requires at least one selected value.");

            var validValues = step.Options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            var invalid = values.FirstOrDefault(v => !validValues.Contains(v));
            if (invalid != null)
                return AnswerResolution.Fail($"Wizard step '{step.StepId}' has invalid selected value '{invalid}'.");
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

    private int TimeoutFor(WizardPayload step)
    {
        var text = $"{step.Title} {step.Message}";
        var isAuthStep = text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oauth", StringComparison.OrdinalIgnoreCase);
        return (isAuthStep
            ? _ctx.Config.Wizard.AuthStepTimeoutSeconds
            : _ctx.Config.Wizard.RequestTimeoutSeconds) * 1000;
    }

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
            "text" => step.Sensitive ? "<sensitive value>" : "<text value>",
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

    private static string[] SplitMultiSelect(string stepInput) =>
        stepInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
    }

    private sealed record WizardOption(string Value, string Label, string Hint);

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
        IReadOnlyList<WizardOption> Options,
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

                var type = step.TryGetProperty("type", out var typeProperty) ? typeProperty.ToString() : "note";
                var title = step.TryGetProperty("title", out var titleProperty) ? titleProperty.ToString() : "";
                var message = step.TryGetProperty("message", out var messageProperty) ? messageProperty.ToString() : "";
                var stepId = step.TryGetProperty("id", out var idProperty) ? idProperty.ToString() : "";
                var initialValue = step.TryGetProperty("initialValue", out var initialProperty) ? initialProperty.ToString() : "";
                var sensitive = step.TryGetProperty("sensitive", out var sensitiveProperty) && sensitiveProperty.ValueKind == JsonValueKind.True;
                var stepIndex = payload.TryGetProperty("stepIndex", out var indexProperty) && indexProperty.TryGetInt32(out var index) ? index : 0;
                var totalSteps = payload.TryGetProperty("totalSteps", out var totalProperty) && totalProperty.TryGetInt32(out var total) ? total : 0;

                var options = new List<WizardOption>();
                if (step.TryGetProperty("options", out var optionsProperty) && optionsProperty.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionsProperty.EnumerateArray())
                    {
                        if (option.ValueKind == JsonValueKind.Object)
                        {
                            var label = option.TryGetProperty("label", out var labelProperty) ? labelProperty.ToString() : "";
                            var value = option.TryGetProperty("value", out var valueProperty) ? valueProperty.ToString() : label;
                            var hint = option.TryGetProperty("hint", out var hintProperty) ? hintProperty.ToString() : "";
                            options.Add(new(value, label, hint));
                        }
                        else
                        {
                            var value = option.ToString();
                            options.Add(new(value, value, ""));
                        }
                    }
                }

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
        IReadOnlyList<WizardOption> Options)
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

            return new(payload.StepId, payload.StepType, payload.Title, payload.Message, payload.Sensitive, suggested, payload.Options);
        }
    }
}
