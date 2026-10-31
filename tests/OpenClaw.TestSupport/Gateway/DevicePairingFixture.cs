using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Memory-only admin pairing decisions, with scope/ownership denials from pinned
/// OpenClaw 098e991 server-methods/devices.ts. Never issues or stores device tokens.
/// </summary>
public sealed class DevicePairingFixture(string scope = "operator.admin", bool supported = true)
{
    public const string RequestId = "fixture-device-request";
    public const string DeviceId = "fixture-other-device";
    private readonly object _sync = new();
    private bool _pending = true;
    private bool _paired;

    public string[] Scopes => scope == "operator.read" ? ["operator.read"] : ["operator.read", scope];
    public string[] Methods => supported
        ? ["device.pair.list", "device.pair.approve", "device.pair.reject"] : [];
    public string Provenance => $"098e991:devices.ts:{scope}:{supported}";

    internal object Respond(string method, JsonElement parameters)
    {
        if (!supported)
            throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown method.");
        if (scope is not ("operator.admin" or "operator.pairing"))
            throw new FixtureRequestException("INVALID_REQUEST", "missing scope: operator.pairing");
        var deciding = method != "device.pair.list";
        if (parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Any(property => !deciding || property.Name != "requestId"))
            throw new FixtureRequestException("INVALID_REQUEST", "Invalid device pairing parameters.");

        lock (_sync)
        {
            if (!deciding)
            {
                // Non-admin paired operators can only see their own identity.
                return new
                {
                    pending = _pending && scope == "operator.admin" ? new[] { Pending() } : [],
                    paired = _paired && scope == "operator.admin" ? new[] { Paired() } : []
                };
            }
            if (!parameters.TryGetProperty("requestId", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new FixtureRequestException("INVALID_REQUEST", "requestId required");
            if (scope != "operator.admin")
                throw new FixtureRequestException("INVALID_REQUEST", "device pairing decision denied");
            if (id.GetString()!.Trim() != RequestId || !_pending)
                throw new FixtureRequestException("INVALID_REQUEST", "unknown requestId");

            _pending = false;
            _paired = method == "device.pair.approve";
            return _paired
                ? new { requestId = RequestId, device = Paired() }
                : (object)new { requestId = RequestId, deviceId = DeviceId };
        }
    }

    private static object Pending() => new
    {
        requestId = RequestId, deviceId = DeviceId, publicKey = "fixture-public-key",
        displayName = "Synthetic device", platform = "fixture", role = "operator",
        scopes = new[] { "operator.read" }, ts = 1_790_000_000_000L
    };

    private static object Paired() => new
    {
        deviceId = DeviceId, publicKey = "fixture-public-key", displayName = "Synthetic device",
        roles = new[] { "operator" }, scopes = new[] { "operator.read" },
        tokens = Array.Empty<object>()
    };
}
