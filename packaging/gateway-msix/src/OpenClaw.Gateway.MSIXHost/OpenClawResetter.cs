namespace OpenClaw.MSIXHost;

public static class OpenClawResetter
{
    public static async Task ResetAsync(
        string nodePath,
        string installDirectory,
        string stateDirectory,
        bool includeUserState,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string fullInstallDirectory = ValidateGatewayResetPath(installDirectory);
        string? fullStateDirectory = includeUserState
            ? ValidateStateResetPath(stateDirectory)
            : null;
        string entryPoint = Path.Combine(fullInstallDirectory, "openclaw.mjs");
        if (File.Exists(entryPoint))
        {
            log("Asking OpenClaw to stop any running gateway.");
            using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            stopTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                int exitCode = await GatewayLauncher.RunAsync(
                    nodePath,
                    fullInstallDirectory,
                    ["gateway", "stop"],
                    stopTimeout.Token,
                    log);
                if (exitCode != 0)
                {
                    log(
                        $"OpenClaw gateway stop exited with code {exitCode}; " +
                        "checking the recorded gateway process.");
                }
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or
                FileNotFoundException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                log(
                    $"OpenClaw gateway stop could not complete: {exception.Message}");
            }
        }

        await GatewayProcessRegistration.StopRegisteredGatewayAsync(
            fullInstallDirectory,
            log,
            cancellationToken);

        string installRoot = Path.GetDirectoryName(fullInstallDirectory)!;
        string installName = Path.GetFileName(fullInstallDirectory);
        string temporaryDirectory = Path.Combine(installRoot, $".{installName}.staging");
        string backupDirectory = Path.Combine(installRoot, $".{installName}.previous");

        log("Waiting for any active preparation to finish.");
        await using (await InstallDirectoryLock.AcquireAsync(
            fullInstallDirectory,
            cancellationToken))
        {
            DeleteDirectory(fullInstallDirectory);
            DeleteDirectory(temporaryDirectory);
            DeleteDirectory(backupDirectory);
        }

        DeleteDirectory(installRoot);
        if (includeUserState)
        {
            log($"Removing OpenClaw configuration and user data: {fullStateDirectory}");
            DeleteDirectory(fullStateDirectory!);
        }

        log(
            includeUserState
                ? "Gateway files and OpenClaw user data were reset."
                : "Prepared gateway files were reset; OpenClaw user data was preserved.");
    }

    private static string ValidateGatewayResetPath(string path)
    {
        string fullPath = ValidateResetPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                "app",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(parent),
                ".openclaw-msix",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset an unexpected gateway directory: {fullPath}");
        }

        return fullPath;
    }

    private static string ValidateStateResetPath(string path)
    {
        string fullPath = ValidateResetPath(path);
        if (!string.Equals(
            Path.GetFileName(fullPath),
            ".openclaw",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset an unexpected OpenClaw state directory: {fullPath}");
        }

        return fullPath;
    }

    private static string ValidateResetPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException("Reset path has no filesystem root.");
        string userProfile = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, userProfile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset unsafe directory: {fullPath}");
        }

        return fullPath;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
