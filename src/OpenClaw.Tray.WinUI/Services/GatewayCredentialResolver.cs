using System;
using System.IO;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Resolved gateway credential plus metadata about which source was used.
/// </summary>
public sealed record GatewayCredential(string Token, bool IsBootstrapToken, string Source);

/// <summary>
/// Static, WinUI-free resolver that picks the gateway credential App should use
/// when constructing <c>OpenClawGatewayClient</c>. Mirrors the prototype's
/// resolver shape (App.xaml.cs:1244-1298 in openclaw-windows-node) so a paired
/// operator whose only credential lives in DeviceIdentity (Bug #4) still gets a
/// client constructed at startup.
///
/// Resolution order (Bug #4 / RubberDucky CONDITIONAL AGREE closure conditions):
///   1. settings.Token         -> use as-is, IsBootstrapToken = false
///   2. settings.BootstrapToken-> use as bootstrap handoff, IsBootstrapToken = true
///   3. DeviceIdentity DeviceToken from device-key-ed25519.json -> IsBootstrapToken = false
///   4. none of the above      -> null (caller logs + skips client init)
/// </summary>
public static class GatewayCredentialResolver
{
    public const string SourceSettingsToken = "settings.Token";
    public const string SourceSettingsBootstrap = "settings.BootstrapToken";
    public const string SourceDeviceIdentity = "deviceIdentity.DeviceToken";

    public static GatewayCredential? Resolve(
        string? settingsToken,
        string? settingsBootstrapToken,
        string? deviceIdentityPath,
        Action<string>? warn = null)
    {
        if (!string.IsNullOrWhiteSpace(settingsToken))
        {
            return new GatewayCredential(settingsToken!, false, SourceSettingsToken);
        }

        if (!string.IsNullOrWhiteSpace(settingsBootstrapToken))
        {
            return new GatewayCredential(settingsBootstrapToken!, true, SourceSettingsBootstrap);
        }

        if (!string.IsNullOrWhiteSpace(deviceIdentityPath) && File.Exists(deviceIdentityPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(deviceIdentityPath!));
                if (doc.RootElement.TryGetProperty("DeviceToken", out var tokenElement))
                {
                    var stored = tokenElement.GetString();
                    if (!string.IsNullOrWhiteSpace(stored))
                    {
                        return new GatewayCredential(stored!, false, SourceDeviceIdentity);
                    }
                }
            }
            catch (Exception ex)
            {
                warn?.Invoke($"Failed to inspect stored gateway device token: {ex.Message}");
            }
        }

        return null;
    }
}
