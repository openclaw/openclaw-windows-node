using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Parses wizard step JSON payloads from gateway wizard.start/wizard.next responses.
/// Extracted from WizardPage.ApplyStep for testability.
/// </summary>
public static class WizardStepParser
{
    public record ParsedStep(
        bool IsDone,
        string? SessionId,
        string StepType,
        string Title,
        string Message,
        string StepId,
        string[] OptionLabels,
        string[] OptionValues,
        string[] OptionHints,
        string InitialValue,
        string Placeholder,
        bool Sensitive,
        int StepNumber,
        int TotalSteps,
        string? Error
    );

    public static ParsedStep Parse(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
        {
            return new ParsedStep(
                IsDone: false, SessionId: null, StepType: "", Title: "", Message: "",
                StepId: "", OptionLabels: [], OptionValues: [], OptionHints: [],
                InitialValue: "", Placeholder: "", Sensitive: false,
                StepNumber: 0, TotalSteps: 0, Error: "Empty response from gateway");
        }

        try
        {
            string? sessionId = null;
            if (payload.TryGetProperty("sessionId", out var sidProp))
                sessionId = sidProp.GetString() ?? "";

            // Check for completion
            if (payload.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
            {
                return new ParsedStep(
                    IsDone: true, SessionId: sessionId, StepType: "", Title: "", Message: "",
                    StepId: "", OptionLabels: [], OptionValues: [], OptionHints: [],
                    InitialValue: "", Placeholder: "", Sensitive: false,
                    StepNumber: 0, TotalSteps: 0, Error: null);
            }

            if (!payload.TryGetProperty("step", out var step))
            {
                return new ParsedStep(
                    IsDone: false, SessionId: sessionId, StepType: "", Title: "", Message: "",
                    StepId: "", OptionLabels: [], OptionValues: [], OptionHints: [],
                    InitialValue: "", Placeholder: "", Sensitive: false,
                    StepNumber: 0, TotalSteps: 0, Error: "Missing 'step' property");
            }

            var typeStr = step.TryGetProperty("type", out var tp) ? tp.ToString() : "note";
            var title = step.TryGetProperty("title", out var t) ? t.ToString() : "";
            var message = step.TryGetProperty("message", out var m) ? m.ToString() : "";

            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(message))
                title = typeStr switch { "confirm" => "Confirm", "select" => "Select", "text" => "Input", _ => "Setup" };

            var stepId = step.TryGetProperty("id", out var id) ? id.ToString() : "";
            var placeholder = step.TryGetProperty("placeholder", out var ph) ? ph.ToString() : "";
            var initialValue = step.TryGetProperty("initialValue", out var ivp) ? ivp.ToString() : "";
            var sensitive = step.TryGetProperty("sensitive", out var sp) && sp.GetBoolean();

            // Parse options
            var labels = new List<string>();
            var values = new List<string>();
            var hints = new List<string>();

            if (step.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
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
            }

            int stepNumber = 0, totalSteps = 0;
            if (payload.TryGetProperty("stepIndex", out var si))
                stepNumber = si.GetInt32();
            if (payload.TryGetProperty("totalSteps", out var ts))
                totalSteps = ts.GetInt32();

            return new ParsedStep(
                IsDone: false,
                SessionId: sessionId,
                StepType: typeStr,
                Title: title,
                Message: message,
                StepId: stepId,
                OptionLabels: labels.ToArray(),
                OptionValues: values.ToArray(),
                OptionHints: hints.ToArray(),
                InitialValue: initialValue,
                Placeholder: placeholder,
                Sensitive: sensitive,
                StepNumber: stepNumber,
                TotalSteps: totalSteps,
                Error: null
            );
        }
        catch (Exception ex)
        {
            return new ParsedStep(
                IsDone: false, SessionId: null, StepType: "", Title: "", Message: "",
                StepId: "", OptionLabels: [], OptionValues: [], OptionHints: [],
                InitialValue: "", Placeholder: "", Sensitive: false,
                StepNumber: 0, TotalSteps: 0, Error: ex.Message);
        }
    }
}
