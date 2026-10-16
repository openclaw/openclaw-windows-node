namespace OpenClaw.SetupEngine;

internal static class SetupModelVerification
{
    internal static void RequireAvailable(GatewayAiSetupVerification result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Ok)
            throw new InvalidOperationException("The primary model is currently unavailable for verification.");
    }

    internal static async Task<GatewayAiSetupCompletion> VerifyAsync(
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
