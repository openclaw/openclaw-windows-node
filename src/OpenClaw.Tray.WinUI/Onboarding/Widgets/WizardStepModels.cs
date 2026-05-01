using System;

namespace OpenClawTray.Onboarding.Widgets;

public enum WizardStepType
{
    Note,
    Text,
    Confirm,
    Select,
    MultiSelect,
    Progress,
    Action,
}

public record WizardStepProps(
    string Id,
    string Title,
    string Message,
    WizardStepType Type,
    string[]? Options = null,
    string? InitialValue = null,
    string? Placeholder = null,
    bool Sensitive = false,
    Action<string>? OnSubmit = null
);
