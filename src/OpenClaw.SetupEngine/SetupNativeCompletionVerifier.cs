using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

public static class SetupNativeCompletionVerifier
{
    public static async Task<SetupVerifiedNativeRoute> VerifyAsync(
        string dataDir, GatewayAiSetupCompletion expected, CancellationToken ct,
        GatewayConnectionManager? connectionManager = null,
        Func<GatewayAiSetupCompletion, CancellationToken, Task>? waitForModel = null,
        TimeProvider? timeProvider = null)
    {
        void RequireOwner()
        {
            try { SetupGatewaySession.RequireCompletionGateway(dataDir, expected); }
            catch (InvalidOperationException) { throw new SetupNativeOwnershipException(); }
        }
        RequireOwner();
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        if (registry.GetActive() is { NativePackageFamilyName: not null } native)
        {
            if (connectionManager is null)
                throw new InvalidOperationException("The native Gateway connection owner is unavailable.");
            Task<IGatewayAiSetupTransport> BorrowAsync() => SetupNativeCompletionTiming.RunAsync(
                token => GatewayAiSetupTransport.BorrowNativeAsync(dataDir, connectionManager, native.Id, token,
                    expected.EndpointBinding, SetupNativeCompletionTiming.Connection),
                SetupNativeCompletionTiming.Connection, SetupNativeCompletionPhase.Connection, ct, timeProvider);
            var transport = await BorrowAsync();
            SetupNativeVerification.RequireRoute(expected, transport.Route);
            if (waitForModel is not null)
            {
                await SetupNativeCompletionTiming.RunAsync(async token =>
                    {
                        await waitForModel(expected, token);
                        return true;
                    }, SetupNativeCompletionTiming.ModelRecovery, SetupNativeCompletionPhase.ModelRecovery, ct, timeProvider);
                RequireOwner();
                // Model recovery can publish a new port and restart the Gateway.
                // Never verify on the pre-recovery handshake.
                transport = await BorrowAsync();
                SetupNativeVerification.RequireRoute(expected, transport.Route);
            }
            var nativeClient = new GatewayAiSetupClient(transport, expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
            var current = new SetupVerifiedNativeRoute(
                await VerifyModelAsync(nativeClient, expected.ModelRef, ct, timeProvider), transport.Route.SessionKey ?? "");
            SetupNativeVerification.RequireSame(expected, current);
            RequireOwner();
            return current;
        }
        await using var session = await SetupGatewaySession.ConnectAsync(dataDir, ct: ct,
            expectedGatewayId: expected.GatewayId, expectedCompletion: expected);
        var route = session.GetRoute();
        if (route.GatewayId != expected.GatewayId || route.EndpointBinding != expected.EndpointBinding ||
            route.AgentId != expected.AgentId || string.IsNullOrWhiteSpace(route.AgentId) ||
            route.IdentityBinding != expected.IdentityBinding || route.SessionKey != expected.SessionKey)
            throw new SetupNativeOwnershipException();
        var client = new GatewayAiSetupClient(new GatewayAiSetupTransport(session.Client, session.GetRoute),
            expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
        var verified = new SetupVerifiedNativeRoute(await VerifyModelAsync(client, expected.ModelRef, ct, timeProvider),
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
        GatewayAiSetupClient client, string modelRef, CancellationToken ct, TimeProvider? timeProvider = null)
    {
        try
        {
            var result = await SetupNativeCompletionTiming.RunAsync(
                token => client.VerifyConfiguredAsync(modelRef, token),
                SetupNativeCompletionTiming.ModelVerification, SetupNativeCompletionPhase.ModelVerification, ct, timeProvider);
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
