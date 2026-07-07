using OpenClaw.Connection;
using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayInstallModeDetectorTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(),
        "OpenClawGatewayInstallModeDetectorTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _localDataDir = Path.Combine(
        Path.GetTempPath(),
        "OpenClawGatewayInstallModeDetectorLocalTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_ReturnsNativeWindowsForActiveManagedNativeGateway()
    {
        SaveActive(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (Windows)",
            IsLocal = true,
        });

        var result = GatewayInstallModeDetector.Detect(_dataDir, GatewayInstallMode.Wsl);

        Assert.Equal(GatewayInstallMode.NativeWindows, result);
    }

    [Fact]
    public void Detect_ReturnsWslForActiveManagedDistroGateway()
    {
        SaveActive(new GatewayRecord
        {
            Id = "wsl",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (OpenClaw)",
            IsLocal = true,
            SetupManagedDistroName = "OpenClaw",
        });

        var result = GatewayInstallModeDetector.Detect(_dataDir, GatewayInstallMode.NativeWindows);

        Assert.Equal(GatewayInstallMode.Wsl, result);
    }

    [Fact]
    public void Detect_PreservesFallbackForRemoteOrUnmanagedLocalGateway()
    {
        SaveActive(new GatewayRecord
        {
            Id = "custom",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "My gateway",
            IsLocal = true,
        });

        var result = GatewayInstallModeDetector.Detect(
            _dataDir,
            GatewayInstallMode.NativeWindows);

        Assert.Equal(GatewayInstallMode.NativeWindows, result);
    }

    [Fact]
    public void DetectInstalled_UsesPersistedNativeModeForUninstall()
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            Path.Combine(_localDataDir, "setup-state.json"),
            """{"InstallMode":"NativeWindows"}""");

        var result = GatewayInstallModeDetector.DetectInstalled(
            _dataDir,
            _localDataDir,
            GatewayInstallMode.Wsl);

        Assert.Equal(GatewayInstallMode.NativeWindows, result);
    }

    [Fact]
    public void DetectInstalled_RejectsFriendlyNameAsNativeOwnership()
    {
        SaveActive(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (Windows)",
            IsLocal = true,
        });

        var result = GatewayInstallModeDetector.DetectInstalled(
            _dataDir,
            _localDataDir,
            GatewayInstallMode.Wsl);

        Assert.Equal(GatewayInstallMode.Wsl, result);
    }

    [Fact]
    public void DetectInstalled_FindsInterruptedNativeInstallFromOwnershipMarker()
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ManagedConfigPaths":[]}""");
        SaveActive(new GatewayRecord
        {
            Id = "stale-wsl",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (OpenClawGateway)",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        });

        var result = GatewayInstallModeDetector.DetectInstalled(
            _dataDir,
            _localDataDir,
            GatewayInstallMode.Wsl);

        Assert.Equal(GatewayInstallMode.NativeWindows, result);
        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(_dataDir, _localDataDir));
    }

    [Fact]
    public void HasManagedNativeInstallation_RejectsUnownedNativeCli()
    {
        Assert.False(GatewayInstallModeDetector.HasManagedNativeInstallation(_dataDir, _localDataDir));
    }

    [Fact]
    public void HasManagedNativeInstallation_AcceptsPersistedNativeMode()
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            Path.Combine(_localDataDir, "setup-state.json"),
            """{"InstallMode":"NativeWindows"}""");

        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(_dataDir, _localDataDir));
    }

    [Fact]
    public void HasManagedNativeInstallation_AcceptsMarkerWithoutGatewayRecord()
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ManagedConfigPaths":[]}""");

        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(_dataDir, _localDataDir));
    }

    [Fact]
    public void HasManagedNativeInstallation_ConfigAwareRejectsForeignOwnershipMarker()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = "owned-profile",
        };
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ProfileName":"foreign-profile","TaskName":"OpenClaw Gateway (foreign-profile)"}""");

        Assert.False(GatewayInstallModeDetector.HasManagedNativeInstallation(
            _dataDir,
            _localDataDir,
            config));
    }

    [Fact]
    public void HasManagedNativeInstallation_ConfigAwareRejectsForeignMarkerDespiteLegacyState()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = "owned-profile",
        };
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            Path.Combine(_localDataDir, "setup-state.json"),
            """{"InstallMode":"NativeWindows"}""");
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ProfileName":"foreign-profile","TaskName":"OpenClaw Gateway (foreign-profile)"}""");

        Assert.False(GatewayInstallModeDetector.HasManagedNativeInstallation(
            _dataDir,
            _localDataDir,
            config));
    }

    [Fact]
    public void HasManagedNativeInstallation_ConfigAwareAcceptsLegacyStateWithoutMarkers()
    {
        var config = new SetupConfig { InstallMode = GatewayInstallMode.NativeWindows };
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            Path.Combine(_localDataDir, "setup-state.json"),
            """{"InstallMode":"NativeWindows"}""");

        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(
            _dataDir,
            _localDataDir,
            config));
    }

    [Fact]
    public void HasManagedNativeInstallation_ConfigAwareAcceptsOwnedActiveMarker()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = "owned-profile",
        };
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ProfileName":"owned-profile","TaskName":"OpenClaw Gateway (owned-profile)"}""");

        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(
            _dataDir,
            _localDataDir,
            config));
    }

    [Fact]
    public void NativeIntentMarkerOverridesPriorPersistedWslMode()
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(
            Path.Combine(_localDataDir, "setup-state.json"),
            """{"InstallMode":"Wsl"}""");
        File.WriteAllText(
            GatewayInstallModeDetector.GetNativeOwnershipPath(_localDataDir),
            """{"InstallMode":"NativeWindows","ManagedConfigPaths":[]}""");
        SaveActive(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (Windows)",
            IsLocal = true,
        });

        Assert.Equal(
            GatewayInstallMode.NativeWindows,
            GatewayInstallModeDetector.DetectInstalled(
                _dataDir,
                _localDataDir,
                GatewayInstallMode.Wsl));
        Assert.True(GatewayInstallModeDetector.HasManagedNativeInstallation(_dataDir, _localDataDir));
    }

    [Theory]
    [InlineData(true, false, null, null, true)]
    [InlineData(false, true, null, null, true)]
    [InlineData(false, true, "custom.json", null, false)]
    [InlineData(true, false, null, "native", false)]
    [InlineData(true, false, null, "nativewindow", true)]
    [InlineData(false, false, null, null, false)]
    [SupportedOSPlatform("windows")]
    public void ProgramModeDetection_RespectsExplicitOverrides(
        bool uninstall,
        bool wizardOnly,
        string? configPath,
        string? environmentOverride,
        bool expected)
    {
        Assert.Equal(
            expected,
            Program.ShouldDetectInstalledMode(uninstall, wizardOnly, configPath, environmentOverride));
    }

    [Theory]
    [InlineData("native", true, GatewayInstallMode.NativeWindows)]
    [InlineData("native-windows", true, GatewayInstallMode.NativeWindows)]
    [InlineData("windows", true, GatewayInstallMode.NativeWindows)]
    [InlineData("wsl", true, GatewayInstallMode.Wsl)]
    [InlineData("nativewindow", false, GatewayInstallMode.Wsl)]
    [InlineData("", false, GatewayInstallMode.Wsl)]
    public void TryParseInstallMode_RejectsUnknownValues(
        string value,
        bool expectedSuccess,
        GatewayInstallMode expectedMode)
    {
        var success = SetupConfig.TryParseInstallMode(value, out var mode);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedMode, mode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"NativeWindows\"")]
    public void DetectInstalled_NonObjectStateDoesNotTrustFriendlyName(string stateJson)
    {
        Directory.CreateDirectory(_localDataDir);
        File.WriteAllText(Path.Combine(_localDataDir, "setup-state.json"), stateJson);
        SaveActive(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            FriendlyName = "Local (Windows)",
            IsLocal = true,
        });

        var result = GatewayInstallModeDetector.DetectInstalled(
            _dataDir,
            _localDataDir,
            GatewayInstallMode.Wsl);

        Assert.Equal(GatewayInstallMode.Wsl, result);
    }

    private void SaveActive(GatewayRecord record)
    {
        var registry = new GatewayRegistry(_dataDir);
        registry.AddOrUpdate(record);
        registry.SetActive(record.Id);
        registry.Save();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
        if (Directory.Exists(_localDataDir))
            Directory.Delete(_localDataDir, recursive: true);
    }
}
