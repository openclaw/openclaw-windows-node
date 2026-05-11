using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 3: RPC-Driven Wizard — server-defined setup steps.
/// Reads/writes wizard state from OnboardingState so it persists across page navigations.
/// Falls back to an offline skip message when gateway is unreachable.
/// </summary>
public sealed class WizardPage : Component<OnboardingState>
{
    private static readonly Regex UrlInMessagePattern = new(
        @"(https?://[^\s\)\"",]+)",
        RegexOptions.Compiled);

    private static readonly Regex DeviceCodePattern = new(
        @"(?:^|\s)(?:[Cc]ode|user_code|USER_CODE)\s*[:=]\s*([A-Z0-9]{2,8}(?:-[A-Z0-9]{2,8})+|[A-Z0-9]{4,12})\b",
        RegexOptions.Compiled);
    public override Element Render()
    {
        // Read persisted wizard state from shared OnboardingState
        var (wizardState, setWizardState) = UseState(Props.WizardLifecycleState ?? "loading");
        var (stepTitle, setStepTitle) = UseState("");
        var (stepMessage, setStepMessage) = UseState("");
        var (stepType, setStepType) = UseState("note");
        var (optionLabels, setOptionLabels) = UseState(Array.Empty<string>());
        var (optionValues, setOptionValues) = UseState(Array.Empty<string>());
        var (optionHints, setOptionHints) = UseState(Array.Empty<string>());
        var (stepId, setStepId) = UseState("");
        var (stepInput, setStepInput) = UseState("");
        var (stepNumber, setStepNumber) = UseState(0);
        var (totalSteps, setTotalSteps) = UseState(0);
        var (errorMsg, setErrorMsg) = UseState(Props.WizardError ?? "");
        var (placeholder, setPlaceholder) = UseState("");
        var (submitting, setSubmitting) = UseState(false);

        void SaveState(string state, string? error = null)
        {
            Props.WizardLifecycleState = state;
            Props.WizardError = error;
        }

        void ApplyStep(JsonElement payload)
        {
            // Guard against default/undefined JsonElement
            if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
            {
                setErrorMsg(LocalizationHelper.GetString("Onboarding_Wizard_ErrorEmptyGatewayResponse"));
                setWizardState("error");
                SaveState("error", LocalizationHelper.GetString("Onboarding_Wizard_ErrorEmptyGatewayResponse"));
                return;
            }

            try
            {
            // Extract sessionId from wizard.start response
            if (payload.TryGetProperty("sessionId", out var sidProp))
            {
                var sid = sidProp.GetString() ?? "";
                Props.WizardSessionId = sid;
            }

            // Store payload for persistence
            Props.WizardStepPayload = payload;

            // Check for completion
            if (payload.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
            {
                setWizardState("complete");
                SaveState("complete");
                return;
            }

            // Extract step fields — use ToString() instead of GetString() to handle non-string values
            if (payload.TryGetProperty("step", out var step))
            {
                var typeStr = step.TryGetProperty("type", out var tp) ? tp.ToString() : "note";
                var newTitle = step.TryGetProperty("title", out var t) ? t.ToString() : "";
                var newMessage = step.TryGetProperty("message", out var m) ? m.ToString() : "";
                // If no title, use the type as a fallback label
                if (string.IsNullOrEmpty(newTitle) && !string.IsNullOrEmpty(newMessage))
                    newTitle = typeStr switch { "confirm" => "Confirm", "select" => "Select", "text" => "Input", _ => "Setup" };
                setStepTitle(newTitle);
                setStepMessage(newMessage);
                setStepType(typeStr);
                setStepId(step.TryGetProperty("id", out var id) ? id.ToString() : "");
                setPlaceholder(step.TryGetProperty("placeholder", out var ph) ? ph.ToString() : "");
                var iv = step.TryGetProperty("initialValue", out var ivp) ? ivp.ToString() : "";
                setStepInput(iv);

                // Parse options — may be plain strings OR objects {value, label, hint}
                if (step.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    var labels = new List<string>();
                    var values = new List<string>();
                    var hints = new List<string>();
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.Object)
                        {
                            var label = o.TryGetProperty("label", out var lp) ? lp.ToString() : "";
                            var value = o.TryGetProperty("value", out var vp) ? vp.ToString() : label;
                            var hint = o.TryGetProperty("hint", out var hp) ? hp.ToString() : "";
                            labels.Add(string.IsNullOrEmpty(hint) ? label : $"{label} — {hint}");
                            values.Add(value);
                            hints.Add(hint);
                        }
                        else
                        {
                            var s = o.ToString();
                            labels.Add(s);
                            values.Add(s);
                            hints.Add("");
                        }
                    }
                    setOptionLabels(labels.ToArray());
                    setOptionValues(values.ToArray());
                    setOptionHints(hints.ToArray());
                }
                else
                {
                    setOptionLabels(Array.Empty<string>());
                    setOptionValues(Array.Empty<string>());
                    setOptionHints(Array.Empty<string>());
                }

                // For select: default to first option's value if no initialValue
                var typeStr2 = step.TryGetProperty("type", out var tp3) ? tp3.ToString() : "";
                if (string.IsNullOrEmpty(iv) && typeStr2 == "select")
                {
                    // Re-read first option value directly
                    if (step.TryGetProperty("options", out var opts2) && opts2.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var o in opts2.EnumerateArray())
                        {
                            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("value", out var fv))
                            {
                                setStepInput(fv.ToString());
                                break;
                            }
                            else
                            {
                                setStepInput(o.ToString());
                                break;
                            }
                        }
                    }
                }
            }

            if (payload.TryGetProperty("stepIndex", out var si))
                setStepNumber(si.GetInt32());
            if (payload.TryGetProperty("totalSteps", out var ts))
                setTotalSteps(ts.GetInt32());

            setWizardState("active");
            SaveState("active");
            }
            catch (Exception ex)
            {
                setErrorMsg(ex.Message);
                setWizardState("error");
                SaveState("error", ex.Message);
            }
        }

        // Start wizard on mount only (empty dependency array = run once)
        UseEffect(() =>
        {
            async void StartWizard()
            {
                // If wizard already has a session, restore from saved payload
                if (!string.IsNullOrEmpty(Props.WizardSessionId) && Props.WizardStepPayload.HasValue)
                {
                    ApplyStep(Props.WizardStepPayload.Value);
                    return;
                }

                // If previously completed or in error, restore that state
                if (Props.WizardLifecycleState is "complete" or "offline")
                {
                    setWizardState(Props.WizardLifecycleState);
                    return;
                }

                // Read client from App directly (persistent singleton, not Props)
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                var client = app.GatewayClient ?? Props.GatewayClient;

                // Show loading UX and poll for client + connection (up to 30s)
                setWizardState("loading");
                setErrorMsg("");

                for (int wait = 0; wait < 30; wait++)
                {
                    client = app.GatewayClient ?? Props.GatewayClient;
                    if (client?.IsConnectedToGateway == true) break;
                    await Task.Delay(1000);
                }

                if (client == null || !client.IsConnectedToGateway)
                {
                    setWizardState("offline");
                    SaveState("offline");
                    return;
                }

                try
                {
                    var response = await client.SendWizardRequestAsync("wizard.start");
                    ApplyStep(response);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
                {
                    // Wizard session exists — try to get current status instead of restarting
                    Logger.Info("[Wizard] Session already running, fetching current status...");
                    try
                    {
                        var response = await client.SendWizardRequestAsync("wizard.status");
                        ApplyStep(response);
                    }
                    catch
                    {
                        // wizard.status not available — skip wizard gracefully
                        Logger.Warn("[Wizard] Could not resume existing wizard session, skipping");
                        setWizardState("offline");
                        SaveState("offline");
                    }
                }
                catch (TimeoutException)
                {
                    setWizardState("offline");
                    SaveState("offline");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    setWizardState("offline");
                    SaveState("offline");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Wizard] Start failed: {ex}");
                    var genericMsg = "Failed to start wizard setup";
                    setErrorMsg(genericMsg);
                    setWizardState("error");
                    SaveState("error", genericMsg);
                }
            }
            StartWizard();
        }, Array.Empty<object>());

        async void SubmitStep()
        {
            // Read client from App directly (same as StartWizard — Props.GatewayClient may be null)
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var client = app.GatewayClient ?? Props.GatewayClient;
            if (client == null) return;

            if (!client.IsConnectedToGateway)
            {
                setErrorMsg(LocalizationHelper.GetString("Onboarding_Wizard_ErrorGatewayDisconnectedDetail"));
                setWizardState("error");
                SaveState("error", LocalizationHelper.GetString("Onboarding_Wizard_ErrorGatewayDisconnected"));
                return;
            }

            // If retrying from error state, restore active state
            if (wizardState == "error")
            {
                setWizardState("active");
                setErrorMsg("");
            }

            setSubmitting(true);
            try
            {
                // All step types need an answer to advance.
                // For note/confirm: send "true". For text/select: send user input.
                var answerValue = string.IsNullOrEmpty(stepInput) ? "true" : stepInput;

                // Smart timeout: 5min for auth-related steps (device code polling), 30s for everything else
                var isAuthStep = !string.IsNullOrEmpty(stepMessage) &&
                    (stepMessage.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                     stepMessage.Contains("authorize", StringComparison.OrdinalIgnoreCase) ||
                     stepMessage.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                     stepMessage.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
                     stepMessage.Contains("OAuth", StringComparison.OrdinalIgnoreCase));
                var timeoutMs = isAuthStep ? 300_000 : 30_000;

                var response = await client.SendWizardRequestAsync("wizard.next", new
                {
                    sessionId = Props.WizardSessionId ?? "",
                    answer = new { stepId, value = answerValue }
                }, timeoutMs: timeoutMs);

                // Validate response before applying
                if (response.ValueKind == JsonValueKind.Undefined || response.ValueKind == JsonValueKind.Null)
                {
                    setErrorMsg(LocalizationHelper.GetString("Onboarding_Wizard_ErrorEmptyNextResponse"));
                    setWizardState("error");
                    SaveState("error", LocalizationHelper.GetString("Onboarding_Wizard_ErrorEmptyNextResponse"));
                }
                else
                {
                    ApplyStep(response);
                }
            }
            catch (Exception ex)
            {
                // SECURITY: Log full exception, show only generic error type to user
                Logger.Error($"[Wizard] Step '{stepId}' ({stepType}) failed: {ex}");
                var msg = LocalizationHelper.GetString("Onboarding_Wizard_StepError");
                if (msg == "Onboarding_Wizard_StepError") msg = "An error occurred processing this step";
                setErrorMsg(msg);
                setWizardState("error");
                SaveState("error", msg);
            }
            finally
            {
                await Task.Delay(400); // Brief delay so button disable is visible
                setSubmitting(false);
            }
        }

        async void SkipStep()
        {
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var client = app.GatewayClient ?? Props.GatewayClient;
            if (client == null) return;

            if (!client.IsConnectedToGateway)
            {
                setErrorMsg(LocalizationHelper.GetString("Onboarding_Wizard_ErrorGatewayDisconnectedDetail"));
                setWizardState("error");
                SaveState("error", LocalizationHelper.GetString("Onboarding_Wizard_ErrorGatewayDisconnected"));
                return;
            }

            setSubmitting(true);
            try
            {
                // Send a proper skip answer based on step type:
                // - confirm: "false" (decline)
                // - select/multiselect: NO answer (gateway keeps current value)
                // - note/text/other: "true" to acknowledge and advance
                object parameters;
                if (stepType == "confirm")
                {
                    parameters = new { sessionId = Props.WizardSessionId ?? "", answer = new { stepId, value = "false" } };
                }
                else if (stepType is "select" or "multiselect")
                {
                    // No answer — gateway keeps current value or skips
                    parameters = new { sessionId = Props.WizardSessionId ?? "" };
                }
                else
                {
                    // note, text, etc. — send "true" to acknowledge (gateway repeats step if no answer)
                    parameters = new { sessionId = Props.WizardSessionId ?? "", answer = new { stepId, value = "true" } };
                }

                var response = await client.SendWizardRequestAsync("wizard.next", parameters);
                ApplyStep(response);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Wizard] Skip step failed: {ex}");
                var msg = LocalizationHelper.GetString("Onboarding_Wizard_StepError");
                if (msg == "Onboarding_Wizard_StepError") msg = "An error occurred processing this step";
                setErrorMsg(msg);
                setWizardState("error");
                SaveState("error", msg);
            }
            finally
            {
                await Task.Delay(400); // Brief delay so button disable is visible
                setSubmitting(false);
            }
        }

        // Always render exactly the same element tree structure.
        // Use empty strings for unused fields to keep a consistent child count.
        string displayTitle = "";
        string displayMessage = "";
        Element inputArea = TextBlock(""); // placeholder for input controls
        string buttonLabel1 = LocalizationHelper.GetString("Onboarding_Wizard_Continue");
        string buttonLabel2 = LocalizationHelper.GetString("Onboarding_Wizard_Skip");
        bool showButtons = false;

        switch (wizardState)
        {
            case "active":
                displayTitle = stepTitle;
                displayMessage = stepMessage;
                showButtons = true;

                // Check sensitive flag from stored payload
                bool isSensitive = false;
                if (Props.WizardStepPayload.HasValue)
                {
                    var sp = Props.WizardStepPayload.Value;
                    if (sp.TryGetProperty("step", out var ss))
                        isSensitive = ss.TryGetProperty("sensitive", out var sv) && sv.ValueKind == JsonValueKind.True;
                }

                if (stepType == "text")
                {
                    // Use PasswordBox for sensitive inputs (API keys, tokens)
                    if (isSensitive)
                    {
                        inputArea = PasswordBox(stepInput, v => setStepInput(v),
                            placeholderText: string.IsNullOrEmpty(placeholder) ? "Enter value..." : placeholder);
                    }
                    else
                    {
                        inputArea = TextField(stepInput, v => setStepInput(v),
                            placeholder: string.IsNullOrEmpty(placeholder) ? "Enter value..." : placeholder);
                    }
                }
                else if (stepType == "select" || stepType == "multiselect")
                {
                    // Read options directly from stored payload to avoid state timing issues
                    var labels = new List<string>();
                    var values = new List<string>();
                    if (Props.WizardStepPayload.HasValue)
                    {
                        var p = Props.WizardStepPayload.Value;
                        if (p.TryGetProperty("step", out var s) && s.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var o in opts.EnumerateArray())
                            {
                                if (o.ValueKind == JsonValueKind.Object)
                                {
                                    var label = o.TryGetProperty("label", out var lp) ? lp.ToString() : "";
                                    var value = o.TryGetProperty("value", out var vp) ? vp.ToString() : label;
                                    var hint = o.TryGetProperty("hint", out var hp) ? hp.ToString() : "";
                                    labels.Add(string.IsNullOrEmpty(hint) ? label : $"{label} — {hint}");
                                    values.Add(value);
                                }
                                else
                                {
                                    labels.Add(o.ToString());
                                    values.Add(o.ToString());
                                }
                            }
                        }
                    }

                    if (labels.Count > 0)
                    {
                        var selIdx = values.IndexOf(stepInput);
                        var labelsArr = labels.ToArray();
                        var valuesArr = values.ToArray();
                        inputArea = RadioButtons(labelsArr, selIdx >= 0 ? selIdx : 0,
                            idx =>
                            {
                                if (idx >= 0 && idx < valuesArr.Length)
                                    setStepInput(valuesArr[idx]);
                            })
                            .Set(rb => { rb.MaxColumns = 1; rb.MaxWidth = 400; });
                    }
                    else
                    {
                        inputArea = TextBlock(LocalizationHelper.GetString("Onboarding_Wizard_NoOptionsAvailable")).FontSize(12).Opacity(0.5);
                        showButtons = false; // Don't allow submit with no valid selection
                    }
                }
                else if (stepType == "confirm")
                {
                    buttonLabel1 = LocalizationHelper.GetString("Onboarding_Wizard_Yes");
                    buttonLabel2 = LocalizationHelper.GetString("Onboarding_Wizard_NoSkip");
                }
                else if (stepType == "progress")
                {
                    // Show spinner while gateway polls for auth completion
                    inputArea = HStack(8,
                        ProgressRing().Width(24).Height(24),
                        TextBlock(LocalizationHelper.GetString("Onboarding_Wizard_Waiting")).FontSize(13).Opacity(0.7)
                            .VAlign(VerticalAlignment.Center)
                    );
                    showButtons = false; // Gateway auto-advances on completion
                }

                break;

            case "complete":
                displayTitle = $"✅ {LocalizationHelper.GetString("Onboarding_Wizard_Complete")}";
                displayMessage = LocalizationHelper.GetString("Onboarding_Wizard_ClickNextToContinue");
                break;

            case "error":
                displayTitle = $"❌ {LocalizationHelper.GetString("Onboarding_Wizard_ErrorTitle")}";
                displayMessage = errorMsg;
                showButtons = true;
                buttonLabel1 = LocalizationHelper.GetString("Onboarding_Retry");
                buttonLabel2 = LocalizationHelper.GetString("Onboarding_Wizard_SkipWizard");
                break;

            case "loading":
                displayTitle = $"🔄 {LocalizationHelper.GetString("Onboarding_Connection_StatusAuthenticating")}";
                displayMessage = LocalizationHelper.GetString("Onboarding_Wizard_ConnectingToGateway");
                inputArea = HStack(8,
                    ProgressRing().Width(24).Height(24),
                    TextBlock(LocalizationHelper.GetString("Onboarding_Wizard_ConnectionWaitDetail"))
                        .FontSize(13).Opacity(0.7)
                        .VAlign(VerticalAlignment.Center)
                );
                break;

            default:
                displayTitle = $"🔌  {LocalizationHelper.GetString("Onboarding_Wizard_Offline")}";
                displayMessage = $"{LocalizationHelper.GetString("Onboarding_Wizard_OfflineMessage")}\n\n{LocalizationHelper.GetString("Onboarding_Wizard_ClickNextToContinue")}";
                break;
        }

        // Detect URLs and device codes in the message for auth flows
        Element urlButton = TextBlock(""); // placeholder
        Element deviceCodeDisplay = TextBlock(""); // placeholder

        if (!string.IsNullOrEmpty(displayMessage))
        {
            // URL detection — find https:// URLs in the message
            var urlMatch = UrlInMessagePattern.Match(displayMessage);
            if (urlMatch.Success)
            {
                var detectedUrl = urlMatch.Value;
                urlButton = Button($"🌐 Open in browser: {detectedUrl}", () =>
                {
                    try
                    {
                        if (Uri.TryCreate(detectedUrl, UriKind.Absolute, out var btnUri) && (btnUri.Scheme == "https" || btnUri.Scheme == "http"))
                        _ = global::Windows.System.Launcher.LaunchUriAsync(btnUri);
                    }
                    catch { }
                }).HAlign(HorizontalAlignment.Left);

                // Auto-open the browser on first render of this step
                // (UseEffect runs once per step since stepId changes)
            }

            // Device code detection — look for "Code: XXXX-XXXX" or similar.
            // Capture must contain a digit or hyphen (or be all uppercase) to avoid
            // matching common English words like "below" that follow "code".
            // Case-sensitive on the value to require the GitHub-style uppercase code.
            var codeMatch = DeviceCodePattern.Match(displayMessage);
            if (codeMatch.Success)
            {
                var code = codeMatch.Groups[1].Value;
                deviceCodeDisplay = Border(
                    HStack(12,
                        TextBlock(code)
                            .FontSize(28)
                            .FontFamily("Consolas")
                            .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                            .VAlign(VerticalAlignment.Center),
                        Button("Copy", () =>
                        {
                            try
                            {
                                var dp = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
                                dp.SetText(code);
                                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                            }
                            catch { }
                        }).VAlign(VerticalAlignment.Center)
                    ).Padding(12)
                )
                .CornerRadius(6)
                .BackgroundResource("SystemFillColorAttentionBackgroundBrush")
                .HAlign(HorizontalAlignment.Center);
            }
        }

        // Auto-open browser for auth URLs when a new step arrives
        UseEffect(() =>
        {
            if (!string.IsNullOrEmpty(displayMessage))
            {
                var urlMatch = UrlInMessagePattern.Match(displayMessage);
                if (urlMatch.Success)
                {
                    try
                    {
                        if (Uri.TryCreate(urlMatch.Value, UriKind.Absolute, out var autoUri) && (autoUri.Scheme == "https" || autoUri.Scheme == "http"))
                        _ = global::Windows.System.Launcher.LaunchUriAsync(autoUri);
                    }
                    catch { }
                }
            }
        }, stepId); // Re-runs when stepId changes (new step)

        return VStack(8,
            TextBlock(LocalizationHelper.GetString("Onboarding_Wizard_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            Border(
                ScrollView(
                    VStack(10,
                        TextBlock(displayTitle)
                            .FontSize(15)
                            .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                            .TextWrapping(),
                        TextBlock(displayMessage)
                            .FontSize(13)
                            .TextWrapping(),
                        inputArea
                    ).Padding(16).MaxWidth(420)
                ).HorizontalScrollMode(Microsoft.UI.Xaml.Controls.ScrollMode.Disabled)
            )
            .CornerRadius(8)
            .BackgroundResource("CardBackgroundFillColorDefaultBrush")
            .MaxHeight(350),

            // Device code display (large, copyable — for auth flows)
            deviceCodeDisplay,

            // "Open in browser" button (for auth URLs)
            urlButton,

            showButtons
                ? HStack(8,
                    Button(buttonLabel1, SubmitStep).Disabled(submitting),
                    Button(buttonLabel2, SkipStep).Disabled(submitting))
                : TextBlock(""),

            totalSteps > 0 && wizardState == "active"
                ? TextBlock($"Step {stepNumber + 1} of {totalSteps}")
                    .FontSize(12).Opacity(0.5).HAlign(HorizontalAlignment.Center)
                : TextBlock("")
        )
        .MaxWidth(460)
        .Padding(0, 8, 0, 0);
    }
}
