using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Ends optional onboarding deliberately, then verifies the saved configuration and
/// authenticated Gateway health. A cancelled wizard is not reported as completed.
/// </summary>
public static class WizardOptionalSetupHandoff
{
    public static async Task CompleteAsync(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        string sessionId,
        JsonElement checkpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            WizardOnboardingPolicy.Evaluate(checkpoint).Action != WizardOnboardingAction.Finish)
            throw new InvalidOperationException("The Gateway has not reached the optional setup handoff.");

        cancellationToken.ThrowIfCancellationRequested();
        var cancelled = await sendRequest("wizard.cancel", new { sessionId }, 30_000)
            .WaitAsync(cancellationToken);
        if (cancelled.ValueKind != JsonValueKind.Object ||
            !cancelled.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "cancelled" ||
            (cancelled.TryGetProperty("error", out var error) &&
             !string.IsNullOrWhiteSpace(error.ToString()) && error.ToString() != "cancelled"))
            throw new InvalidOperationException("The Gateway did not confirm cancellation of optional setup.");

        cancellationToken.ThrowIfCancellationRequested();
        var config = await sendRequest("config.get", null, 30_000).WaitAsync(cancellationToken);
        if (!IsTrue(config, "valid"))
            throw new InvalidOperationException("The saved Gateway configuration is invalid. Fix it before completing setup.");

        cancellationToken.ThrowIfCancellationRequested();
        var health = await sendRequest("health", null, 30_000).WaitAsync(cancellationToken);
        if (!IsTrue(health, "ok"))
            throw new InvalidOperationException("The Gateway health check failed after deferring optional setup.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsTrue(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
