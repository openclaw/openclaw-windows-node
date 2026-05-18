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
            var legacyKeyPath = Path.Combine(legacyDir, "device-key-ed25519.json");
            // Construct the minimal JSON shape DeviceIdentity expects. We only need the
            // token fields to assert the migration invariant; the keypair is irrelevant
            // to the read paths the credential resolver uses.
            File.WriteAllText(legacyKeyPath, """
                {
                    "Algorithm": "ed25519",
                    "PrivateKeyBase64": "AAAA",
                    "PublicKeyBase64": "AAAA",
                    "DeviceToken": "operator-tok",
                    "DeviceTokenScopes": ["operator.read","operator.write"],
                    "NodeDeviceToken": "node-tok",
                    "NodeDeviceTokenScopes": ["node.connect","node.reconnect"]
                }
                """);

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
            try { Directory.Delete(legacyDir, recursive: true); } catch { }
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
            var legacyKeyPath = Path.Combine(legacyDir, "device-key-ed25519.json");
            File.WriteAllText(legacyKeyPath, """
                {
                    "Algorithm": "ed25519",
                    "PrivateKeyBase64": "AAAA",
                    "PublicKeyBase64": "AAAA",
                    "NodeDeviceToken": "node-only-tok",
                    "NodeDeviceTokenScopes": ["node.connect"]
                }
                """);

            File.Copy(legacyKeyPath, Path.Combine(perGatewayDir, "device-key-ed25519.json"), overwrite: false);

            Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(perGatewayDir));
            Assert.Equal("node-only-tok", DeviceIdentity.TryReadStoredDeviceTokenForRole(perGatewayDir, "node"));
        }
        finally
        {
            try { Directory.Delete(legacyDir, recursive: true); } catch { }
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
