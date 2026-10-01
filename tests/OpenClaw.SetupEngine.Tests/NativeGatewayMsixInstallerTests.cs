using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewayMsixInstallerTests
{
    [Fact]
    public async Task Install_DownloadsPinnedBundleAndChecksHashBeforeCurrentUserDeployment()
    {
        using var cts = new CancellationTokenSource();
        var commands = new Commands((executable, arguments, timeout, ct) =>
        {
            Assert.Equal(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"), executable);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand"], arguments[..5]);
            Assert.Equal(6, arguments.Length);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[5]));
            Assert.Contains("'https://github.com/openclaw/openclaw-windows-packaging/releases/download/v2026.9.7-msix.0/OpenClawGateway-2026.9.7-msix.0.msixbundle'", script);
            Assert.Contains("'97c335c10ba3ac5bdf473b7043ec828a5a2bdc6bae60dfc988d37887e2b6e221'", script);
            Assert.Contains("$ErrorActionPreference = 'Stop'", script);
            Assert.Contains("Invoke-WebRequest -UseBasicParsing", script);
            Assert.Contains("Get-FileHash -LiteralPath $packagePath -Algorithm SHA256", script);
            Assert.Contains("throw 'The downloaded Gateway MSIX bundle", script);
            Assert.Contains("Add-AppxPackage -Path $packagePath -ErrorAction Stop", script);
            Assert.True(script.IndexOf("Get-FileHash", StringComparison.Ordinal) <
                script.IndexOf("Add-AppxPackage", StringComparison.Ordinal));
            Assert.DoesNotContain("winget", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Import-Certificate", script);
            Assert.Equal(TimeSpan.FromMinutes(5), timeout);
            Assert.Equal(cts.Token, ct);
            return Task.FromResult(Result());
        });

        await new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token);
        Assert.Equal(1, commands.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(unchecked((int)0x8A15000F))]
    public async Task Failure_ReportsExitCodeOutputAndRepairGuidanceWithoutRetry(int exitCode)
    {
        var commands = new Commands((_, _, _, _) =>
            Task.FromResult(Result(exitCode, stdout: "GitHub unavailable", stderr: "Deployment error")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.Contains($"0x{exitCode:X8}", error.Message);
        Assert.Contains("GitHub access", error.Message);
        Assert.Contains("Windows app package installation policy", error.Message);
        Assert.Contains("retry native setup", error.Message);
        Assert.Contains("GitHub unavailable", error.Message);
        Assert.Contains("Deployment error", error.Message);
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
    public async Task RunnerFailure_IsPropagated()
    {
        var failure = new IOException("Process failed");
        var commands = new Commands((_, _, _, _) => throw failure);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task Cancellation_DoesNotStartDownload()
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_IsRemovedAfterSuccessOrCancellation(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        string? packagePath = null;
        var commands = new Commands((_, arguments, _, ct) =>
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[5]));
            var match = Regex.Match(script, @"\$packagePath = '((?:[^']|'')*)'");
            Assert.True(match.Success);
            packagePath = match.Groups[1].Value.Replace("''", "'");
            File.WriteAllText(packagePath, "partial download");
            if (cancel)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Result());
        });

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token));
        else
            await new NativeGatewayMsixInstaller().InstallAsync(commands, cts.Token);
        Assert.NotNull(packagePath);
        Assert.False(File.Exists(packagePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallerScript_DeploysOnlyWhenDownloadedBytesMatchHash(bool hashMatches)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var logger = new SetupLogger(filePath: null);
        var runner = new CommandRunner(logger);
        CommandResult? result = null;
        var commands = new Commands(async (executable, arguments, timeout, ct) =>
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[5]));
            var expectedHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(hashMatches ? "test package" : "different package")));
            script = script.Replace(NativeGatewayMsixInstaller.PackageSha256, expectedHash);
            // Exercise the real PowerShell hash/failure flow without downloading or deploying.
            var stubs = """
                function Invoke-WebRequest {
                    param([switch]$UseBasicParsing, $Uri, $OutFile)
                    [IO.File]::WriteAllBytes($OutFile, [Text.Encoding]::UTF8.GetBytes('test package'))
                }
                function Add-AppxPackage {
                    param($Path, $ErrorAction)
                    if (!(Test-Path -LiteralPath $Path)) { throw 'Download missing' }
                    Write-Output 'DEPLOYMENT_PROOF'
                }
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(stubs + "\n" + script));
            result = await runner.RunAsync(
                executable, [.. arguments[..5], encoded], timeout, ct: ct);
            return result;
        });

        if (hashMatches)
            await new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new NativeGatewayMsixInstaller().InstallAsync(commands, CancellationToken.None));
            Assert.Contains("does not match the pinned release SHA256", error.Message);
        }
        Assert.NotNull(result);
        Assert.Equal(hashMatches, result.Stdout.Contains("DEPLOYMENT_PROOF", StringComparison.Ordinal));
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
