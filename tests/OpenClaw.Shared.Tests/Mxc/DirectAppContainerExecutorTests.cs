using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// Unit tests for <see cref="DirectAppContainerExecutor"/> that don't actually
/// spawn wxc-exec. End-to-end smoke is covered by
/// <see cref="MxcCommandRunnerIntegrationTests"/>.
/// </summary>
public class DirectAppContainerExecutorTests
{
    [Fact]
    public async Task ScratchConfigFile_IsBomFreeAndRestrictedToUserAndSystem()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = DirectAppContainerExecutor.CreateScratchDir();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Equal(2, rules.Length);
            var allowedSids = new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
            foreach (var rule in rules)
                Assert.Contains(rule.IdentityReference.Value, allowedSids);
            var file = Path.Combine(path, "config.json");
            await DirectAppContainerExecutor.WriteConfigFileAsync(file,
                """{"process":{"env":["CONTROL=synthetic ☃"]}}""", CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            using var parsed = JsonDocument.Parse(bytes);
            Assert.Equal("CONTROL=synthetic ☃", parsed.RootElement.GetProperty("process").GetProperty("env")[0].GetString());
            var fileRules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Assert.Equal(2, fileRules.Length);
            foreach (var rule in fileRules)
                Assert.Contains(rule.IdentityReference.Value, allowedSids);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static SandboxExecutionRequest NewRequest() => new(
        CapabilityCommand: "system.run",
        Args: JsonDocument.Parse("{\"command\":\"echo hi\",\"shell\":\"cmd\"}").RootElement,
        Policy: new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: Array.Empty<string>(),
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: Array.Empty<string>(),
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000),
        TimeoutMs: 30_000);

    [Fact]
    public async Task ExecuteAsync_AppContainerUnavailable_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: false,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: false,
            wxcExecPath: null,
            unsupportedReasons: new[] { "test reason" });
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("test reason", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WxcExecNotResolvable_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: false,
            wxcExecPath: null,
            unsupportedReasons: Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("wxc-exec.exe not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WxcExecPathMissingOnDisk_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        // MxcExecutor's ctor throws FileNotFoundException → wrapped in SandboxUnavailableException.
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
    }

    [Fact]
    public async Task ExecuteAsync_DaclTier_IsUnavailableBeforeLaunch()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>(),
            isolationTier: "appcontainer-dacl",
            needsDaclAugmentation: true);
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(
            () => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("requires MXC BaseContainer", ex.Message);
    }

    [Theory]
    [InlineData("0.9.0-alpha", false, 0, false, true)]
    [InlineData("0.8.0-alpha", false, 0, false, false)]
    [InlineData("0.7.0-alpha", false, 0, false, false)]
    [InlineData("0.9.0-alpha", true, 0, false, false)]
    [InlineData("0.9.0-alpha", false, 1, false, false)]
    [InlineData("0.9.0-alpha", false, 2, false, false)]
    [InlineData("0.9.0-alpha", false, 0, true, false)]
    public void IsSystemRunConfigBaseContainerCompatible_RequiresExactEmittedContract(
        string version,
        bool leastPrivilege,
        int deniedPathMode,
        bool hasContainment,
        bool expected)
    {
        var config = new MxcConfig
        {
            Version = version,
            ContainerId = "test",
            Containment = hasContainment ? "process" : null,
            Process = new MxcProcess { CommandLine = "cmd.exe /c echo hi" },
            Network = new MxcNetwork { Egress = new() },
            ProcessContainer = new MxcProcessContainer
            {
                LeastPrivilege = leastPrivilege,
            },
            Filesystem = new MxcFilesystem
            {
                ReadonlyPaths = [@"C:\Users\test\Downloads"],
                DeniedPaths = deniedPathMode switch
                {
                    1 => Array.Empty<string>(),
                    2 => [@"C:\secret"],
                    _ => null,
                },
            },
        };

        Assert.Equal(
            expected,
            MxcIsolationTierPolicy.IsSystemRunConfigBaseContainerCompatible(config));
    }

    [Fact]
    public async Task ExecuteAsync_CustomExecutor_NeverReceivesNewPolicy()
    {
        var availability = new MxcAvailability(
            true, false, true, @"C:\custom\wxc-exec.exe", Array.Empty<string>(),
            isolationTier: "base-container", needsDaclAugmentation: false,
            usesCustomExecutor: true);
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var error = await Assert.ThrowsAsync<SandboxUnavailableException>(
            () => executor.ExecuteAsync(NewRequest()));

        Assert.Contains("requires the bundled executor", error.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesAvailabilityPerCall_PicksUpRecovery()
    {
        // Simulates a transient startup probe error that later recovers. The executor
        // must read the provider on each call, not freeze the first snapshot — else a
        // momentary startup glitch would pin it to "unavailable" for its lifetime.
        var calls = 0;
        var unavailable = new MxcAvailability(
            isAppContainerAvailable: false,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: null,
            unsupportedReasons: new[] { "transient probe error" },
            probeErrored: true);
        var recoveredButPathMissing = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>(),
            isolationTier: "base-container");

        var executor = new DirectAppContainerExecutor(
            () => { calls++; return calls == 1 ? unavailable : recoveredButPathMissing; },
            NullLogger.Instance);

        // First call: unavailable → throws with the transient reason.
        var ex1 = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("transient probe error", ex1.Message);

        // Second call: provider now reports available (path just doesn't exist on
        // disk). The DIFFERENT failure proves the executor re-resolved availability
        // instead of reusing the first (unavailable) snapshot.
        var ex2 = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("wxc-exec.exe not found at", ex2.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Name_IsStableForTelemetry()
    {
        var availability = new MxcAvailability(false, false, false, null, Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);
        Assert.Equal("mxc-direct-appc", executor.Name);
        Assert.True(executor.IsContained);
    }

    [Fact]
    public void BuildRedactedConfigSummary_DoesNotExposeWxcExecOrRequestPaths()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\secret\\mxc\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>());
        var config = new MxcConfig
        {
            ContainerId = "test",
            Process = new MxcProcess
            {
                CommandLine = "cmd /c echo hi",
                Cwd = "C:\\secret\\work",
                Env = new[] { "SECRET_PATH=C:\\secret\\value" },
            },
            Filesystem = new MxcFilesystem
            {
                ReadwritePaths = new[] { "C:\\secret\\repo" },
                ReadonlyPaths = new[] { "C:\\secret\\docs" },
                DeniedPaths = new[] { "C:\\secret\\.ssh" },
            },
        };

        var summary = DirectAppContainerExecutor.BuildRedactedConfigSummary(
            availability,
            config,
            "{\"fake\":true}",
            NewRequest());

        Assert.Contains("wxcExec=<set>", summary);
        Assert.Contains("cwd=<set>", summary);
        Assert.Contains("envKeys=[SECRET_PATH]", summary);
        Assert.DoesNotContain("C:\\secret", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd /c echo hi", summary, StringComparison.OrdinalIgnoreCase);
    }
}
