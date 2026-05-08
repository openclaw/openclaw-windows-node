using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;

namespace OpenClawTray.Services;

/// <summary>
/// Applies a decoded setup code to the gateway registry (the single source
/// of truth for gateway URL, tokens, and bootstrap state).
/// </summary>
public static class SetupCodeApplicator
{
    public record ApplyResult(bool Success, string? DisplayUrl = null, string? Error = null);

    public static ApplyResult Apply(string? rawCode, SettingsManager settings,
        string? dataPath = null, GatewayRegistry? registry = null)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return new ApplyResult(false, Error: "Please paste a setup code.");

        var result = SetupCodeDecoder.Decode(rawCode.Trim());
        if (!result.Success)
            return new ApplyResult(false, Error: result.Error);

        var gatewayUrl = result.Url ?? settings.GatewayUrl ?? "";

        if (registry != null && !string.IsNullOrWhiteSpace(gatewayUrl))
        {
            var id = GatewayRecord.GenerateId(gatewayUrl);
            registry.Remove(id);
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = id,
                Url = gatewayUrl,
                BootstrapToken = result.Token,
            });
        }

        // Clear stored device token — setup code targets a potentially different gateway
        if (!string.IsNullOrEmpty(dataPath))
            DeviceIdentity.TryClearStoredDeviceToken(dataPath);

        var displayUrl = GatewayUrlHelper.SanitizeForDisplay(gatewayUrl);
        return new ApplyResult(true, DisplayUrl: displayUrl);
    }
}
