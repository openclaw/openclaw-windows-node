using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Regression tests for the legacy device-key-ed25519.json migration performed
/// by App.InitializeGatewayClient when bridging settings → GatewayRegistry on
/// startup.
/// <para>
/// Invariant under test: a single file copy of <c>device-key-ed25519.json</c>
/// from the legacy settings directory to a per-gateway identity directory must
/// migrate BOTH operator (<c>DeviceToken</c>) and node (<c>NodeDeviceToken</c>)
/// roles. The Connection Phase 4 unification (manager-owned NodeConnector)
/// relies on this — without it, paired-pre-unification installs would lose
/// their node identity on first launch after the unification ships.
/// </para>
/// </summary>
public class IdentityFileMigrationTests
{
    [Fact]
    public void FileCopy_MigratesBothOperatorAndNodeTokens()
    {
        var legacyDir = CreateTempDir();
        var perGatewayDir = CreateTempDir();
        try
        {
            var legacyIdentity = new DeviceIdentity(legacyDir);
            legacyIdentity.Initialize();
            legacyIdentity.StoreDeviceTokenForRole(
                "operator",
                "operator-tok",
                ["operator.read", "operator.write"]);
            legacyIdentity.StoreDeviceTokenForRole(
                "node",
                "node-tok",
                ["node.connect", "node.reconnect"]);
            var legacyKeyPath = Path.Combine(legacyDir, "device-key-ed25519.json");

            // Mirror the production migration step (App.xaml.cs:2746-2753).
            var newKeyPath = Path.Combine(perGatewayDir, "device-key-ed25519.json");
            File.Copy(legacyKeyPath, newKeyPath, overwrite: false);

            // Both roles must be readable from the per-gateway directory.
            var operatorToken = DeviceIdentity.TryReadStoredDeviceToken(perGatewayDir);
            var nodeToken = DeviceIdentity.TryReadStoredDeviceTokenForRole(perGatewayDir, "node");

            Assert.Equal("operator-tok", operatorToken);
            Assert.Equal("node-tok", nodeToken);

            // Legacy file must remain in place (copy, not move) for safe rollback.
            Assert.True(File.Exists(legacyKeyPath));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(legacyDir, recursive: true); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(perGatewayDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FileCopy_NodeTokenOnly_StillMigrates()
    {
        // Pre-unification easy-button installs that completed setup but had operator
        // tokens cleared (e.g., after a token rotation) should still migrate node identity.
        var legacyDir = CreateTempDir();
        var perGatewayDir = CreateTempDir();
        try
        {
            var legacyIdentity = new DeviceIdentity(legacyDir);
            legacyIdentity.Initialize();
            legacyIdentity.StoreDeviceTokenForRole("node", "node-only-tok", ["node.connect"]);
            var legacyKeyPath = Path.Combine(legacyDir, "device-key-ed25519.json");

            File.Copy(legacyKeyPath, Path.Combine(perGatewayDir, "device-key-ed25519.json"), overwrite: false);

            Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(perGatewayDir));
            Assert.Equal("node-only-tok", DeviceIdentity.TryReadStoredDeviceTokenForRole(perGatewayDir, "node"));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(legacyDir, recursive: true); } catch { }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(perGatewayDir, recursive: true); } catch { }
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-idmig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
