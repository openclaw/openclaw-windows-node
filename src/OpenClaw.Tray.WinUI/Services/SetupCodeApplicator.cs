using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;

namespace OpenClawTray.Services;

/// <summary>
/// Applies a decoded setup code to settings. Shared by Settings page and
/// Onboarding wizard to guarantee consistent behavior:
/// — GatewayUrl is updated from the decoded URL
/// — BootstrapToken is set (single-use, for pairing only)
/// — Token is cleared to prevent dual-token confusion
/// — Stored device token is cleared (setup code = new pairing to a potentially different gateway)
/// </summary>
public static class SetupCodeApplicator
{
    public record ApplyResult(bool Success, string? DisplayUrl = null, string? Error = null);

    /// <summary>
    /// Decodes the raw setup code and applies the result to settings.
    /// Clears Settings.Token and stored device tokens to avoid stale
    /// credentials from a previous gateway pairing.
    /// </summary>
    public static ApplyResult Apply(string? rawCode, SettingsManager settings, string? dataPath = null)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return new ApplyResult(false, Error: "Please paste a setup code.");

        var result = SetupCodeDecoder.Decode(rawCode.Trim());
        if (!result.Success)
            return new ApplyResult(false, Error: result.Error);

        if (!string.IsNullOrEmpty(result.Url))
            settings.GatewayUrl = result.Url;

        if (!string.IsNullOrEmpty(result.Token))
        {
            // Bootstrap token goes to BootstrapToken only — it's single-use for
            // device pairing and must NOT be saved as Settings.Token.
            settings.BootstrapToken = result.Token;
        }

        // Clear the manual token to prevent the dual-save race where
        // a stale Token value is persisted alongside BootstrapToken.
        settings.Token = "";

        // Clear any stored device token from a previous pairing — the setup code
        // may target a different gateway that doesn't recognize the old token.
        if (!string.IsNullOrEmpty(dataPath))
        {
            DeviceIdentity.TryClearStoredDeviceToken(dataPath);
        }

        settings.Save();

        var displayUrl = GatewayUrlHelper.SanitizeForDisplay(
            result.Url ?? settings.GatewayUrl ?? "");

        return new ApplyResult(true, DisplayUrl: displayUrl);
    }
}
