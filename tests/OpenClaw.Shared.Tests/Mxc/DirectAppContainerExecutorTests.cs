using System.Text.Json;
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
        var executor = new DirectAppContainerExecutor(availability, NullLogger.Instance);

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
        var executor = new DirectAppContainerExecutor(availability, NullLogger.Instance);

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
        var executor = new DirectAppContainerExecutor(availability, NullLogger.Instance);

        // MxcExecutor's ctor throws FileNotFoundException → wrapped in SandboxUnavailableException.
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
    }

    [Fact]
    public void Name_IsStableForTelemetry()
    {
        var availability = new MxcAvailability(false, false, false, null, Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(availability, NullLogger.Instance);
        Assert.Equal("mxc-direct-appc", executor.Name);
        Assert.True(executor.IsContained);
    }
}
