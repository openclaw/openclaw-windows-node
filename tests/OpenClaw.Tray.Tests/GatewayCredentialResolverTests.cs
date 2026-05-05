using System.Text.Json;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug #4 (Wizard hung at "Authenticating") — covers the App-init credential
/// resolver Aaron extracted into <see cref="GatewayCredentialResolver"/> per
/// RubberDucky's CONDITIONAL AGREE closure conditions.
///
/// Each test exercises one branch of the resolution order documented on the
/// resolver:
///   settings.Token -> settings.BootstrapToken -> DeviceIdentity DeviceToken -> null.
/// The tests deliberately stay WinUI-free so the Tray.Tests project keeps its
/// existing 551/551 baseline.
/// </summary>
public class GatewayCredentialResolverTests : IDisposable
{
    private readonly string _tempDir;

    public GatewayCredentialResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", "GatewayCred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string IdentityPath => Path.Combine(_tempDir, "device-key-ed25519.json");

    private void WriteIdentity(string? deviceToken)
    {
        var payload = new Dictionary<string, object?> { ["DeviceToken"] = deviceToken };
        File.WriteAllText(IdentityPath, JsonSerializer.Serialize(payload));
    }

    // Branch 1: settings.Token populated → resolved as-is, IsBootstrapToken=false.
    // This is also the regression guard for the existing manual ConnectionPage flow
    // (App.xaml.cs sets _settings.Token after the user submits gateway URL + token).
    [Fact]
    public void Resolve_PrefersSettingsToken_AsNonBootstrap()
    {
        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "operator-token",
            settingsBootstrapToken: "bootstrap-token",
            deviceIdentityPath: IdentityPath);

        Assert.NotNull(result);
        Assert.Equal("operator-token", result!.Token);
        Assert.False(result.IsBootstrapToken);
        Assert.Equal(GatewayCredentialResolver.SourceSettingsToken, result.Source);
    }

    // Branch 2: Token empty + BootstrapToken populated → resolved as bootstrap (true).
    // Load-bearing per RubberDucky note 4 — OpenClawGatewayClient.BuildAuthPayload only
    // emits auth.bootstrapToken when the constructor flag is true.
    [Fact]
    public void Resolve_FallsBackToBootstrapToken_AsBootstrap()
    {
        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "",
            settingsBootstrapToken: "bt",
            deviceIdentityPath: IdentityPath);

        Assert.NotNull(result);
        Assert.Equal("bt", result!.Token);
        Assert.True(result.IsBootstrapToken);
        Assert.Equal(GatewayCredentialResolver.SourceSettingsBootstrap, result.Source);
    }

    // Branch 3: both settings empty + DeviceIdentity has DeviceToken → resolved from
    // device-key-ed25519.json as a non-bootstrap operator token (this is the literal
    // Bug #4 path: Phase 12 stored the operator token only into DeviceIdentity).
    [Fact]
    public void Resolve_FallsBackToDeviceIdentityDeviceToken_AsNonBootstrap()
    {
        WriteIdentity("stored-device-token");

        var result = GatewayCredentialResolver.Resolve(
            settingsToken: null,
            settingsBootstrapToken: "",
            deviceIdentityPath: IdentityPath);

        Assert.NotNull(result);
        Assert.Equal("stored-device-token", result!.Token);
        Assert.False(result.IsBootstrapToken);
        Assert.Equal(GatewayCredentialResolver.SourceDeviceIdentity, result.Source);
    }

    // Branch 4: all three empty → returns null and no client is constructed.
    // Caller logs "Gateway token not configured" (preserved by App.xaml.cs).
    [Fact]
    public void Resolve_AllEmpty_ReturnsNull()
    {
        // No identity file written.
        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "",
            settingsBootstrapToken: null,
            deviceIdentityPath: IdentityPath);

        Assert.Null(result);
    }

    // Identity file exists but has no DeviceToken property → still null.
    // Guards against a malformed/blank device-key file silently producing an
    // empty token that would fail authentication noisily downstream.
    [Fact]
    public void Resolve_DeviceIdentityWithoutDeviceToken_ReturnsNull()
    {
        File.WriteAllText(IdentityPath, "{\"OtherField\":\"x\"}");

        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "",
            settingsBootstrapToken: "",
            deviceIdentityPath: IdentityPath);

        Assert.Null(result);
    }

    // Identity file is unreadable JSON → resolver swallows the parse error,
    // surfaces it via the warn callback, and returns null (no throw).
    [Fact]
    public void Resolve_DeviceIdentityCorrupt_LogsWarningAndReturnsNull()
    {
        File.WriteAllText(IdentityPath, "{ not valid json");
        string? warning = null;

        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "",
            settingsBootstrapToken: "",
            deviceIdentityPath: IdentityPath,
            warn: msg => warning = msg);

        Assert.Null(result);
        Assert.NotNull(warning);
        Assert.Contains("Failed to inspect stored gateway device token", warning);
    }

    // Whitespace-only credentials must not be picked up — string.IsNullOrWhiteSpace
    // semantics are part of the contract that callers in App.InitializeGatewayClient
    // depend on.
    [Fact]
    public void Resolve_WhitespaceTokens_AreIgnored()
    {
        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "   ",
            settingsBootstrapToken: "\t\n",
            deviceIdentityPath: IdentityPath);

        Assert.Null(result);
    }

    // Token precedence: when Token AND BootstrapToken are both populated,
    // Token wins (so a later producer-side BootstrapToken cleanup or the
    // Phase 12 / QR dual-token harvest follow-ups stay harmless).
    [Fact]
    public void Resolve_TokenWinsOverBootstrap_WhenBothPresent()
    {
        var result = GatewayCredentialResolver.Resolve(
            settingsToken: "op",
            settingsBootstrapToken: "bt",
            deviceIdentityPath: IdentityPath);

        Assert.NotNull(result);
        Assert.Equal("op", result!.Token);
        Assert.False(result.IsBootstrapToken);
    }
}
