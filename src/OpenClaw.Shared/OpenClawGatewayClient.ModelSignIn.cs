using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private static GatewayModelSignInException ReadModelSignInFailure(JsonElement root)
    {
        root.TryGetProperty("error", out var error);
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) &&
            c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var detailCode = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("details", out var d) &&
            d.ValueKind == JsonValueKind.Object && d.TryGetProperty("code", out var dc) &&
            dc.ValueKind == JsonValueKind.String ? dc.GetString() : null;
        bool? retryable = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("retryable", out var r) &&
            r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null;
        double? retryAfter = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("retryAfterMs", out var ra) &&
            ra.ValueKind == JsonValueKind.Number && ra.TryGetDouble(out var delay) && double.IsFinite(delay) && delay >= 0
            ? delay : null;
        return new(code, detailCode, retryable, retryAfter);
    }

    public ModelSignInSession CreateModelSignInSession(string agentId, Func<bool> isViewCurrent) =>
        new(new ModelSignInTransport(this, isViewCurrent), agentId);

    private sealed class ModelSignInTransport(OpenClawGatewayClient client, Func<bool> isViewCurrent)
        : IModelSignInTransport
    {
        private readonly long? _epoch = client.SessionMutationConnectionEpoch;
        public bool IsConnectionCurrent => _epoch.HasValue && client.SessionMutationConnectionEpoch == _epoch;
        public bool IsCurrent => IsConnectionCurrent && isViewCurrent();
        public bool Supports(string method, string scope) => IsConnectionCurrent &&
            client.AdvertisedServerMethods.Contains(method, StringComparer.Ordinal) &&
            (client.GrantedOperatorScopes.Contains(scope, StringComparer.Ordinal) ||
             client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal));

        public async Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs)
        {
            if (!IsConnectionCurrent || method != "wizard.cancel" && !IsCurrent)
                throw new InvalidOperationException("The sign-in connection changed.");
            if (!Supports(method, method == "models.authStatus" ? "operator.read" : "operator.admin"))
                throw new UnauthorizedAccessException("The Gateway does not authorize this sign-in operation.");
            var response = await client.SendResponseRequestAsync(method, parameters, timeoutMs, _epoch, modelSignIn: true);
            if (!IsConnectionCurrent)
                throw new InvalidOperationException("The sign-in connection changed.");
            return response;
        }
    }
}
