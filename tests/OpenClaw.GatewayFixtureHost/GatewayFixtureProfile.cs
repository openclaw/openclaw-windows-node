using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>
/// A second synthetic gateway record seeded as an inactive saved peer before app startup.
/// This exists only so switching between saved gateways can be exercised through the real
/// Connection UI. It carries a synthetic loopback endpoint and a redacted fixture token.
/// </summary>
public sealed record GatewayFixtureSecondaryPeer(Uri Endpoint, string Token, string FriendlyName);

/// <summary>Owns synthetic app state. No installed settings or identities are imported.</summary>
public sealed class GatewayFixtureProfile : IDisposable
{
    private readonly TempDirectory _directory;
    private readonly int? _secondaryGatewayPort;
    private bool _disposed;

    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string RunDirectory => _directory.Path;
    public string DataDirectory => _directory.Combine("profile");
    public string SetupDirectory => Path.Combine(LocalRoot, "OpenClawTray");
    public string RoamingRoot => _directory.Combine("roaming");
    public string LocalRoot => _directory.Combine("local");
    public string GatewayId { get; } = Guid.NewGuid().ToString();
    public Uri GatewayEndpoint { get; }

    /// <summary>The id of the inactive second saved peer, when one was seeded.</summary>
    public string? SecondaryGatewayId { get; private set; }

