using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClaw.Tray.IntegrationTests;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayCleanupTaskTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public Task RegisteredAction_PreservesExitCodeAndDiagnostics(int exitCode) =>
        VerifyRegisteredActionAsync(exitCode, observeVisibility: false);

    [IntegrationFact]
    public async Task RegisteredAction_StaysInvisibleWithoutTaskbarButton()
    {
        foreach (var exitCode in new[] { 0, 37 })
            await VerifyRegisteredActionAsync(exitCode, observeVisibility: true);
    }

    private static async Task VerifyRegisteredActionAsync(int exitCode, bool observeVisibility)
    {
        Assert.True(OperatingSystem.IsWindows(), "Cleanup launch proof requires native Windows.");
        using var temp = new TempDirectory();
        var directory = Directory.CreateDirectory(temp.Combine("cleanup %PATH% & apostrophe's \u00e9\u4e2d")).FullName;
        var hostPath = await GatewayPackageCleanup.DeployAsync(directory);
        // A repeated startup must work while the current helper is locked for execution.
        using (var runningImage = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(hostPath, await GatewayPackageCleanup.DeployAsync(directory));
        await File.WriteAllTextAsync(Path.Combine(directory, "host-path.txt"), hostPath, new System.Text.UTF8Encoding(true));
        var workerPath = Path.Combine(directory, "cleanup.ps1");
        var token = "OpenClaw-visibility-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + token + "-ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + token + "-release");
        await File.WriteAllTextAsync(workerPath, $$"""
            param([string]$Family)
            $ErrorActionPreference = 'Stop'
            if ({{(observeVisibility ? "$true" : "$false")}}) { [Console]::Title = '{{token}}' }
            [pscustomobject]@{
                Family = $Family
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.json') -Encoding UTF8
            $ready = [Threading.EventWaitHandle]::OpenExisting('Local\{{token}}-ready')
            $release = [Threading.EventWaitHandle]::OpenExisting('Local\{{token}}-release')
            try {
                if ({{(observeVisibility ? "$true" : "$false")}}) {
                    $ready.Set() | Out-Null
                    if (-not $release.WaitOne(45000)) { throw 'Visibility controller did not release the worker.' }
                }
            } finally { $ready.Dispose(); $release.Dispose() }
            if ({{exitCode}} -ne 0) { Write-Error 'synthetic worker failure' -ErrorAction Continue }
            exit {{exitCode}}
            """);
        var harnessPath = Path.Combine(directory, "register-test.ps1");
        await File.WriteAllTextAsync(harnessPath, """
            $ErrorActionPreference = 'Stop'
            Import-Module ScheduledTasks -ErrorAction Stop
            # Only the persistence boundary is replaced. Task actions, principals and triggers are real CIM objects.
            $capturePath = Join-Path $PSScriptRoot 'action.json'
            Remove-Item Function:\Register-ScheduledTask -ErrorAction Stop
            Set-Item Function:\Register-ScheduledTask -Options Constant -Value {
                param($TaskName, $Action, $Principal, $Trigger, $Settings, [switch]$Force)
                [pscustomobject]@{
                    Execute = $Action.Execute
                    Arguments = $Action.Arguments
                    LogonType = [string]$Principal.LogonType
                    RunLevel = [string]$Principal.RunLevel
                    Interval = $Trigger[0].Repetition.Interval
                    TriggerCount = @($Trigger).Count
                    ReplacesExisting = $Force.IsPresent
                } | ConvertTo-Json | Set-Content -LiteralPath $capturePath -Encoding UTF8
            }
            & (Join-Path $PSScriptRoot 'cleanup-task.ps1') -TaskName 'OpenClaw disposable launch proof' `
                -HostPath (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'host-path.txt') -Raw) -Family 'Fixture_123'
            """);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var registration = new ProcessStartInfo(powershell);
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harnessPath })
            registration.ArgumentList.Add(argument);
        Assert.Equal(0, await RunAsync(registration, createNoWindow: true));
        using var action = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "action.json")));
        var value = action.RootElement;
        Assert.Equal("Interactive", value.GetProperty("LogonType").GetString());
        Assert.Equal("Limited", value.GetProperty("RunLevel").GetString());
        Assert.Equal("PT1M", value.GetProperty("Interval").GetString());
        Assert.Equal(2, value.GetProperty("TriggerCount").GetInt32());
        Assert.True(value.GetProperty("ReplacesExisting").GetBoolean());

        var launch = new ProcessStartInfo(value.GetProperty("Execute").GetString()!,
            value.GetProperty("Arguments").GetString()!);
        if (observeVisibility)
        {
            using var visibility = await ObserveAsync(launch, directory, token, positiveControl: false);
            Assert.Equal(exitCode, visibility.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.Empty(visibility.RootElement.GetProperty("Errors").EnumerateArray());
            Assert.DoesNotContain(visibility.RootElement.GetProperty("Observations").EnumerateArray(),
                item => item.GetProperty("OnScreen").GetBoolean());
            Assert.True(visibility.RootElement.GetProperty("WindowSamples").GetInt32() >= 20);
            Assert.True(visibility.RootElement.GetProperty("TaskbarSamples").GetInt32() >= 2);
        }
        else
        {
            Assert.Equal(exitCode, await RunAsync(launch, createNoWindow: false));
        }
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
        Assert.Equal("Fixture_123", result.RootElement.GetProperty("Family").GetString());
        var errorPath = Path.Combine(directory, "cleanup-host-error.txt");
        if (exitCode != 0)
        {
            var error = await File.ReadAllTextAsync(errorPath);
            // Windows PowerShell wraps rendered errors at the host's line width.
            var message = System.Text.RegularExpressions.Regex.Replace(error, @"\s+", " ");
            Assert.True(message.Contains("synthetic worker failure", StringComparison.Ordinal), error);
        }
        else
            Assert.False(File.Exists(errorPath));
    }

    [IntegrationFact]
    public async Task VisibilityProbe_VisibleConsole_IsDetectedByEventsSamplesAndTaskbar()
    {
        using var temp = new TempDirectory();
        var token = "OpenClaw-positive-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + token + "-ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + token + "-release");
        var worker = temp.Combine("positive.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CleanupPositiveConsoleIdentity.cs"),
            temp.Combine("CleanupPositiveConsoleIdentity.cs"));
        await File.WriteAllTextAsync(worker, $$"""
            [Console]::Title = '{{token}}'
            Add-Type -Path (Join-Path $PSScriptRoot 'CleanupPositiveConsoleIdentity.cs')
            # Grouped taskbar buttons expose the AppUserModelID, not the console title.
            [CleanupPositiveConsoleIdentity]::SetTaskbarIdentity('{{token}}')
            $ready = [Threading.EventWaitHandle]::OpenExisting('Local\{{token}}-ready')
            $release = [Threading.EventWaitHandle]::OpenExisting('Local\{{token}}-release')
            try {
                $ready.Set() | Out-Null
                if (-not $release.WaitOne(45000)) { throw 'Visibility positive control was not detected.' }
            } finally { $ready.Dispose(); $release.Dispose() }
            """);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var launch = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"),
            "\"" + powershell + "\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + worker + "\"");
        using var visibility = await ObserveAsync(launch, temp.Path, token, positiveControl: true);
        Assert.Equal(0, visibility.RootElement.GetProperty("ExitCode").GetInt32());
        var observations = visibility.RootElement.GetProperty("Observations").EnumerateArray().ToArray();
        foreach (var source in new[] { "WinEvent", "WindowSample" })
            Assert.Contains(observations, item => item.GetProperty("Source").GetString() == source &&
                item.GetProperty("OnScreen").GetBoolean());
        Assert.Contains(observations, item => item.GetProperty("Source").GetString() == "TaskbarUIA" &&
            item.GetProperty("OnScreen").GetBoolean());
    }

    private static async Task<JsonDocument> ObserveAsync(ProcessStartInfo launch, string directory, string token,
        bool positiveControl)
    {
        var output = Path.Combine(directory, "visibility.json");
        var monitor = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(AppContext.BaseDirectory, "RunCleanupVisibilityProbe.ps1"),
            "-Executable", launch.FileName,
            "-ArgumentsBase64", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(launch.Arguments)),
            "-Directory", directory, "-Token", token, "-ReadyName", @"Local\" + token + "-ready",
            "-ReleaseName", @"Local\" + token + "-release", "-ResultPath", output })
            monitor.ArgumentList.Add(argument);
        if (positiveControl) monitor.ArgumentList.Add("-PositiveControl");
        Assert.Equal(0, await RunAsync(monitor, createNoWindow: true));
        return JsonDocument.Parse(await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Launcher_MissingOrTerminatingWorker_ReportsFailure(bool missing)
    {
        using var temp = new TempDirectory();
        var launcher = await GatewayPackageCleanup.DeployAsync(temp.Path);
        var worker = Path.Combine(temp.Path, "cleanup.ps1");
        if (missing) File.Delete(worker);
        else await File.WriteAllTextAsync(worker, "throw 'synthetic terminating cleanup failure'");
        var start = new ProcessStartInfo(launcher, "Fixture_123");
        Assert.NotEqual(0, await RunAsync(start, createNoWindow: false));
        Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(temp.Combine("cleanup-host-error.txt"))));
    }

    [Fact]
    public async Task Registration_MissingHelper_RejectsBeforeTaskPersistence()
    {
        using var temp = new TempDirectory();
        var launcher = await GatewayPackageCleanup.DeployAsync(temp.Path);
        File.Delete(launcher);
        await File.WriteAllTextAsync(temp.Combine("blocked.ps1"), """
            $ErrorActionPreference = 'Stop'
            Import-Module ScheduledTasks -ErrorAction Stop
            Remove-Item Function:\Register-ScheduledTask -ErrorAction Stop
            Set-Item Function:\Register-ScheduledTask -Options Constant -Value { throw 'Persistence must not run.' }
            try {
                & (Join-Path $PSScriptRoot 'cleanup-task.ps1') -TaskName 'Never registered' `
                    -HostPath (Join-Path $PSScriptRoot 'missing-helper.exe') -Family 'Fixture_123'
                throw 'Expected missing helper rejection.'
            } catch {
                if ($_.Exception.Message -notlike '*windowless cleanup host is missing*') { throw }
            }
            """);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", temp.Combine("blocked.ps1") })
            start.ArgumentList.Add(argument);
        Assert.Equal(0, await RunAsync(start, createNoWindow: true));
    }

    private static async Task<int> RunAsync(ProcessStartInfo start, bool createNoWindow)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = createNoWindow;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var error = (await stderr).Trim();
            Assert.True(error.Length == 0, error);
            await stdout;
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
