using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

public static class SetupNativeCompletionVerifier
{
    public static async Task ConfirmReadinessAuthorityAsync(string dataDir, GatewayAiSetupCompletion expected,
        GatewayConnectionManager? manager, CancellationToken ct)
    {
        SetupGatewaySession.RequireCompletionGateway(dataDir, expected);
        if (manager is null) throw new InvalidOperationException("The Gateway connection owner is unavailable.");
        var transport = await GatewayAiSetupTransport.BorrowAsync(dataDir, manager, expected.GatewayId, ct,
            expected.EndpointBinding, SetupNativeCompletionTiming.Connection, readOnlyRequests: true);
        await SetupNativeReadyBinding.RequireStableAuthorityAsync(transport, expected, ct);
        SetupGatewaySession.RequireCompletionGateway(dataDir, expected);
    }

    public static async Task<SetupVerifiedNativeRoute> VerifyAsync(
        string dataDir, GatewayAiSetupCompletion expected, CancellationToken ct,
        GatewayConnectionManager? connectionManager = null,
        Func<GatewayAiSetupCompletion, CancellationToken, Task>? waitForModel = null,
        TimeProvider? timeProvider = null, bool captureReadiness = false,
        IProgress<SetupLoadingStep>? progress = null)
    {
        void RequireOwner()
        {
            try { SetupGatewaySession.RequireCompletionGateway(dataDir, expected); }
            catch (InvalidOperationException) { throw new SetupNativeOwnershipException(); }
        }
        RequireOwner();
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        if (registry.GetActive() is { } native && (native.NativePackageFamilyName is not null || captureReadiness))
        {
            if (connectionManager is null)
                throw new InvalidOperationException("The native Gateway connection owner is unavailable.");
            Task<IGatewayAiSetupTransport> BorrowAsync() => SetupNativeCompletionTiming.RunAsync(
                token => GatewayAiSetupTransport.BorrowAsync(dataDir, connectionManager, native.Id, token,
                    expected.EndpointBinding, SetupNativeCompletionTiming.Connection, readOnlyRequests: captureReadiness),
                SetupNativeCompletionTiming.Connection, SetupNativeCompletionPhase.Connection, ct, timeProvider);
            progress?.Report(SetupLoadingStep.ConnectGateway);
            var transport = await BorrowAsync();
            SetupNativeVerification.RequireRoute(expected, transport.Route);
            if (waitForModel is not null)
            {
                if (expected.RequiresManagedLocalAi) progress?.Report(SetupLoadingStep.RecoverLocalAi);
                await SetupNativeCompletionTiming.RunAsync(async token =>
                    {
                        await waitForModel(expected, token);
                        return true;
                    }, SetupNativeCompletionTiming.ModelRecovery, SetupNativeCompletionPhase.ModelRecovery, ct, timeProvider);
                RequireOwner();
                // Model recovery can publish a new port and restart the Gateway.
                // Never verify on the pre-recovery handshake.
                progress?.Report(SetupLoadingStep.ReconnectGateway);
                transport = await BorrowAsync();
                SetupNativeVerification.RequireRoute(expected, transport.Route);
            }
            if (captureReadiness)
            {
                var ready = await SetupNativeReadyBinding.VerifyAsync(transport, expected, ct, timeProvider, progress);
                RequireOwner();
                return new(ready.Proof, ready.Proof.SessionKey!, ready);
            }
            var nativeClient = new GatewayAiSetupClient(transport, expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
            progress?.Report(SetupLoadingStep.VerifyModel);
            var current = new SetupVerifiedNativeRoute(
                await VerifyModelAsync(nativeClient, expected.ModelRef, ct, timeProvider), transport.Route.SessionKey ?? "");
            SetupNativeVerification.RequireSame(expected, current);
            RequireOwner();
            return current;
        }
        progress?.Report(SetupLoadingStep.ConnectGateway);
        await using var session = await SetupGatewaySession.ConnectAsync(dataDir, ct: ct,
            expectedGatewayId: expected.GatewayId, expectedCompletion: expected);
        var route = session.GetRoute();
        if (route.GatewayId != expected.GatewayId || route.EndpointBinding != expected.EndpointBinding ||
            route.AgentId != expected.AgentId || string.IsNullOrWhiteSpace(route.AgentId) ||
            route.IdentityBinding != expected.IdentityBinding || route.SessionKey != expected.SessionKey)
            throw new SetupNativeOwnershipException();
        var client = new GatewayAiSetupClient(new GatewayAiSetupTransport(session.Client, session.GetRoute),
            expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
        progress?.Report(SetupLoadingStep.VerifyModel);
        var verified = new SetupVerifiedNativeRoute(await VerifyModelAsync(client, expected.ModelRef, ct, timeProvider),
            session.Client.MainSessionKey ?? "");
        SetupNativeVerification.RequireSame(expected, verified);
        RequireOwner();
        return verified;
    }

    internal static void RequireAvailable(GatewayAiSetupVerification result)
        => SetupModelVerification.RequireAvailable(result);

    internal static Task<GatewayAiSetupCompletion> VerifyModelAsync(
        GatewayAiSetupClient client, string modelRef, CancellationToken ct, TimeProvider? timeProvider = null)
        => SetupModelVerification.VerifyAsync(client, modelRef, ct, timeProvider);
}