    public GatewayFixtureProfile(Uri endpoint, string token, bool showChatToolCalls = true,
        GatewayFixtureStartupMode startupMode = GatewayFixtureStartupMode.Connected,
        GatewayFixtureSecondaryPeer? secondaryPeer = null)
    {
        if (!Enum.IsDefined(startupMode)) throw new ArgumentOutOfRangeException(nameof(startupMode));
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (startupMode == GatewayFixtureStartupMode.McpOnly && secondaryPeer is not null)
            throw new ArgumentException("A secondary saved peer requires a Connected startup profile.", nameof(secondaryPeer));
        if (secondaryPeer is not null)
        {
            ValidateEndpoint(secondaryPeer.Endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(secondaryPeer.Token);
            ArgumentException.ThrowIfNullOrWhiteSpace(secondaryPeer.FriendlyName);
            if (secondaryPeer.Endpoint.Port == endpoint.Port)
                throw new ArgumentException("The secondary saved peer must bind a distinct loopback port.", nameof(secondaryPeer));
            if (string.Equals(secondaryPeer.Token, token, StringComparison.Ordinal))
                throw new ArgumentException("The secondary saved peer must use a distinct credential.", nameof(secondaryPeer));
        }
        _secondaryGatewayPort = secondaryPeer?.Endpoint.Port;
        GatewayEndpoint = endpoint;
        _directory = new TempDirectory("openclaw-gateway-fixture-");
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(SetupDirectory);
            Directory.CreateDirectory(RoamingRoot);
            Directory.CreateDirectory(LocalRoot);
            var settings = new SettingsData
            {
                GatewayUrl = startupMode == GatewayFixtureStartupMode.McpOnly ? "" : endpoint.AbsoluteUri,
                EnableMcpServer = true,
                EnableNodeMode = false,
                AutoStart = false,
                GlobalHotkeyEnabled = false,
                ShowNotifications = false,
                NotifyChatResponses = false,
                ShowPairingApprovalDialog = false,
                HasSeenActivityStreamTip = true,
                HasInjectedFirstRunBootstrap = true,
                EnableManagedLocalGatewayAutoRepair = false,
                NodeSystemRunEnabled = false,
                NodeBrowserProxyEnabled = false,
                NodeCanvasEnabled = false,
                NodeScreenEnabled = false,
                NodeCameraEnabled = false,
                NodeLocationEnabled = false,
                NodeSttEnabled = false,
                NodeTtsEnabled = false,
                NodeOllamaInferenceEnabled = false,
                VoiceTtsEnabled = false,
                VoiceAudioFeedback = false,
                UseLegacyWebChat = false,
                ShowCompletedSessions = true,
                ShowChatToolCalls = showChatToolCalls,
                AppTheme = "Light",
                OpenTelemetryEndpoint = null
            };
            File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), settings.ToJson());
            if (startupMode == GatewayFixtureStartupMode.McpOnly) return;
            var registry = new GatewayRegistry(DataDirectory);
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = GatewayId,
                Url = endpoint.AbsoluteUri,
                FriendlyName = $"Fixture Gateway ({RunId[..8]})",
                SharedGatewayToken = token,
                // This is an unmanaged test server, not a provisioned WSL Gateway.
                // Do not claim managed ownership or bypass its credential-provenance checks.
                IsLocal = false
            });
            registry.SetActive(GatewayId);
            if (secondaryPeer is not null)
            {
                var secondaryId = Guid.NewGuid().ToString();
                registry.AddOrUpdate(new GatewayRecord
                {
                    Id = secondaryId,
                    Url = secondaryPeer.Endpoint.AbsoluteUri,
                    FriendlyName = secondaryPeer.FriendlyName,
                    SharedGatewayToken = secondaryPeer.Token,
                    // A second unmanaged synthetic test server, seeded inactive. The app must
                    // connect only to the active record until the UI switches to this one.
                    IsLocal = false
                });
                SecondaryGatewayId = secondaryId;
            }
            registry.Save();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ProcessStartInfo CreateStartInfo(string appPath, int mcpPort, string? postSetupLaunch = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mcpPort is < 1 or > 65535 || mcpPort == GatewayEndpoint.Port
            || mcpPort == _secondaryGatewayPort || mcpPort is 8765 or 18789)
            throw new ArgumentOutOfRangeException(nameof(mcpPort), "A distinct, non-default MCP port is required.");
        var executable = ValidateApp(appPath);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        // No inherited test flags, profile paths or real Gateway overrides may escape into this child.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("OPENCLAW_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["OPENCLAW_GATEWAY_FIXTURE"] = "1";
        start.Environment["OPENCLAW_TRAY_DATA_DIR"] = DataDirectory;
        start.Environment["OPENCLAW_TRAY_LOCAL_DATA_DIR"] = SetupDirectory;
        start.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = RoamingRoot;
        start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = LocalRoot;
        start.Environment["OPENCLAW_MCP_PORT"] = mcpPort.ToString(CultureInfo.InvariantCulture);
        start.Environment["OPENCLAW_SKIP_UPDATE_CHECK"] = "1";
        start.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";
        start.Environment["OPENCLAW_LANGUAGE"] = "en-US";
        if (postSetupLaunch is not null)
        {
            start.ArgumentList.Add("--post-setup-launch");
            start.ArgumentList.Add(postSetupLaunch);
        }
        return start;
    }

    /// <summary>Reject older binaries before they can perform unguarded startup side effects.</summary>
    public static string ValidateApp(string appPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        var executable = Path.GetFullPath(appPath);
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the app before starting a Gateway fixture.", executable);
        if (!string.Equals(Path.GetFileName(executable), "OpenClaw.Tray.WinUI.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AppPath must identify the built OpenClaw.Tray.WinUI.exe.", nameof(appPath));
        var runtimeConfig = Path.ChangeExtension(executable, ".runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)
            || !options.TryGetProperty("configProperties", out var properties)
            || !properties.TryGetProperty("OpenClaw.GatewayFixtureIsolationVersion", out var version)
            || version.ToString() != "1")
        {
            throw new InvalidDataException(
                "This app does not advertise Gateway fixture isolation version 1. Rebuild the app; refusing to launch an older binary.");
        }
        return executable;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "ws"
            || !IPAddress.TryParse(endpoint.Host, out var address) || !address.Equals(IPAddress.Loopback)
            || endpoint.Port is <= 0 or 18789 or 8765
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("A dedicated numeric IPv4 loopback WebSocket endpoint is required.", nameof(endpoint));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EnsureNoReparsePoints(RunDirectory);
        Directory.Delete(RunDirectory, recursive: true);
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing fixture cleanup through a reparse point.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing fixture cleanup containing a reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                EnsureNoReparsePoints(entry);
        }
    }
}
