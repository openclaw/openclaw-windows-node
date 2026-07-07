using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

public sealed class InstallNativeCliStep : SetupStep
{
    private const string CliPathMarker = "OPENCLAW_CLI_PATH=";
    private const string CliPrefixEnvironmentVariable = "OPENCLAW_SETUP_CLI_PREFIX";

    public override string Id => "install-native-cli";
    public override string DisplayName => "Install OpenClaw for Windows";
    public override RetryPolicy Retry => new(MaxAttempts: 2, InitialDelay: TimeSpan.FromSeconds(5));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var installUrl = ctx.Config.Gateway.WindowsInstallUrl ?? GatewayLkgVersion.DefaultWindowsInstallUrl;
        if (!Uri.TryCreate(installUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return StepResult.Fail($"Windows installer URL must be HTTPS: {installUrl}");
        }

        string script;
        try
        {
            script = BuildInstallScript(installUrl, ctx.Config.Gateway.Version);
        }
        catch (ArgumentException ex)
        {
            return StepResult.Fail(ex.Message);
        }

        var encodedScript = EncodePowerShellScript(script);
        var cliPrefix = GatewayCliRunner.GetManagedNativeCliPrefix(ctx.LocalDataDir);
        var installEnvironment = new Dictionary<string, string>(
            GatewayCliRunner.GetManagedNativeEnvironmentDefaults(ctx.Config))
        {
            [CliPrefixEnvironmentVariable] = cliPrefix,
            ["NPM_CONFIG_PREFIX"] = cliPrefix,
            ["npm_config_prefix"] = cliPrefix,
        };
        var result = await ctx.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedScript],
            TimeSpan.FromMinutes(10),
            installEnvironment,
            ct: ct);
        if (result.ExitCode != 0)
            return StepResult.Fail($"Native CLI install failed (exit {result.ExitCode}): {FirstUsefulLine(result)}");

        var cliPath = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(CliPathMarker, StringComparison.Ordinal))
            ?[CliPathMarker.Length..];
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
            return StepResult.Fail("OpenClaw installed, but its Windows command launcher could not be located.");

        ctx.NativeCliPath = cliPath;
        var verify = await GatewayCliRunner.RunNativeAsync(ctx, ["--version"], TimeSpan.FromSeconds(30), ct: ct);
        if (verify.ExitCode != 0 || string.IsNullOrWhiteSpace(verify.Stdout))
            return StepResult.Fail($"OpenClaw Windows CLI verification failed: {FirstUsefulLine(verify)}");
        var requestedVersion = ctx.Config.Gateway.Version;
        if (IsExactVersionRequest(requestedVersion)
            && !MatchesRequestedVersion(verify.Stdout, requestedVersion!))
        {
            return StepResult.Fail(
                $"OpenClaw Windows CLI version mismatch: expected {requestedVersion!.Trim()}, " +
                $"found {verify.Stdout.Trim()} at {cliPath}.");
        }

        return StepResult.Ok($"CLI installed: {verify.Stdout.Trim()}");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!ctx.IsUninstalling)
            return Task.CompletedTask;

        var cliPrefix = GatewayCliRunner.GetManagedNativeCliPrefix(ctx.LocalDataDir);
        if (!Directory.Exists(cliPrefix))
            return Task.CompletedTask;
        if (new DirectoryInfo(cliPrefix).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"Refusing to remove app-owned native CLI reparse point '{cliPrefix}'.");

        Directory.Delete(cliPrefix, recursive: true);
        if (Directory.Exists(cliPrefix))
            throw new InvalidOperationException($"App-owned native CLI directory still exists after removal: '{cliPrefix}'.");
        ctx.Logger.Info("[Uninstall] Deleted app-owned native CLI");
        return Task.CompletedTask;
    }

    internal static string BuildInstallScript(string installUrl, string? requestedVersion)
    {
        if (requestedVersion?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Gateway version cannot contain newlines.");

        var url = PowerShellQuote(installUrl);
        var tagArgument = string.IsNullOrWhiteSpace(requestedVersion)
            ? ""
            : " -Tag " + PowerShellQuote(requestedVersion.Trim());
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $prefix = $env:{{CliPrefixEnvironmentVariable}}
            if ([string]::IsNullOrWhiteSpace($prefix)) {
                throw 'App-owned OpenClaw CLI prefix was not provided.'
            }
            New-Item -ItemType Directory -Force -Path $prefix | Out-Null
            $env:NPM_CONFIG_PREFIX = $prefix
            $env:npm_config_prefix = $prefix
            $env:Path = "$prefix;$env:Path"

            # The upstream installer currently removes a git-method wrapper and may
            # persist its npm prefix on PATH. Preserve those user-owned surfaces while
            # still reusing its supported Node/Git bootstrap and package install flow.
            $gitWrapper = Join-Path (Join-Path $env:USERPROFILE '.local\bin') 'openclaw.cmd'
            $gitWrapperExisted = Test-Path -LiteralPath $gitWrapper -PathType Leaf
            $gitWrapperBytes = if ($gitWrapperExisted) { [IO.File]::ReadAllBytes($gitWrapper) } else { $null }
            try {
                $content = (Invoke-WebRequest -UseBasicParsing -Uri {{url}}).Content
                $installer = if ($content -is [byte[]]) {
                    [Text.Encoding]::UTF8.GetString($content)
                } else {
                    [string]$content
                }
                & ([ScriptBlock]::Create($installer)) -NoOnboard{{tagArgument}}
                $powerShellCandidate = Join-Path $prefix 'openclaw.ps1'
                $cmdCandidate = Join-Path $prefix 'openclaw.cmd'
                if (Test-Path -LiteralPath $powerShellCandidate) { $cli = $powerShellCandidate }
                elseif (Test-Path -LiteralPath $cmdCandidate) { $cli = $cmdCandidate }
                if (-not $cli) { throw 'OpenClaw command launcher not found after install.' }
                Write-Output ('{{CliPathMarker}}' + $cli)
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            } finally {
                $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
                $filteredUserPath = (@($userPath -split ';') |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ine $prefix }) -join ';'
                [Environment]::SetEnvironmentVariable('Path', $filteredUserPath, 'User')
                if ($gitWrapperExisted) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $gitWrapper) | Out-Null
                    [IO.File]::WriteAllBytes($gitWrapper, $gitWrapperBytes)
                } elseif (Test-Path -LiteralPath $gitWrapper -PathType Leaf) {
                    Remove-Item -LiteralPath $gitWrapper -Force
                }
            }
            """;
    }

    internal static string EncodePowerShellScript(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    internal static bool MatchesRequestedVersion(string versionOutput, string requestedVersion)
    {
        var banner = versionOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (banner is null)
            return false;

        var actual = Regex.Match(
            banner,
            @"(?<![0-9A-Za-z])v?\d{4}\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?![0-9A-Za-z.-])",
            RegexOptions.IgnoreCase).Value;
        return actual.Length > 0
            && string.Equals(
                actual.TrimStart('v', 'V'),
                requestedVersion.Trim().TrimStart('v', 'V'),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExactVersionRequest(string? requestedVersion) =>
        !string.IsNullOrWhiteSpace(requestedVersion)
        && Regex.IsMatch(
            requestedVersion.Trim(),
            @"^v?\d{4}\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
            RegexOptions.IgnoreCase);

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string FirstUsefulLine(CommandResult result) =>
        $"{result.Stderr}\n{result.Stdout}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault() ?? "No diagnostic output was produced.";
}

public sealed class BeginNativeGatewayInstallStep : SetupStep
{
    public override string Id => "begin-native-install";
    public override string DisplayName => "Record native gateway setup intent";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.Config.InstallMode != GatewayInstallMode.NativeWindows)
            return StepResult.Skip("Native gateway setup intent is not needed for WSL");

        var markerPath = GatewayInstallModeDetector.GetNativeOwnershipPath(ctx.LocalDataDir);
        var existed = File.Exists(markerPath);
        var hasOwnershipMarker = existed
            || GatewayInstallModeDetector.HasNativeProfileOwnershipMarker(ctx.LocalDataDir);
        var profileIsOwned = GatewayInstallModeDetector.IsNativeProfileOwned(ctx.LocalDataDir, ctx.Config);
        if (hasOwnershipMarker && !profileIsOwned)
        {
            return StepResult.Terminal(
                "Native gateway ownership belongs to a different OpenClaw profile or Scheduled Task. " +
                "Use the matching Companion variant or remove that managed profile first.");
        }

        if (!hasOwnershipMarker)
        {
            var stateDirectory = GatewayCliRunner.GetManagedNativeStateDir(ctx.Config);
            if (Directory.Exists(stateDirectory))
            {
                return StepResult.Terminal(
                    $"The native OpenClaw profile '{GatewayCliRunner.GetManagedNativeProfile(ctx.Config)}' " +
                    "already exists without OpenClaw Companion ownership. Choose WSL or remove/rename that profile first.");
            }

            var taskInstalled = await NativeGatewayServiceCleanupStep.TryGetManagedTaskInstalledAsync(ctx, ct);
            if (taskInstalled == true)
            {
                return StepResult.Terminal(
                    $"The native OpenClaw Scheduled Task '{GatewayCliRunner.GetManagedNativeTaskName(ctx.Config)}' " +
                    "already exists without OpenClaw Companion ownership. Choose WSL or remove/rename that task first.");
            }
        }

        var profileMarkerPath = GatewayInstallModeDetector.GetNativeProfileOwnershipPath(ctx.LocalDataDir);
        ctx.PreviousNativeOwnershipMarker = new NativeOwnershipMarkerRollbackState(
            await CaptureMarkerAsync(markerPath, ct),
            await CaptureMarkerAsync(profileMarkerPath, ct));

        await ConfigureGatewayStep.WriteNativeOwnershipMarkerAsync(
            ctx,
            ct,
            includeCurrentConfigPaths: false);
        return StepResult.Ok("Native gateway setup intent recorded");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.PreviousNativeOwnershipMarker is not { } previous)
            return;

        await RestoreMarkerAsync(previous.Active, ct);
        if (previous.Profile.Existed)
        {
            await RestoreMarkerAsync(previous.Profile, ct);
        }
        else if (Directory.Exists(GatewayCliRunner.GetManagedNativeStateDir(ctx.Config))
                 && GatewayInstallModeDetector.IsNativeProfileOwnershipMarkerOwned(ctx.LocalDataDir, ctx.Config))
        {
            ctx.Logger.Info("Preserved native profile ownership after rollback because managed profile state remains");
        }
        else
        {
            await RestoreMarkerAsync(previous.Profile, ct);
        }

        ctx.PreviousNativeOwnershipMarker = null;
    }

    private static async Task RestoreMarkerAsync(
        NativeOwnershipMarkerFileRollbackState marker,
        CancellationToken ct)
    {
        if (marker.Existed)
        {
            await AtomicFile.WriteAllBytesAsync(marker.Path, marker.Contents!, ct);
        }
        else if (File.Exists(marker.Path))
        {
            File.Delete(marker.Path);
        }
    }

    private static async Task<NativeOwnershipMarkerFileRollbackState> CaptureMarkerAsync(
        string path,
        CancellationToken ct)
    {
        var existed = File.Exists(path);
        return new NativeOwnershipMarkerFileRollbackState(
            path,
            existed,
            existed ? await File.ReadAllBytesAsync(path, ct) : null);
    }
}

public sealed class StopConflictingLocalGatewaysStep : SetupStep
{
    public override string Id => "stop-conflicting-gateways";
    public override string DisplayName => "Stop conflicting local gateway";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var canManageNativeGateway = ctx.Config.InstallMode == GatewayInstallMode.NativeWindows
            || GatewayInstallModeDetector.HasManagedNativeInstallation(
                ctx.DataDir,
                ctx.LocalDataDir,
                ctx.Config);
        var nativeCli = GatewayCliRunner.TryResolveNativeCliPath(ctx.LocalDataDir);
        if (canManageNativeGateway)
        {
            if (nativeCli is not null)
                ctx.NativeCliPath ??= nativeCli;
            await CaptureNativeRollbackStateAsync(ctx, ct);
        }

        if (!ctx.Config.CleanBeforeRun)
            return StepResult.Skip("Existing gateway state captured; conflicting-gateway cleanup is disabled");

        if (canManageNativeGateway)
        {
            if (nativeCli is not null)
            {
                var stop = await GatewayCliRunner.RunNativeAsync(ctx, ["gateway", "stop"], TimeSpan.FromSeconds(30), ct: ct);
                ctx.Logger.Info(stop.ExitCode == 0
                    ? "Stopped existing native Windows gateway"
                    : "No running native Windows gateway needed stopping");
            }
            else
            {
                ctx.Logger.Warn("Managed native gateway CLI is unavailable; setup will verify its Scheduled Task before switching modes");
            }

            // The official Windows installer refreshes every loaded gateway service.
            // Remove the captured app-managed task before upgrading the CLI so rollback
            // remains anchored to the pre-installer config and service state.
            if (ctx.Config.InstallMode == GatewayInstallMode.NativeWindows
                && nativeCli is not null
                && ctx.PreviousNativeGateway is { ServiceInstalled: true })
            {
                var uninstall = await GatewayCliRunner.RunNativeAsync(
                    ctx,
                    ["gateway", "uninstall"],
                    TimeSpan.FromSeconds(30),
                    ct: ct);
                if (uninstall.ExitCode != 0 && !InstallGatewayServiceStep.IsMissingWslService(uninstall))
                {
                    return StepResult.Fail(
                        $"Could not prepare the existing native gateway for upgrade (exit {uninstall.ExitCode}).");
                }

                ctx.Logger.Info("Removed existing native gateway service before CLI upgrade");
            }
        }
        else if (!canManageNativeGateway && nativeCli is not null)
        {
            ctx.Logger.Info("Leaving an unmanaged native Windows gateway untouched");
        }

        if (ctx.Config.InstallMode == GatewayInstallMode.NativeWindows)
        {
            await StopAppOwnedWslGatewayAsync(ctx, ct);
            await PreflightPortStep.WaitForPortFreeAsync(
                ctx.Config.GatewayPort,
                ctx.Config.Gateway.Bind,
                ctx.Logger,
                ct,
                maxWaitSeconds: 10);
        }
        return StepResult.Ok("Conflicting local gateways stopped");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.PreviousNativeGateway is { } previousNative)
        {
            if (previousNative.ConfigExisted)
            {
                await AtomicFile.WriteAllBytesAsync(
                    previousNative.ConfigPath,
                    previousNative.ConfigContents!,
                    ct);
            }
            else if (File.Exists(previousNative.ConfigPath))
            {
                File.Delete(previousNative.ConfigPath);
            }

            if (previousNative.OwnershipMarkerPath is { } ownershipPath)
            {
                if (previousNative.OwnershipMarkerExisted)
                {
                    await AtomicFile.WriteAllBytesAsync(
                        ownershipPath,
                        previousNative.OwnershipMarkerContents!,
                        ct);
                }
                else if (File.Exists(ownershipPath))
                {
                    File.Delete(ownershipPath);
                }
            }

            if (previousNative.ServiceInstalled)
            {
                var install = await GatewayCliRunner.RunNativeAsync(
                    ctx,
                    ["gateway", "install", "--force"],
                    TimeSpan.FromSeconds(60),
                    ct: ct);
                if (install.ExitCode != 0)
                    throw new InvalidOperationException("Could not restore the previous native gateway service.");

                var restoreRuntime = await GatewayCliRunner.RunNativeAsync(
                    ctx,
                    ["gateway", previousNative.WasRunning ? "start" : "stop"],
                    TimeSpan.FromSeconds(30),
                    ct: ct);
                if (restoreRuntime.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        previousNative.WasRunning
                            ? "Restored the previous native gateway service but could not start it."
                            : "Restored the previous native gateway service but could not keep it stopped.");
                }
            }

            ctx.PreviousNativeGateway = null;
        }

        if (ctx.PreviousWslGateway is { } previousWsl)
        {
            if (previousWsl.WasRunning)
            {
                var start = await ctx.Commands.RunInWslAsync(
                    previousWsl.DistroName,
                    $"{ctx.WslPathPrefix} && openclaw gateway start",
                    TimeSpan.FromSeconds(30),
                    ct: ct);
                if (start.ExitCode != 0)
                    throw new InvalidOperationException("Could not restart the previous WSL gateway after native setup failed.");
            }

            if (previousWsl.HadManagedKeepalive)
            {
                var keepalive = await new StartKeepaliveStep().ExecuteAsync(ctx, ct);
                if (!keepalive.IsSuccess)
                    throw new InvalidOperationException("Could not restore the previous WSL keepalive after native setup failed.");
            }
        }

        ctx.PreviousWslGateway = null;
    }

    private static async Task CaptureNativeRollbackStateAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.PreviousNativeGateway is not null)
            return;

        var configPath = GatewayCliRunner.GetManagedNativeConfigPath(ctx.Config);
        var configExisted = File.Exists(configPath);
        var configContents = configExisted
            ? await File.ReadAllBytesAsync(configPath, ct)
            : null;
        var ownershipMarkerPath = GatewayInstallModeDetector.GetNativeOwnershipPath(ctx.LocalDataDir);
        var ownershipMarkerExisted = File.Exists(ownershipMarkerPath);
        var ownershipMarkerContents = ownershipMarkerExisted
            ? await File.ReadAllBytesAsync(ownershipMarkerPath, ct)
            : null;

        var status = await GatewayCliRunner.RunNativeAsync(
            ctx,
            ["gateway", "status", "--json"],
            TimeSpan.FromSeconds(30),
            ct: ct);
        var serviceInstalled = await IsNativeServiceInstalledAsync(ctx, status, ct);
        var wasRunning = serviceInstalled
            && ((status.ExitCode == 0 && NativeGatewayServiceCleanupStep.IsServiceRunning(status.Stdout))
                || await IsGatewayReachableAsync(ctx.Config.GatewayPort, ct));

        ctx.PreviousNativeGateway = new NativeGatewayRollbackState(
            configPath,
            configExisted,
            configContents,
            serviceInstalled,
            wasRunning,
            ownershipMarkerPath,
            ownershipMarkerExisted,
            ownershipMarkerContents);
    }

    private static async Task<bool> IsNativeServiceInstalledAsync(
        SetupContext ctx,
        CommandResult status,
        CancellationToken ct)
    {
        if (status.ExitCode == 0
            && NativeGatewayServiceCleanupStep.TryGetServiceInstalled(status.Stdout, out var installed))
        {
            return installed;
        }

        return await NativeGatewayServiceCleanupStep.TryGetManagedTaskInstalledAsync(ctx, ct) == true
            || NativeGatewayServiceCleanupStep.HasManagedServiceFiles(ctx.Config);
    }

    private static async Task<bool> IsGatewayReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync(StartGatewayStep.GetNativeHealthUri(port), ct);
            return StartGatewayStep.IsHealthyHttpStatus(response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task StopAppOwnedWslGatewayAsync(SetupContext ctx, CancellationToken ct)
    {
        var list = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (!WslInstallSupport.ContainsDistro(list.Stdout, ctx.DistroName!))
            return;

        var status = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            $"{ctx.WslPathPrefix} && openclaw gateway status --json",
            TimeSpan.FromSeconds(30),
            ct: ct);
        var activeMode = GatewayInstallModeDetector.Detect(ctx.DataDir, GatewayInstallMode.NativeWindows);
        var wasRunning = (status.ExitCode == 0 && NativeGatewayServiceCleanupStep.IsServiceRunning(status.Stdout))
            || (activeMode == GatewayInstallMode.Wsl
                && await IsGatewayReachableAsync(ctx.Config.GatewayPort, ct));
        var keepaliveMarker = StartKeepaliveStep.GetKeepaliveMarkerPath(ctx);
        ctx.PreviousWslGateway = new WslGatewayRollbackState(
            ctx.DistroName!,
            wasRunning,
            HadManagedKeepalive: File.Exists(keepaliveMarker));

        await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            $"{ctx.WslPathPrefix} && openclaw gateway stop || true",
            TimeSpan.FromSeconds(30),
            ct: ct);
        await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--terminate", ctx.DistroName!],
            TimeSpan.FromSeconds(30),
            ct: ct);
        ctx.Logger.Info($"Stopped existing WSL gateway '{ctx.DistroName}' without deleting its distro");
    }
}

public sealed class NativeGatewayServiceCleanupStep : SetupStep
{
    public override string Id => "native-service-cleanup";
    public override string DisplayName => "Remove conflicting native gateway service";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!ctx.Config.CleanBeforeRun)
            return StepResult.Skip("Conflicting native gateway cleanup is disabled");

        if (ctx.Config.InstallMode == GatewayInstallMode.NativeWindows)
        {
            if (ctx.PreviousNativeGateway is not { ServiceInstalled: true })
                return StepResult.Ok();

            // If the old launcher was missing, the preceding installer repairs it
            // first. Remove the captured service now, before port preflight, while
            // keeping the old task intact if installer repair itself fails.
            var repairedCli = ctx.NativeCliPath ?? GatewayCliRunner.TryResolveNativeCliPath(ctx.LocalDataDir);
            if (repairedCli is null)
                return StepResult.Fail("The repaired native gateway CLI could not be located for service cleanup.");

            ctx.NativeCliPath ??= repairedCli;
            var uninstall = await GatewayCliRunner.RunNativeAsync(
                ctx,
                ["gateway", "uninstall"],
                TimeSpan.FromSeconds(30),
                ct: ct);
            var taskInstalled = await TryGetManagedTaskInstalledAsync(ctx, ct);
            if ((uninstall.ExitCode != 0 && !InstallGatewayServiceStep.IsMissingWslService(uninstall))
                || taskInstalled != false)
            {
                try
                {
                    await RemoveManagedServiceWithoutCliAsync(ctx, ct);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    return StepResult.Fail($"Could not remove the previous native gateway service: {ex.Message}");
                }
            }

            return StepResult.Ok("Previous native gateway service removed before reconfiguration");
        }

        // When switching to WSL, remove the native Scheduled Task so it cannot
        // reclaim the port after a later sign-in. The preceding stop step owns
        // the captured state and restores it if the pipeline rolls back.
        var nativeCli = ctx.NativeCliPath ?? GatewayCliRunner.TryResolveNativeCliPath(ctx.LocalDataDir);
        if (nativeCli is null)
        {
            if (!GatewayInstallModeDetector.HasManagedNativeInstallation(
                    ctx.DataDir,
                    ctx.LocalDataDir,
                    ctx.Config))
                return StepResult.Skip("No native OpenClaw installation found");

            var taskInstalled = await TryGetManagedTaskInstalledAsync(ctx, ct);
            var fallbackFilesExist = HasManagedServiceFiles(ctx.Config);
            if (taskInstalled != false || fallbackFilesExist)
            {
                return StepResult.Fail(
                    "The managed native gateway CLI is unavailable and its service could not be verified as fully absent. " +
                    "Repair OpenClaw before switching to WSL so its Scheduled Task and Startup fallback can be removed safely.");
            }

            if (ctx.PreviousNativeGateway is not { } capturedNative)
                return StepResult.Fail("Could not capture the managed native gateway before switching to WSL.");

            var interruptedOwnershipPath = GatewayInstallModeDetector.GetNativeOwnershipPath(ctx.LocalDataDir);
            if (File.Exists(interruptedOwnershipPath))
                File.Delete(interruptedOwnershipPath);
            return StepResult.Ok("Inactive native gateway service is absent; preserved its profile configuration");
        }

        ctx.NativeCliPath ??= nativeCli;
        if (ctx.PreviousNativeGateway is not { } previousNative)
            return StepResult.Skip("No managed native gateway found");

        if (previousNative.ServiceInstalled)
        {
            var result = await GatewayCliRunner.RunNativeAsync(
                ctx,
                ["gateway", "uninstall"],
                TimeSpan.FromSeconds(30),
                ct: ct);
            if (result.ExitCode != 0 && !InstallGatewayServiceStep.IsMissingWslService(result))
                return StepResult.Fail($"Could not remove the native gateway service (exit {result.ExitCode}).");
        }

        var configPath = GatewayCliRunner.GetManagedNativeConfigPath(ctx.Config);
        var markerExists = GatewayInstallModeDetector.HasNativeOwnershipMarker(ctx.LocalDataDir);
        var managedPaths = markerExists
            ? ConfigureGatewayStep.ReadNativeManagedConfigPaths(ctx.LocalDataDir, ctx.Config)
            : ConfigureGatewayStep.NativeManagedConfigPaths;
        if (!await ConfigureGatewayStep.TryRemoveNativeManagedConfigWithCliAsync(ctx, managedPaths, ct))
        {
            return StepResult.Fail(
                File.Exists(configPath)
                    ? "Could not remove setup-managed native settings without risking other profile configuration."
                    : "Native profile ownership could not be verified for fallback configuration cleanup.");
        }

        var ownershipPath = GatewayInstallModeDetector.GetNativeOwnershipPath(ctx.LocalDataDir);
        if (File.Exists(ownershipPath))
            File.Delete(ownershipPath);

        return StepResult.Ok("Native gateway service and setup-managed configuration removed");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) => Task.CompletedTask;

    internal static async Task<bool?> TryGetManagedTaskInstalledAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var schtasks = string.IsNullOrWhiteSpace(windowsDirectory)
            ? "schtasks.exe"
            : Path.Combine(windowsDirectory, "System32", "schtasks.exe");
        var query = await ctx.Commands.RunAsync(
            schtasks,
            ["/Query", "/TN", GatewayCliRunner.GetManagedNativeTaskName(ctx.Config)],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (query.ExitCode == 0)
            return true;

        var output = $"{query.Stderr}\n{query.Stdout}";
        return output.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase)
            || output.Contains("system cannot find", StringComparison.OrdinalIgnoreCase)
            || output.Contains("task name", StringComparison.OrdinalIgnoreCase)
                && output.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            ? false
            : null;
    }

    internal static async Task RemoveManagedServiceWithoutCliAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        if (!GatewayInstallModeDetector.IsNativeProfileOwned(ctx.LocalDataDir, ctx.Config))
        {
            throw new InvalidOperationException(
                "Native profile ownership could not be verified for direct service cleanup.");
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var schtasks = string.IsNullOrWhiteSpace(windowsDirectory)
            ? "schtasks.exe"
            : Path.Combine(windowsDirectory, "System32", "schtasks.exe");
        var taskName = GatewayCliRunner.GetManagedNativeTaskName(ctx.Config);
        if (await TryGetManagedTaskInstalledAsync(ctx, ct) == true)
        {
            await ctx.Commands.RunAsync(
                schtasks,
                ["/End", "/TN", taskName],
                TimeSpan.FromSeconds(15),
                ct: ct);
            var delete = await ctx.Commands.RunAsync(
                schtasks,
                ["/Delete", "/TN", taskName, "/F"],
                TimeSpan.FromSeconds(15),
                ct: ct);
            if (delete.ExitCode != 0 && await TryGetManagedTaskInstalledAsync(ctx, ct) == true)
                throw new InvalidOperationException($"Could not remove native gateway Scheduled Task '{taskName}'.");
        }

        foreach (var path in GetManagedServiceFiles(ctx.Config))
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        const string portVariable = "OPENCLAW_SETUP_GATEWAY_PORT";
        var verifyStoppedScript = $$"""
            $ErrorActionPreference = 'Stop'
            $port = [int]$env:{{portVariable}}
            for ($attempt = 1; $attempt -le 10; $attempt++) {
                $matching = @()
                $connections = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
                foreach ($connection in $connections) {
                    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($connection.OwningProcess)" -ErrorAction SilentlyContinue
                    if ($null -ne $process -and [string]$process.CommandLine -match '(?i)(openclaw.*gateway|gateway.*openclaw)') {
                        $matching += $process
                    }
                }
                if ($matching.Count -eq 0) {
                    exit 0
                }
                Start-Sleep -Milliseconds 250
            }
            throw "An OpenClaw gateway is still listening on port $port, but process ownership cannot be proven. It was left untouched."
            """;
        var verifyStopped = await ctx.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", InstallNativeCliStep.EncodePowerShellScript(verifyStoppedScript)],
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string>
            {
                [portVariable] = ctx.Config.GatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct: ct);
        if (verifyStopped.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not verify native gateway runtime shutdown without risking an unrelated OpenClaw process.");
        }

        if (await TryGetManagedTaskInstalledAsync(ctx, ct) == true
            || GetManagedServiceFiles(ctx.Config).Any(File.Exists))
        {
            throw new InvalidOperationException("Native gateway service artifacts remain after direct cleanup.");
        }
    }

    internal static IReadOnlyList<string> GetManagedServiceFiles(SetupConfig config)
    {
        var taskName = GatewayCliRunner.GetManagedNativeTaskName(config);
        var safeTaskName = System.Text.RegularExpressions.Regex.Replace(taskName, "[<>:\"/\\\\|?*\\x00-\\x1F]", "_");
        var startupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        var stateDirectory = GatewayCliRunner.GetManagedNativeStateDir(config);
        return
        [
            Path.Combine(startupDirectory, safeTaskName + ".cmd"),
            Path.Combine(startupDirectory, safeTaskName + ".vbs"),
            Path.Combine(stateDirectory, "gateway.cmd"),
            Path.Combine(stateDirectory, "gateway.vbs"),
        ];
    }

    internal static bool HasManagedServiceFiles(SetupConfig config) =>
        GetManagedServiceFiles(config).Any(File.Exists);

    internal static bool IsServiceInstalled(string statusJson) =>
        TryGetServiceInstalled(statusJson, out var installed) && installed;

    internal static bool TryGetServiceInstalled(string statusJson, out bool installed)
    {
        installed = false;
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (!document.RootElement.TryGetProperty("service", out var service))
                return false;

            installed = (service.TryGetProperty("loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True)
                || (service.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsServiceRunning(string statusJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            return document.RootElement.TryGetProperty("service", out var service)
                && service.TryGetProperty("runtime", out var runtime)
                && runtime.ValueKind == JsonValueKind.Object
                && runtime.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "running", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
