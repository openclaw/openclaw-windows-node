using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayCleanupTaskTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public async Task RegisteredAction_RunsWorkerWithoutDesktopConsole_AndPreservesExitCode(int exitCode)
    {
        Assert.True(OperatingSystem.IsWindows(), "Cleanup launch proof requires native Windows.");
        using var temp = new TempDirectory();
        var directory = Directory.CreateDirectory(temp.Combine("cleanup with spaces & apostrophe's")).FullName;
        var hostPath = await GatewayPackageCleanup.DeployAsync(directory);
        // A repeated startup must work while the current helper is locked for execution.
        using (var runningImage = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(hostPath, await GatewayPackageCleanup.DeployAsync(directory));
        await File.WriteAllTextAsync(Path.Combine(directory, "host-path.txt"), hostPath);
        var workerPath = Path.Combine(directory, "cleanup.ps1");
        await File.WriteAllTextAsync(workerPath, $$"""
            param([string]$Family)
            $ErrorActionPreference = 'Stop'
            Add-Type @'
            using System;
            using System.Runtime.InteropServices;
            public static class ConsoleProbe {
                [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
                [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
                [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
            }
            '@
            $window = [ConsoleProbe]::GetConsoleWindow()
            [pscustomobject]@{
                Family = $Family
                HasConsole = ($window -ne [IntPtr]::Zero)
                HasDesktopConsole = ($window -ne [IntPtr]::Zero -and [ConsoleProbe]::GetParent($window) -ne [IntPtr]::new(-3))
                Visible = [ConsoleProbe]::IsWindowVisible($window)
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.json') -Encoding UTF8
            if ({{exitCode}} -ne 0) { [Console]::Error.WriteLine('synthetic worker failure') }
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

        // Check the actual scheduler entrypoint, not just the console state after PowerShell startup.
        // A console-subsystem action can flash and detach before the worker can sample it.
        using (var binary = File.OpenRead(value.GetProperty("Execute").GetString()!))
        using (var pe = new PEReader(binary))
            Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);

        // Do not use CREATE_NO_WINDOW here: that would mask a regression to direct powershell.exe.
        var launch = new ProcessStartInfo(value.GetProperty("Execute").GetString()!,
            value.GetProperty("Arguments").GetString()!);
        Assert.Equal(exitCode, await RunAsync(launch, createNoWindow: false));
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
        Assert.Equal("Fixture_123", result.RootElement.GetProperty("Family").GetString());
        Assert.False(result.RootElement.GetProperty("HasConsole").GetBoolean());
        Assert.False(result.RootElement.GetProperty("HasDesktopConsole").GetBoolean(),
            "The cleanup worker must not allocate a desktop console, even one hidden after startup.");
        Assert.False(result.RootElement.GetProperty("Visible").GetBoolean());
        var errorPath = Path.Combine(directory, "cleanup-host-error.txt");
        if (exitCode != 0)
            Assert.Contains("synthetic worker failure", await File.ReadAllTextAsync(errorPath));
        else
            Assert.False(File.Exists(errorPath));
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
            Assert.Equal("", (await stderr).Trim());
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
