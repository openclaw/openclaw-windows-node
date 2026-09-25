using System.Diagnostics;
using System.Text;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class UninstallOnboardingSettingsTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task Reset_RemovesLegacyCredentialsWithoutChangingOtherPreferences(
        bool preserveNodeSettings, bool hasGatewayUrl, bool hasTokens)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var settings = new JsonObject
        {
            ["EnableNodeMode"] = true,
            ["AutoStart"] = true,
            ["EnableMcpServer"] = true,
            ["Theme"] = "Dark",
            ["Preferences"] = new JsonObject { ["Accent"] = "Blue" },
        };
        if (hasGatewayUrl)
            settings["GatewayUrl"] = "ws://localhost:18789";
        if (hasTokens)
        {
            settings["Token"] = "gateway-token";
            settings["BootstrapToken"] = "test-auth-token";
        }
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, settings.ToJsonString());
        // This pins the reset helper's boundary, not the separate registry-cleanup path.
        const string externalRegistry = """{"activeId":"external","gateways":[{"id":"external","url":"wss://gateway.example","sharedGatewayToken":"test-token-placeholder"}]}""";
        File.WriteAllText(temp.Combine("gateways.json"), externalRegistry);

        await RunResetAsync(temp, preserveNodeSettings);

        settings.Remove("GatewayUrl");
        settings.Remove("Token");
        settings.Remove("BootstrapToken");
        settings["EnableNodeMode"] = preserveNodeSettings;
        settings["AutoStart"] = preserveNodeSettings;
        Assert.True(JsonNode.DeepEquals(settings, JsonNode.Parse(File.ReadAllText(path))));
        Assert.Equal(externalRegistry, File.ReadAllText(temp.Combine("gateways.json")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        var log = File.ReadAllText(temp.Combine("uninstall.log"));
        Assert.DoesNotContain("gateway-token", log);
        Assert.DoesNotContain("test-auth-token", log);
        Assert.DoesNotContain("test-token-placeholder", log);
        var resetJson = File.ReadAllText(path);
        await RunResetAsync(temp, preserveNodeSettings);
        Assert.Equal(resetJson, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_AlreadyCleanSettings_DoesNotRewrite(bool preserveNodeSettings)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var path = temp.Combine("settings.json");
        const string cleanJson = """{ "Theme": "Dark" }""";
        File.WriteAllText(path, cleanJson);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWrite = File.GetLastWriteTimeUtc(path);

        await RunResetAsync(temp, preserveNodeSettings);

        Assert.Equal(cleanJson, File.ReadAllText(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        Assert.Contains("No onboarding settings needed reset.", File.ReadAllText(temp.Combine("uninstall.log")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_WriteFailure_ReportsWarningAndPreservesOriginal(bool preserveNodeSettings)
    {
        using var temp = new TempDirectory("uninstall-settings-");
        var path = temp.Combine("settings.json");
        const string original = """{"Token":"gateway-token","BootstrapToken":"test-auth-token","Theme":"Dark"}""";
        File.WriteAllText(path, original);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await RunResetAsync(temp, preserveNodeSettings, expectedWarnings: 1);

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        var log = File.ReadAllText(temp.Combine("uninstall.log"));
        Assert.Contains("Failed to reset onboarding settings:", log);
        Assert.DoesNotContain("Reset onboarding settings;", log);
        Assert.DoesNotContain("gateway-token", log);
        Assert.DoesNotContain("test-auth-token", log);
    }

    private static async Task RunResetAsync(
        TempDirectory temp, bool preserveNodeSettings, int expectedWarnings = 0)
    {
        // Load only the production JSON/reset/logging functions. Never dot-source
        // the uninstaller: its top-level code operates on WSL and user state.
        const string command = """
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
                throw 'Installer helper regression must run under Windows PowerShell 5.1.'
            }
            $tokens = $null
            $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $env:OPENCLAW_TEST_UNINSTALL_SCRIPT, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw 'Uninstall script failed to parse.' }
            foreach ($name in @(
                'Ensure-AppRoot', 'Write-GatewayLog', 'Add-CleanupWarning',
                'Read-JsonFile', 'Write-JsonFileAtomic', 'Reset-OnboardingSettings'
            )) {
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
            Reset-OnboardingSettings -DataDir $AppRoot -PreserveNodeSettings:([bool]::Parse($env:OPENCLAW_TEST_PRESERVE_NODE))
            Write-Output $cleanupWarnings.Count
            """;
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
        startInfo.Environment["OPENCLAW_TEST_SETTINGS_DIR"] = temp.Path;
        startInfo.Environment["OPENCLAW_TEST_PRESERVE_NODE"] = preserveNodeSettings.ToString();

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
        Assert.Equal(expectedWarnings.ToString(), (await stdout).Trim());
    }
}
