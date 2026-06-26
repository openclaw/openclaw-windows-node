namespace OpenClaw.SetupEngine;

public sealed record WizardNextStepAnswer(string stepId);

public sealed record WizardNextStepAcknowledgement(string sessionId, WizardNextStepAnswer answer);

public static class WizardNextPayload
{
    public static WizardNextStepAcknowledgement Acknowledge(string sessionId, string stepId) =>
        new(sessionId, new WizardNextStepAnswer(stepId));
}
