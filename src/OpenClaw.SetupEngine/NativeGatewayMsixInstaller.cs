using System.Text;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Installs the pinned, signed Gateway release after setup review consent.
/// Windows owns bundle architecture selection, signature validation and deployment. A successful command
/// still requires current-user package and alias verification by the acquisition owner.
/// </summary>
public sealed class NativeGatewayMsixInstaller
{
    public const string ReleaseVersion = "2026.9.7-msix.0";
    public const string PackageUrl =
        "https://github.com/openclaw/openclaw-windows-packaging/releases/download/v2026.9.7-msix.0/OpenClawGateway-2026.9.7-msix.0.msixbundle";
    public const string PackageSha256 = "97c335c10ba3ac5bdf473b7043ec828a5a2bdc6bae60dfc988d37887e2b6e221";

    public async Task InstallAsync(
        ICommandRunner commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        cancellationToken.ThrowIfCancellationRequested();
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var packagePath = Path.Combine(Path.GetTempPath(), $"openclaw-gateway-{Guid.NewGuid():N}.msixbundle");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            # Do not inherit PowerShell 7 module paths from a development launcher.
            $env:PSModulePath = "$PSHOME\Modules"
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $packagePath = '{{packagePath.Replace("'", "''")}}'
            Invoke-WebRequest -UseBasicParsing -Uri '{{PackageUrl}}' -OutFile $packagePath
            if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne '{{PackageSha256}}') {
                throw 'The downloaded Gateway MSIX bundle does not match the pinned release SHA256.'
            }
            Add-AppxPackage -Path $packagePath -ErrorAction Stop
            """;
        CommandResult result;
        try
        {
            result = await commands.RunAsync(
                powershellPath,
                ["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                TimeSpan.FromMinutes(5),
                ct: cancellationToken);
        }
        finally
        {
            // CommandRunner awaits process cleanup before we remove a partial download.
            File.Delete(packagePath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.TimedOut && result.ExitCode == 0)
            return;

        var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
        if (detail.Length > 2048)
            detail = detail[..2048] + "...";
        var guidance = "Check GitHub access and Windows app package installation policy, then retry native setup.";
        if (result.TimedOut)
            throw new TimeoutException(
                $"Gateway MSIX installation timed out. Windows may still finish deployment. {guidance} {detail}");
        throw new InvalidOperationException(
            $"Could not install Gateway MSIX {ReleaseVersion} (exit code 0x{result.ExitCode:X8}). {guidance} {detail}");
    }
}
