namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewayMsixInstallerTests
{
    [Fact]
    public async Task Install_UsesCurrentUserWinGetAndFixedStoreProductExactlyOnce()
    {
        using var cts = new CancellationTokenSource();
        var commands = new Commands((executable, arguments, timeout, ct) =>
        {
            Assert.Equal(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe"), executable);
            Assert.Equal(
                [
                    "install", "--id", "9NV70LV3D6XC", "--source", "msstore",
                    "--silent", "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity", "--no-upgrade",
                ], arguments);
            Assert.Equal(TimeSpan.FromMinutes(5), timeout);
            Assert.Equal(cts.Token, ct);
            return Task.FromResult(Result());
        });

        await new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token);
        Assert.Equal(1, commands.Calls);
    }

    [Fact]
    public async Task PinnedCertificateMismatch_RepairsLatestWinGetAndRetriesOnce()
    {
        var call = 0;
        var commands = new Commands((executable, arguments, timeout, _) =>
        {
            call++;
            Assert.Equal(TimeSpan.FromMinutes(5), timeout);
            if (call is 1 or 3)
            {
                Assert.EndsWith(Path.Combine("Microsoft", "WindowsApps", "winget.exe"), executable);
                Assert.Equal("install", arguments[0]);
                return Task.FromResult(call == 1
                    ? Result(unchecked((int)0x8A15005E), stderr: "certificate mismatch")
                    : Result());
            }

            Assert.EndsWith(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), executable);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-Command"], arguments[..3]);
            Assert.Contains("Install-Module -Name Microsoft.WinGet.Client", arguments[3]);
            Assert.Contains("-Scope CurrentUser", arguments[3]);
            Assert.Contains("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force", arguments[3]);
            Assert.Contains("Repair-WinGetPackageManager -Force -Latest", arguments[3]);
            Assert.DoesNotContain("BypassCertificatePinningForMicrosoftStore", arguments[3]);
            Assert.DoesNotContain("ExecutionPolicy Bypass", arguments[3]);
            Assert.DoesNotContain("ExecutionPolicy Unrestricted", arguments[3]);
            Assert.DoesNotContain("-ExecutionPolicy", arguments);
            return Task.FromResult(Result());
        });

        await new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None);

        Assert.Equal(3, commands.Calls);
    }

    [Fact]
    public async Task WinGetRepairFailure_StopsWithoutRetryingInstallation()
    {
        var call = 0;
        var commands = new Commands((_, _, _, _) => Task.FromResult(++call == 1
            ? Result(unchecked((int)0x8A15005E), stderr: "certificate mismatch")
            : Result(7, stderr: "PowerShell Gallery unavailable")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));

        Assert.Contains("0x00000007", error.Message);
        Assert.Contains("PowerShell Gallery unavailable", error.Message);
        Assert.Contains("Repair-WinGetPackageManager -Force -Latest", error.Message);
        Assert.Equal(2, commands.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(unchecked((int)0x8A15000F))]
    public async Task Failure_ReportsExitCodeOutputAndRepairGuidanceWithoutRetry(int exitCode)
    {
        var commands = new Commands((_, _, _, _) =>
            Task.FromResult(Result(exitCode, stdout: "Store unavailable", stderr: "App Installer error")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.Contains($"0x{exitCode:X8}", error.Message);
        Assert.Contains("App Installer (WinGet)", error.Message);
        Assert.Contains("Microsoft Store access", error.Message);
        Assert.Contains("retry native setup", error.Message);
        Assert.Contains("Store unavailable", error.Message);
        Assert.Contains("App Installer error", error.Message);
        Assert.Equal(1, commands.Calls);
    }

    [Fact]
    public async Task Failure_SanitizesAndBoundsCommandOutput()
    {
        var commands = new Commands((_, _, _, _) => Task.FromResult(
            Result(1, stdout: string.Concat(Enumerable.Repeat("Install status. ", 300)),
                stderr: "Authorization: Bearer secret-value")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.DoesNotContain("secret-value", error.Message);
        Assert.Contains("[REDACTED]", error.Message);
        Assert.EndsWith("...", error.Message);
        Assert.True(error.Message.Length < 2500);
    }

    [Fact]
    public async Task Timeout_IsNotSuccessEvenWithZeroExitCode()
    {
        var commands = new Commands((_, _, _, _) => Task.FromResult(Result(timedOut: true)));
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.Contains("Windows may still finish deployment", error.Message);
    }

    [Fact]
    public async Task RepairTimeout_IsNotRetriedOrReportedAsInstallationSuccess()
    {
        var call = 0;
        var commands = new Commands((_, _, _, _) => Task.FromResult(++call == 1
            ? Result(unchecked((int)0x8A15005E))
            : Result(timedOut: true)));

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));

        Assert.Contains("WinGet repair timed out", error.Message);
        Assert.Equal(2, commands.Calls);
    }

    [Fact]
    public async Task RunnerFailure_IsPropagated()
    {
        var failure = new IOException("Process failed");
        var commands = new Commands((_, _, _, _) => throw failure);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task Cancellation_DoesNotStartWinGet()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var commands = new Commands((_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not install"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token));
        Assert.Equal(0, commands.Calls);
    }

    [Fact]
    public async Task CancelledInstall_DoesNotReportSuccess()
    {
        using var cts = new CancellationTokenSource();
        var commands = new Commands((_, _, _, ct) =>
        {
            Assert.Equal(cts.Token, ct);
            cts.Cancel();
            return Task.FromResult(Result());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token));
    }

    [Fact]
    public async Task Cancellation_AwaitsCommandCleanup()
    {
        using var cts = new CancellationTokenSource();
        var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new Commands(async (_, _, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Result();
            }
            finally
            {
                cleaningUp.SetResult();
                await allowCleanup.Task;
            }
        });
        var install = new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token);
        cts.Cancel();
        try
        {
            await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(install.IsCompleted);
        }
        finally
        {
            allowCleanup.SetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
    }

    private static CommandResult Result(
        int exitCode = 0, string stdout = "", string stderr = "", bool timedOut = false) =>
        new(exitCode, stdout, stderr, TimeSpan.Zero, timedOut);

    private sealed class Commands(
        Func<string, string[], TimeSpan, CancellationToken, Task<CommandResult>> execute) : ICommandRunner
    {
        public int Calls { get; private set; }

        public Task<CommandResult> RunAsync(
            string executable, string[] arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
            string? stdinInput = null, CancellationToken ct = default, Stream? stdinStream = null)
        {
            Calls++;
            return execute(executable, arguments, timeout, ct);
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName, string command, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default,
            string? user = null, bool inputViaStdin = false) =>
            throw new Xunit.Sdk.XunitException("Native installation must not use WSL.");
    }
}
