namespace OpenClaw.SetupEngine;

/// <summary>
/// Installs the fixed Gateway Store product through WinGet after setup review consent.
/// Store owns package selection, signature validation and deployment. A successful command
/// still requires current-user package and alias verification by the acquisition owner.
/// </summary>
public sealed class NativeGatewayMsixInstaller
{
    public const string StoreProductId = "9NV70LV3D6XC";
    private const int PinnedCertificateMismatch = unchecked((int)0x8A15005E);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private const string RepairScript = """
        $ErrorActionPreference = 'Stop'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
        Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery -Scope CurrentUser -AllowClobber -Confirm:$false
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force
        Import-Module Microsoft.WinGet.Client -Force
        Repair-WinGetPackageManager -Force -Latest -ErrorAction Stop
        """;

    public async Task InstallAsync(
        ICommandRunner commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        cancellationToken.ThrowIfCancellationRequested();
        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        Task<CommandResult> Install() => commands.RunAsync(
            wingetPath,
            [
                "install", "--id", StoreProductId, "--source", "msstore",
                "--silent", "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity", "--no-upgrade",
            ],
            CommandTimeout,
            ct: cancellationToken);

        var result = await Install();
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.TimedOut && result.ExitCode == PinnedCertificateMismatch)
        {
            await RepairWinGetAsync(commands, cancellationToken);
            result = await Install();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!result.TimedOut && result.ExitCode == 0)
            return;
        ThrowInstallFailure(result);
    }

    private static async Task RepairWinGetAsync(
        ICommandRunner commands,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var repair = await commands.RunAsync(
            powershellPath,
            ["-NoProfile", "-NonInteractive", "-Command", RepairScript],
            CommandTimeout,
            ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (repair.TimedOut)
            throw new TimeoutException(
                "WinGet repair timed out. Update App Installer in Microsoft Store, then retry native setup. " +
                FormatResult(repair));
        if (repair.ExitCode != 0)
            throw new InvalidOperationException(
                $"WinGet could not be repaired (exit code 0x{repair.ExitCode:X8}). " +
                "Update App Installer in Microsoft Store or run Repair-WinGetPackageManager -Force -Latest, then retry native setup. " +
                FormatResult(repair));
    }

    private static void ThrowInstallFailure(CommandResult result)
    {
        var detail = FormatResult(result);
        var guidance = "Ensure App Installer (WinGet) is installed and Microsoft Store access is allowed, then retry native setup.";
        if (result.TimedOut)
            throw new TimeoutException(
                $"WinGet timed out installing the OpenClaw Gateway package. Windows may still finish deployment. {guidance} {detail}");
        throw new InvalidOperationException(
            $"WinGet could not install the OpenClaw Gateway package (exit code 0x{result.ExitCode:X8}). {guidance} {detail}");
    }

    private static string FormatResult(CommandResult result)
    {
        var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
        if (detail.Length > 2048)
            detail = detail[..2048] + "...";
        return detail;
    }
}
