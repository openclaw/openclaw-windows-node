namespace OpenClaw.SetupEngine;

public static class SetupNativeCompletionVerifier
{
    public static async Task<SetupVerifiedNativeRoute> VerifyAsync(
        string dataDir, GatewayAiSetupCompletion expected, CancellationToken ct)
    {
        void RequireOwner()
        {
            try { SetupGatewaySession.RequireCompletionGateway(dataDir, expected); }
            catch (InvalidOperationException) { throw new SetupNativeOwnershipException(); }
        }
        RequireOwner();
        await using var session = await SetupGatewaySession.ConnectAsync(dataDir, ct: ct,
            expectedGatewayId: expected.GatewayId, expectedCompletion: expected);
        var route = session.GetRoute();
        if (route.GatewayId != expected.GatewayId || route.EndpointBinding != expected.EndpointBinding ||
            route.AgentId != expected.AgentId || string.IsNullOrWhiteSpace(route.AgentId) ||
            route.IdentityBinding != expected.IdentityBinding || route.SessionKey != expected.SessionKey)
            throw new SetupNativeOwnershipException();
        var client = new GatewayAiSetupClient(new GatewayAiSetupTransport(session.Client, session.GetRoute),
            expected.ModelRef, expected.Intent);
        var verified = new SetupVerifiedNativeRoute(await VerifyModelAsync(client, expected.ModelRef, ct),
            session.Client.MainSessionKey ?? "");
        SetupNativeVerification.RequireSame(expected, verified);
        RequireOwner();
        return verified;
    }

    internal static void RequireAvailable(GatewayAiSetupVerification result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Ok)
            throw new InvalidOperationException("The primary model is currently unavailable for verification.");
    }

    internal static async Task<GatewayAiSetupCompletion> VerifyModelAsync(
        GatewayAiSetupClient client, string modelRef, CancellationToken ct)
    {
        try
        {
            var result = await client.VerifyConfiguredAsync(modelRef, ct);
            ct.ThrowIfCancellationRequested();
            RequireAvailable(result);
            return client.GetVerifiedCompletion();
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("The Gateway verification contract or response is currently unavailable.", error);
        }
    }
}
