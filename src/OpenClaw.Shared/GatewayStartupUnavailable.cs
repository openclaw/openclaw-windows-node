using System;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>A structured, retryable connect rejection while Gateway startup sidecars initialize.</summary>
public sealed record GatewayStartupUnavailable(int RetryAfterMs)
{
    private const int DefaultRetryAfterMs = 500;
    private const int MinimumRetryAfterMs = 100;
    private const int MaximumRetryAfterMs = 2000;

    internal static GatewayStartupUnavailable? Parse(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.String || code.GetString() != "UNAVAILABLE" ||
            !error.TryGetProperty("retryable", out var retryable) ||
            retryable.ValueKind != JsonValueKind.True ||
            !error.TryGetProperty("details", out var details) ||
            details.ValueKind != JsonValueKind.Object ||
            !details.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String || reason.GetString() != "startup-sidecars")
            return null;

        var delay = (double)DefaultRetryAfterMs;
        if (error.TryGetProperty("retryAfterMs", out var hint) &&
            hint.ValueKind == JsonValueKind.Number &&
            hint.TryGetDouble(out var value) && double.IsFinite(value))
            delay = Math.Floor(value);

        return new((int)Math.Clamp(delay, MinimumRetryAfterMs, MaximumRetryAfterMs));
    }
}

/// <summary>The caller's existing connection deadline expired during typed Gateway startup.</summary>
public sealed class GatewayStartupUnavailableException(GatewayStartupUnavailable startup)
    : TimeoutException("The Gateway is still starting. Its startup retry did not complete before the connection deadline.")
{
    public GatewayStartupUnavailable Startup { get; } = startup;
}
