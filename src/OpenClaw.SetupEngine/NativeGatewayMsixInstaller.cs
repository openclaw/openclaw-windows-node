namespace OpenClaw.SetupEngine;

/// <summary>
/// Installs the fixed Gateway Store product through WinGet after setup review consent.
/// Store owns package selection, signature validation and deployment. A successful command
/// still requires current-user package and alias verification by the acquisition owner.
/// </summary>
public sealed class NativeGatewayMsixInstaller
{
    public const string StoreProductId = "9NV70LV3D6XC";

    public async Task InstallAsync(
        ICommandRunner commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        cancellationToken.ThrowIfCancellationRequested();
        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        var result = await commands.RunAsync(
            wingetPath,
            [
                "install", "--id", StoreProductId, "--source", "msstore",
                "--silent", "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity", "--no-upgrade",
            ],
            TimeSpan.FromMinutes(5),
            ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.TimedOut && result.ExitCode == 0)
            return;

        var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
        if (detail.Length > 2048)
            detail = detail[..2048] + "...";
        var guidance = "Ensure App Installer (WinGet) is installed and Microsoft Store access is allowed, then retry native setup.";
        if (result.TimedOut)
            throw new TimeoutException(
                $"WinGet timed out installing the OpenClaw Gateway package. Windows may still finish deployment. {guidance} {detail}");
        throw new InvalidOperationException(
            $"WinGet could not install the OpenClaw Gateway package (exit code 0x{result.ExitCode:X8}). {guidance} {detail}");
    }
}
