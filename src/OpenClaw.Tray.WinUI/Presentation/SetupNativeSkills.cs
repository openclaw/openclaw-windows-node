using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

/// <summary>Read-only, response-bound skills load. Never accepts the app's unscoped skills cache.</summary>
internal static class SetupNativeSkills
{
    public static async Task<JsonElement> LoadAsync(SetupNativeNavigationRequest request,
        Func<IOperatorGatewayClient> getCurrentClient, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Completion.Target.Destination != SetupNativeDestination.Skills ||
            !request.Completion.Target.Matches(request.Completion.Verification))
            throw new SetupNativeOwnershipException();
        var client = getCurrentClient();
        var result = await client.SendWizardRequestAsync("skills.status",
            new { agentId = request.Completion.Verification.AgentId }, timeoutMs: 12000).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(client, getCurrentClient()))
            throw new SetupNativeOwnershipException();
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The Gateway did not return a skills list.");
        return result;
    }
}
