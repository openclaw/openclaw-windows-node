using System.Diagnostics;
using System.Text;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Runs named production functions from Uninstall-LocalGateway.ps1 under Windows PowerShell 5.1,
/// the host Inno uses. It loads only those functions: never dot-source the uninstaller, because
/// its top-level code operates on WSL and user state.
/// </summary>
internal static class UninstallScriptHost
{
    public static async Task<string> RunAsync(
        TempDirectory temp,
        IReadOnlyList<string> functions,
        string body,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var command = """
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
                throw 'Installer helper regression must run under Windows PowerShell 5.1.'
            }
            $tokens = $null
            $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $env:OPENCLAW_TEST_UNINSTALL_SCRIPT, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw 'Uninstall script failed to parse.' }
            foreach ($name in ($env:OPENCLAW_TEST_UNINSTALL_FUNCTIONS -split ',')) {
                $definitions = @($ast.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $false))
                if ($definitions.Count -ne 1) { throw "Expected exactly one function: $name" }
                . ([scriptblock]::Create($definitions[0].Extent.Text))
            }
            $AppRoot = $env:OPENCLAW_TEST_SETTINGS_DIR
            $wslLogPath = Join-Path $AppRoot 'uninstall.log'
            $cleanupWarnings = New-Object 'System.Collections.Generic.List[string]'

            """ + body;
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = temp.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
        })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["OPENCLAW_TEST_UNINSTALL_SCRIPT"] = Path.Combine(root, "scripts", "Uninstall-LocalGateway.ps1");
        startInfo.Environment["OPENCLAW_TEST_UNINSTALL_FUNCTIONS"] = string.Join(',', functions);
        startInfo.Environment["OPENCLAW_TEST_SETTINGS_DIR"] = temp.Path;
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}
