<#
.SYNOPSIS
    Validate the OpenClaw WSL gateway local-setup product code path end-to-end.

.DESCRIPTION
    Phase 6 clean port. Drives the WinUI3 tray app from launch through the
    forked onboarding (SetupWarningPage -> "Set up locally" -> LocalSetupProgressPage)
    so the *product* code path that runs

        wsl --install Ubuntu-24.04 --name OpenClawGateway --location <path> --no-launch --version 2

    is exercised end-to-end. The script does NOT install WSL itself and does NOT
    invoke `wsl --install` directly: it expects the tray engine to do that and
    only verifies the postcondition.

    Networking diagnostics are loopback-only. There is no WSL-IP / lan / auto
    fallback. Token / setup-code / private-key material is redacted in artifacts.

.PARAMETER Scenario
    PreflightOnly  - Repo layout + WSL host status + relay probe (safe; no install).
    UpstreamInstall - Build/test, drive tray onboarding to install OpenClawGateway,
                      run smoke + pairing proofs. Reuses an existing distro if present.
    FreshMachine    - Like UpstreamInstall, but unregisters any existing
                      OpenClawGateway distro first (simulates a clean machine).
    Recreate        - Iterated FreshMachine (unregister between runs). Use `-Iterations`.

.NOTES
    Diagnostics on networking/lifecycle health failures point operators at
    https://aka.ms/wsllogs (per Craig).

    File I/O against WSL is via `wsl bash -c` only. NEVER \\wsl$ / \\wsl.localhost.
#>
[CmdletBinding()]
param(
    [ValidateSet("PreflightOnly", "UpstreamInstall", "FreshMachine", "Recreate")]
    [string]$Scenario = "PreflightOnly",
    [string]$OutputDir = (Join-Path (Get-Location) "artifacts\wsl-gateway-validation"),
    [int]$Iterations = 1,
    [switch]$ConfirmDestructiveClean,
    [switch]$KeepFailedDistro,
    [bool]$CleanupAfterSuccess = $true,
    [switch]$ContinueOnCleanupFailure,
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 600,
    [string]$DistroName = "OpenClawGateway",
    [string]$GatewayUrl = "ws://127.0.0.1:18789",
    [string]$RelayProbeUri,
    [switch]$RequireRelayProbe,
    [switch]$RequireRealGatewayBootstrap,
    [switch]$RequireOperatorPairing,
    [switch]$RequireWindowsNodePairing,
    [switch]$ContinueOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $OutputDir $runStamp
$commandsRoot = Join-Path $runRoot "commands"
$screenshotsRoot = Join-Path $runRoot "screenshots"
$summaryPath = Join-Path $runRoot "summary.json"
$summaryMarkdownPath = Join-Path $runRoot "summary.md"
$trayProject = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
$runtimeIdentifier = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
$trayExe = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\$runtimeIdentifier\OpenClaw.Tray.WinUI.exe"
$cliProject = Join-Path $repoRoot "src\OpenClaw.Cli\OpenClaw.Cli.csproj"

# Always isolate AppData under run root for non-Preflight scenarios so we never
# trample the operator's real Windows tray identity.
$validationAppDataRoot = if ($Scenario -eq "PreflightOnly") { $env:APPDATA } else { Join-Path $runRoot "isolated\appdata" }
$validationLocalAppDataRoot = if ($Scenario -eq "PreflightOnly") { $env:LOCALAPPDATA } else { Join-Path $runRoot "isolated\localappdata" }
$setupStatePath = Join-Path $validationLocalAppDataRoot "OpenClawTray\setup-state.json"
$settingsPath = Join-Path $validationAppDataRoot "settings.json"
$wslInstallLocation = Join-Path $runRoot "wsl\$DistroName"

$script:summary = [ordered]@{
    script = "validate-wsl-gateway"
    scenario = $Scenario
    startedAt = (Get-Date).ToString("o")
    finishedAt = $null
    status = "Running"
    validationStatus = "Running"
    cleanupStatus = "NotStarted"
    repository = $repoRoot.Path
    outputDir = $runRoot
    networkingMode = "LocalhostOnly"
    activeDistroName = $DistroName
    activeInstallLocation = $wslInstallLocation
    selectedGatewayUrl = $GatewayUrl
    pairingValidation = [ordered]@{
        gatewayImplementation = "Unknown"
        bootstrapQrShape = "Unknown"
        realUpstreamBootstrapHandoff = $false
        operatorPaired = $false
        windowsNodePaired = $false
    }
    setupPhases = @()
    iterations = @()
    steps = @()
    error = $null
}

function Add-Step {
    param([string]$Name, [string]$Status, [string]$Message, [hashtable]$Data = @{})
    $script:summary.steps += [ordered]@{
        name = $Name
        status = $Status
        message = $Message
        data = $Data
        timestamp = (Get-Date).ToString("o")
    }
}

function Test-IsOpenClawOwnedDistroName {
    param([string]$Name)
    return $Name -eq "OpenClawGateway" -or $Name.StartsWith("OpenClawGateway", [System.StringComparison]::Ordinal)
}

function Assert-DestructiveSafety {
    if ($Scenario -in @("FreshMachine", "Recreate") -and -not $ConfirmDestructiveClean) {
        throw "-ConfirmDestructiveClean is required when -Scenario is $Scenario (will unregister WSL distro '$DistroName')."
    }
    if ($Scenario -in @("FreshMachine", "Recreate") -and -not (Test-IsOpenClawOwnedDistroName -Name $DistroName)) {
        throw "Refusing destructive action for non-OpenClaw distro '$DistroName'. Distro name must start with 'OpenClawGateway'."
    }
}

function Get-SafeUriDisplay {
    param([string]$Uri)
    try {
        $b = [System.UriBuilder]::new($Uri)
        $b.Query = $null; $b.Fragment = $null
        return $b.Uri.AbsoluteUri
    } catch {
        return "<invalid-uri>"
    }
}

function Write-Summary {
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $script:summary.finishedAt = (Get-Date).ToString("o")
    $script:summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $lines = @(
        "# OpenClaw WSL gateway validation",
        "",
        "- Scenario: $Scenario",
        "- Status: $($script:summary.status)",
        "- Validation: $($script:summary.validationStatus)",
        "- Cleanup: $($script:summary.cleanupStatus)",
        "- Networking mode: LocalhostOnly (loopback only)",
        "- Started: $($script:summary.startedAt)",
        "- Finished: $($script:summary.finishedAt)",
        "- Output: $runRoot",
        "",
        "## Steps"
    )
    foreach ($step in $script:summary.steps) {
        $lines += "- $($step.status): $($step.name) - $($step.message)"
    }
    if ($script:summary.error) {
        $lines += "", "## Error", $script:summary.error
        $lines += "", "Diagnostics: see https://aka.ms/wsllogs for WSL networking/lifecycle logs."
    }
    $lines | Set-Content -LiteralPath $summaryMarkdownPath -Encoding UTF8
}

function Redact-SensitiveGatewayOutput {
    param([string]$Content)
    if ([string]::IsNullOrEmpty($Content)) { return $Content }
    $r = $Content -replace '("(?:bootstrapToken|bootstrap_token|deviceToken|device_token|token|setupCode|setup_code|PrivateKeyBase64|PublicKeyBase64)"\s*:\s*")[^"]+(")', '$1<redacted>$2'
    $r = $r -replace '(?i)((?:bootstrap|device|gateway|auth)[_-]?token\s*[:=]\s*)[^\s,"''}]+', '$1<redacted>'
    return $r
}

function Read-TextFileWithRetry {
    param([string]$Path, [int]$Attempts = 10, [int]$DelayMilliseconds = 200)
    for ($i = 1; $i -le $Attempts; $i++) {
        try { return Get-Content -LiteralPath $Path -Raw -ErrorAction Stop }
        catch [System.IO.IOException] { if ($i -eq $Attempts) { throw } ; Start-Sleep -Milliseconds $DelayMilliseconds }
    }
}

function Write-TextFileWithRetry {
    param([string]$Path, [string]$Content, [int]$Attempts = 10, [int]$DelayMilliseconds = 200)
    for ($i = 1; $i -le $Attempts; $i++) {
        try { $Content | Set-Content -LiteralPath $Path -Encoding UTF8 -ErrorAction Stop ; return }
        catch [System.IO.IOException] { if ($i -eq $Attempts) { throw } ; Start-Sleep -Milliseconds $DelayMilliseconds }
    }
}

function Copy-RedactedFileIfExists {
    param([string]$SourcePath, [string]$DestinationPath)
    if (-not (Test-Path -LiteralPath $SourcePath)) { return $false }
    $content = Read-TextFileWithRetry -Path $SourcePath
    Write-TextFileWithRetry -Path $DestinationPath -Content (Redact-SensitiveGatewayOutput $content)
    return $true
}

function Invoke-LoggedProcess {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = $repoRoot.Path,
        [hashtable]$Environment = @{},
        [switch]$IgnoreExitCode,
        [switch]$SensitiveOutput
    )

    New-Item -ItemType Directory -Force -Path $commandsRoot | Out-Null
    $safe = $Name -replace "[^a-zA-Z0-9_.-]", "-"
    $stdout = Join-Path $commandsRoot "$safe.stdout.txt"
    $stderr = Join-Path $commandsRoot "$safe.stderr.txt"
    $saved = @{}
    foreach ($k in $Environment.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
        [Environment]::SetEnvironmentVariable($k, [string]$Environment[$k], "Process")
    }
    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList > $stdout 2> $stderr
        $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    } finally {
        Pop-Location
        foreach ($k in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($k, $saved[$k], "Process")
        }
    }

    if ($SensitiveOutput) {
        foreach ($p in @($stdout, $stderr)) {
            if (Test-Path -LiteralPath $p) {
                $c = Read-TextFileWithRetry -Path $p -Attempts 20 -DelayMilliseconds 250
                Write-TextFileWithRetry -Path $p -Content (Redact-SensitiveGatewayOutput $c) -Attempts 20 -DelayMilliseconds 250
            }
        }
    }

    Add-Step $Name "Completed" "Command completed with exit code $exitCode." @{
        file = $FilePath; arguments = ($ArgumentList -join " "); exitCode = $exitCode; stdout = $stdout; stderr = $stderr
    }

    if ($exitCode -ne 0 -and -not $IgnoreExitCode) {
        throw "$Name failed with exit code $exitCode. See $stdout and $stderr."
    }
}

function Invoke-LoggedPowerShellScript {
    param([string]$Name, [string]$ScriptPath, [string[]]$ArgumentList = @())
    $hostExe = if ($PSHOME -and (Test-Path (Join-Path $PSHOME "pwsh.exe"))) { Join-Path $PSHOME "pwsh.exe" } else { "powershell.exe" }
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ScriptPath) + $ArgumentList
    Invoke-LoggedProcess -Name $Name -FilePath $hostExe -ArgumentList $args
}

function Invoke-RepositoryValidation {
    if ($NoBuild) {
        Add-Step "repository-validation" "Skipped" "Skipped build and tests because -NoBuild was set."
        return
    }
    Invoke-LoggedPowerShellScript "build" (Join-Path $repoRoot "build.ps1")
    Invoke-LoggedProcess "test-shared" "dotnet" @("test", ".\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj", "--no-restore")
    Invoke-LoggedProcess "test-tray"   "dotnet" @("test", ".\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj", "--no-restore")
}

function Invoke-Preflight {
    Invoke-LoggedProcess "dotnet-info"     "dotnet"  @("--info") -IgnoreExitCode
    Invoke-LoggedProcess "wsl-status"      "wsl.exe" @("--status") -IgnoreExitCode
    Invoke-LoggedProcess "wsl-list-before" "wsl.exe" @("--list", "--verbose") -IgnoreExitCode

    if (-not (Test-Path -LiteralPath $trayProject)) { throw "Tray project not found: $trayProject" }
    if (-not (Test-Path -LiteralPath $cliProject))  { throw "CLI project not found: $cliProject" }
    Add-Step "repo-layout" "Passed" "Required projects are present."

    Invoke-RelayPrototypeProbe
}

function Invoke-RelayPrototypeProbe {
    $probeUri = if (-not [string]::IsNullOrWhiteSpace($RelayProbeUri)) { $RelayProbeUri } else { [Environment]::GetEnvironmentVariable("OPENCLAW_RELAY_PROBE_URI", "Process") }
    if ([string]::IsNullOrWhiteSpace($probeUri)) {
        $msg = "No relay probe endpoint was supplied. Set -RelayProbeUri or OPENCLAW_RELAY_PROBE_URI."
        if ($RequireRelayProbe) { throw "RelayProbeMissing: $msg" }
        Add-Step "relay-prototype-probe" "NotAvailable" $msg
        return
    }
    $relayPath = Join-Path $commandsRoot "relay-prototype-probe.txt"
    New-Item -ItemType Directory -Force -Path $commandsRoot | Out-Null
    try {
        $r = Invoke-WebRequest -Uri $probeUri -TimeoutSec 15 -UseBasicParsing
        $body = if ($null -ne $r.Content) { $r.Content } else { "" }
        $body = $body -replace '(?i)(token=)[^&\s]+', '$1<redacted>'
        $body | Set-Content -LiteralPath $relayPath -Encoding UTF8
        Add-Step "relay-prototype-probe" "Passed" "Relay probe endpoint responded." @{
            uri = (Get-SafeUriDisplay $probeUri); statusCode = [int]$r.StatusCode; path = $relayPath
        }
    } catch {
        throw "RelayProbeFailed: relay probe failed for $(Get-SafeUriDisplay $probeUri): $($_.Exception.Message)"
    }
}

function Get-LatestScreenshotPath {
    if (-not (Test-Path -LiteralPath $screenshotsRoot)) { return $null }
    $latest = Get-ChildItem -LiteralPath $screenshotsRoot -Filter "*.png" -File -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) { return $null }
    return $latest.FullName
}

function Save-DiagnosticsSnapshot {
    param([string]$Reason)
    $diag = Join-Path $runRoot "diagnostics"
    New-Item -ItemType Directory -Force -Path $diag | Out-Null

    if (Test-Path -LiteralPath $setupStatePath) {
        Copy-RedactedFileIfExists -SourcePath $setupStatePath -DestinationPath (Join-Path $diag "setup-state.redacted.json") | Out-Null
    }
    if (Test-Path -LiteralPath $settingsPath) {
        Copy-RedactedFileIfExists -SourcePath $settingsPath -DestinationPath (Join-Path $diag "settings.redacted.json") | Out-Null
    }
    $identityPath = Join-Path $validationAppDataRoot "OpenClawTray\device-key-ed25519.json"
    if (Test-Path -LiteralPath $identityPath) {
        Copy-RedactedFileIfExists -SourcePath $identityPath -DestinationPath (Join-Path $diag "device-key.shape.redacted.json") | Out-Null
    }

    Add-Step "diagnostics-snapshot" "Completed" "Saved diagnostics snapshot for $Reason. See https://aka.ms/wsllogs for WSL networking/lifecycle logs." @{
        path = $diag
        latestScreenshot = (Get-LatestScreenshotPath)
        wslLogsHelp = "https://aka.ms/wsllogs"
    }
}

function Get-ValidationAppEnvironment {
    return @{
        OPENCLAW_TRAY_DATA_DIR = $validationAppDataRoot
        OPENCLAW_TRAY_APPDATA_DIR = $validationAppDataRoot
        OPENCLAW_TRAY_LOCALAPPDATA_DIR = $validationLocalAppDataRoot
    }
}

function Convert-SetupStatus {
    param([object]$Status)
    $v = [string]$Status
    if ($v -match '^\d+$') {
        # Aligned with LocalGatewaySetupStatus enum
        $names = @("Pending", "Running", "RequiresAdmin", "RequiresRestart", "Blocked",
                   "FailedRetryable", "FailedTerminal", "Complete", "Cancelled")
        $i = [int]$v
        if ($i -ge 0 -and $i -lt $names.Count) { return $names[$i] }
    }
    return $v
}

function Convert-SetupPhase {
    param([object]$Phase)
    $v = [string]$Phase
    if ($v -match '^\d+$') {
        # Aligned with the clean LocalGatewaySetupPhase enum (worker / rootfs phases removed).
        $names = @(
            "NotStarted", "Preflight", "ElevationCheck",
            "EnsureWslEnabled", "CreateWslInstance", "ConfigureWslInstance",
            "InstallOpenClawCli", "PrepareGatewayConfig", "InstallGatewayService",
            "StartGateway", "WaitForGateway",
            "MintBootstrapToken", "PairOperator",
            "CheckWindowsNodeReadiness", "PairWindowsTrayNode",
            "VerifyEndToEnd", "Complete", "Failed", "Cancelled"
        )
        $i = [int]$v
        if ($i -ge 0 -and $i -lt $names.Count) { return $names[$i] }
    }
    return $v
}

function Wait-ForUiAutomationElement {
    param([string]$AutomationId, [int]$TimeoutSeconds)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    while ((Get-Date) -lt $deadline) {
        $el = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Invoke-UiAutomationClick {
    param([string]$AutomationId, [int]$TimeoutSeconds)
    $el = Wait-ForUiAutomationElement -AutomationId $AutomationId -TimeoutSeconds $TimeoutSeconds
    if ($null -ne $el) {
        $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $p.Invoke()
        Add-Step "ui-click-$AutomationId" "Completed" "Clicked UI element with AutomationId '$AutomationId'."
        return
    }
    Save-DiagnosticsSnapshot -Reason "missing-ui-target-$AutomationId"
    throw "UI element with AutomationId '$AutomationId' was not found within $TimeoutSeconds seconds."
}

function Stop-ExistingTrayProcesses {
    param([string]$Reason)
    $repoPrefix = [string]$repoRoot.Path
    $procs = Get-Process -Name "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue |
        Where-Object {
            try { -not [string]::IsNullOrWhiteSpace($_.Path) -and $_.Path.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase) }
            catch { $false }
        }
    foreach ($p in $procs) {
        $procId = $p.Id
        try {
            Stop-Process -Id $procId -Force -ErrorAction Stop
            Add-Step "stop-existing-tray" "Completed" "Stopped existing repo tray process by PID before validation." @{ pid = $procId; reason = $Reason }
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            Add-Step "stop-existing-tray" "Skipped" "Repo tray process had already exited before cleanup." @{ pid = $procId; reason = $Reason }
        }
    }
}

function Stop-WslKeepAliveProcesses {
    $target = $DistroName
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine.Contains($target, [System.StringComparison]::OrdinalIgnoreCase) -and
            $_.CommandLine.Contains("sleep", [System.StringComparison]::OrdinalIgnoreCase) -and
            $_.CommandLine.Contains("2147483647", [System.StringComparison]::OrdinalIgnoreCase)
        }
    foreach ($p in $procs) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Add-Step "stop-wsl-keepalive" "Completed" "Stopped $target keepalive process by PID." @{ pid = $p.ProcessId; distroName = $target }
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            Add-Step "stop-wsl-keepalive" "Skipped" "$target keepalive process had already exited." @{ pid = $p.ProcessId; distroName = $target }
        }
    }
}

function Start-TrayForLocalSetup {
    Stop-ExistingTrayProcesses -Reason "pre-launch"

    # Forked onboarding entry point is SetupWarning by default; we just force
    # onboarding mode and let the script click "Set up locally".
    $env = @{
        OPENCLAW_SKIP_UPDATE_CHECK = "1"
        OPENCLAW_FORCE_ONBOARDING = "1"
        OPENCLAW_WSL_DISTRO_NAME = $DistroName
        OPENCLAW_WSL_INSTALL_LOCATION = $wslInstallLocation
        OPENCLAW_WSL_ALLOW_EXISTING_DISTRO = if ($Scenario -eq "UpstreamInstall") { "1" } else { "0" }
        OPENCLAW_TRAY_DATA_DIR = $validationAppDataRoot
        OPENCLAW_TRAY_APPDATA_DIR = $validationAppDataRoot
        OPENCLAW_TRAY_LOCALAPPDATA_DIR = $validationLocalAppDataRoot
        OPENCLAW_VISUAL_TEST = "1"
        OPENCLAW_VISUAL_TEST_DIR = $screenshotsRoot
    }

    $saved = @{}
    foreach ($k in $env.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
        [Environment]::SetEnvironmentVariable($k, [string]$env[$k], "Process")
    }

    try {
        New-Item -ItemType Directory -Force -Path $screenshotsRoot | Out-Null
        if (-not (Test-Path -LiteralPath $trayExe)) {
            throw "Built tray executable not found at $trayExe. Run build.ps1 first or omit -NoBuild."
        }
        $proc = Start-Process -FilePath $trayExe -WorkingDirectory $repoRoot -PassThru
        Add-Step "launch-tray" "Completed" "Launched tray onboarding for WSL local setup." @{
            pid = $proc.Id; screenshots = $screenshotsRoot; file = $trayExe; runtimeIdentifier = $runtimeIdentifier
        }
        return $proc
    } finally {
        foreach ($k in $env.Keys) {
            [Environment]::SetEnvironmentVariable($k, $saved[$k], "Process")
        }
    }
}

function Wait-ForSetupCompletion {
    param([int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastPhase = ""; $lastStatus = ""
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $setupStatePath) {
            $text = Read-TextFileWithRetry -Path $setupStatePath
            $state = $text | ConvertFrom-Json
            $copy = Join-Path $runRoot "setup-state.json"
            $text | Set-Content -LiteralPath $copy -Encoding UTF8

            $phase = Convert-SetupPhase $state.Phase
            $status = Convert-SetupStatus $state.Status
            if ($phase -ne $lastPhase -or $status -ne $lastStatus) {
                $lastPhase = $phase; $lastStatus = $status
                $script:summary.setupPhases += [ordered]@{
                    phase = $phase; status = $status; message = [string]$state.UserMessage; timestamp = (Get-Date).ToString("o")
                }
                Add-Step "setup-phase-$phase" $status ([string]$state.UserMessage) @{ phase = $phase; status = $status }
            }

            if ($status -eq "Complete") {
                if ($state.PSObject.Properties.Name -contains "GatewayUrl" -and -not [string]::IsNullOrWhiteSpace([string]$state.GatewayUrl)) {
                    $script:GatewayUrl = [string]$state.GatewayUrl
                    $script:summary.selectedGatewayUrl = $script:GatewayUrl
                }
                Add-Step "setup-state" "Passed" "Setup reached $status." @{
                    status = $status; phase = $phase; path = $copy
                    gatewayUrl = (Get-SafeUriDisplay $script:GatewayUrl)
                }
                return
            }
            if ($status -in @("FailedRetryable", "FailedTerminal", "Blocked", "Cancelled")) {
                Save-DiagnosticsSnapshot -Reason "setup-failed-$phase"
                throw "Setup failed with status $status, phase $phase, code $($state.FailureCode): $($state.UserMessage). Diagnostics: https://aka.ms/wsllogs."
            }
        }
        Start-Sleep -Seconds 2
    }
    Save-DiagnosticsSnapshot -Reason "setup-timeout"
    throw "Setup did not reach Complete within $TimeoutSeconds seconds. Diagnostics: https://aka.ms/wsllogs."
}

function Invoke-TrayLocalSetup {
    $proc = Start-TrayForLocalSetup
    Start-Sleep -Seconds 5

    # SetupWarningPage hosts the "Set up locally" primary button.
    if ($null -eq (Wait-ForUiAutomationElement -AutomationId "OnboardingSetupLocal" -TimeoutSeconds 60)) {
        Save-DiagnosticsSnapshot -Reason "setup-local-button-not-found"
        throw "UI automation target OnboardingSetupLocal was not found on SetupWarningPage."
    }
    Invoke-UiAutomationClick -AutomationId "OnboardingSetupLocal" -TimeoutSeconds 5

    # LocalSetupProgressPage starts the engine on appearance; just wait for state.
    Wait-ForSetupCompletion -TimeoutSeconds $TimeoutSeconds
    return $proc
}

function Stop-TrayProcess {
    param([object]$Process)
    if ($null -ne $Process) {
        $procId = $Process.Id
        $live = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($null -ne $live) {
            Stop-Process -Id $procId -Force
            Add-Step "stop-tray" "Completed" "Stopped tray process by PID after setup validation." @{ pid = $procId }
        } else {
            Add-Step "stop-tray" "Skipped" "Tray process had already exited before cleanup." @{ pid = $procId }
        }
    }
    Stop-ExistingTrayProcesses -Reason "post-validation"
    Stop-WslKeepAliveProcesses
}

function Convert-GatewayUrlToHealthUri {
    param([string]$Url)
    $b = [System.UriBuilder]::new($Url)
    if ($b.Scheme -eq "ws")  { $b.Scheme = "http" }
    elseif ($b.Scheme -eq "wss") { $b.Scheme = "https" }
    $b.Path = ($b.Path.TrimEnd("/") + "/health")
    return $b.Uri.AbsoluteUri
}

function Save-LoopbackNetworkDiagnostics {
    param([string]$Reason)
    # Loopback only - no WSL IP, no `hostname -I`, no lan probes.
    $safe = $Reason -replace "[^a-zA-Z0-9_.-]", "-"
    $tcpPath = Join-Path $commandsRoot "network-$safe-windows-tcp-18789.json"
    try {
        $cs = @(Get-NetTCPConnection -LocalPort 18789 -ErrorAction Stop | ForEach-Object {
            [ordered]@{
                localAddress = $_.LocalAddress; localPort = $_.LocalPort
                state = $_.State.ToString(); owningProcess = $_.OwningProcess
            }
        })
        $cs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $tcpPath -Encoding UTF8
        Add-Step "network-$safe-windows-tcp" "Completed" "Captured Windows TCP listener state for loopback gateway port." @{ path = $tcpPath }
    } catch {
        $_.Exception.Message | Set-Content -LiteralPath $tcpPath -Encoding UTF8
        Add-Step "network-$safe-windows-tcp" "Skipped" "Could not capture Windows TCP listener state. See https://aka.ms/wsllogs." @{ path = $tcpPath }
    }
}

function Save-RedactedSettings {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        Add-Step "settings-redacted" "Skipped" "Tray settings file was not found."
        return
    }
    $copy = Join-Path $runRoot "settings.redacted.json"
    $c = Read-TextFileWithRetry -Path $settingsPath
    $c = $c -replace '("(?:Token|token|GatewayToken|BootstrapToken|bootstrapToken|bootstrap_token|NodeToken|nodeToken)"\s*:\s*")[^"]*(")', '$1<redacted>$2'
    $c | Set-Content -LiteralPath $copy -Encoding UTF8
    Add-Step "settings-redacted" "Completed" "Saved redacted tray settings." @{ path = $copy }
}

function Test-SetupHistoryPhase {
    param([string]$Phase)
    if (-not (Test-Path -LiteralPath $setupStatePath)) { return $false }
    $state = Read-TextFileWithRetry -Path $setupStatePath | ConvertFrom-Json
    if (-not ($state.PSObject.Properties.Name -contains "History")) { return $false }
    foreach ($e in @($state.History)) {
        if ((Convert-SetupPhase $e.Phase) -eq $Phase -and (Convert-SetupStatus $e.Status) -in @("Running", "Complete")) {
            return $true
        }
    }
    return (Convert-SetupPhase $state.Phase) -eq $Phase
}

function Save-RedactedDeviceIdentityShape {
    $idp = Join-Path $validationAppDataRoot "OpenClawTray\device-key-ed25519.json"
    if (-not (Test-Path -LiteralPath $idp)) {
        Add-Step "device-identity" "Failed" "Device identity file was not found." @{ path = $idp }
        return $false
    }
    $copy = Join-Path $runRoot "device-key.shape.redacted.json"
    Copy-RedactedFileIfExists -SourcePath $idp -DestinationPath $copy | Out-Null
    try {
        $id = Get-Content -LiteralPath $idp -Raw | ConvertFrom-Json
        $hasOperatorToken = ($id.PSObject.Properties.Name -contains "DeviceToken" -and -not [string]::IsNullOrWhiteSpace([string]$id.DeviceToken)) -or
                            ($id.PSObject.Properties.Name -contains "OperatorDeviceToken" -and -not [string]::IsNullOrWhiteSpace([string]$id.OperatorDeviceToken))
        Add-Step "device-identity" ($(if ($hasOperatorToken) { "Passed" } else { "Failed" })) "Checked stored device identity token shape." @{
            path = $copy; hasOperatorToken = $hasOperatorToken
        }
        return $hasOperatorToken
    } catch {
        Add-Step "device-identity" "Failed" "Device identity JSON could not be parsed." @{ path = $copy }
        return $false
    }
}

function Test-JsonStringProperty {
    param([object]$Json, [string[]]$Names)
    foreach ($n in $Names) {
        if ($Json.PSObject.Properties.Name -contains $n) {
            $v = [string]$Json.$n
            if (-not [string]::IsNullOrWhiteSpace($v)) { return $true }
        }
    }
    return $false
}

function Get-JsonStringProperty {
    param([object]$Json, [string]$Name)
    if ($Json -and $Json.PSObject.Properties.Name -contains $Name) { return [string]$Json.$Name }
    return ""
}

function Invoke-BootstrapHandoffProbe {
    # Real upstream setup-code / bootstrap proof.
    $stdout = Join-Path $commandsRoot "wsl-bootstrap-token.stdout.txt"
    $stderr = Join-Path $commandsRoot "wsl-bootstrap-token.stderr.txt"
    $args = @("-d", $DistroName, "--", "/opt/openclaw/bin/openclaw", "qr", "--json", "--url", $GatewayUrl)
    & wsl.exe @args > $stdout 2> $stderr
    $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    $raw = if (Test-Path -LiteralPath $stdout) { Read-TextFileWithRetry -Path $stdout -Attempts 20 -DelayMilliseconds 250 } else { "" }
    Write-TextFileWithRetry -Path $stdout -Content (Redact-SensitiveGatewayOutput $raw) -Attempts 20 -DelayMilliseconds 250

    if ($exitCode -ne 0) {
        Add-Step "wsl-bootstrap-token" "Failed" "Gateway QR command failed with exit code $exitCode." @{
            arguments = ($args -join " "); exitCode = $exitCode; stdout = $stdout; stderr = $stderr
        }
        throw "BootstrapTokenCommandFailed: openclaw qr --json failed. See $stdout and $stderr."
    }

    $hasSetupCode = $false; $hasDirectToken = $false
    try {
        $qr = $raw | ConvertFrom-Json
        $hasSetupCode  = Test-JsonStringProperty $qr @("setupCode", "setup_code")
        $hasDirectToken = Test-JsonStringProperty $qr @("bootstrapToken", "bootstrap_token", "token")
    } catch {
        throw "BootstrapTokenJsonInvalid: openclaw qr --json did not produce valid JSON: $($_.Exception.Message)"
    }

    $shape = if ($hasSetupCode) { "UpstreamSetupCode" } elseif ($hasDirectToken) { "DirectBootstrapToken" } else { "Unknown" }
    $script:summary.pairingValidation["bootstrapQrShape"] = $shape
    $script:summary.pairingValidation["realUpstreamBootstrapHandoff"] = $hasSetupCode

    Add-Step "wsl-bootstrap-token" "Completed" "Gateway QR command completed; bootstrap shape is $shape." @{
        arguments = ($args -join " "); exitCode = $exitCode; stdout = $stdout; stderr = $stderr; bootstrapQrShape = $shape; realUpstreamBootstrapHandoff = $hasSetupCode
    }

    if ($RequireRealGatewayBootstrap -and -not $hasSetupCode) {
        throw "RealGatewayBootstrapRequired: expected upstream setupCode bootstrap handoff, but openclaw qr --json returned $shape."
    }
}

function Invoke-OperatorPairingProof {
    if (-not $RequireOperatorPairing) {
        Add-Step "operator-pairing-proof" "Skipped" "Operator pairing proof was not required."
        return
    }
    if (-not (Test-SetupHistoryPhase -Phase "PairOperator")) {
        Save-DiagnosticsSnapshot -Reason "operator-pair-phase-missing"
        throw "OperatorPairingProofFailed: setup state did not record PairOperator."
    }
    if (-not (Save-RedactedDeviceIdentityShape)) {
        Save-DiagnosticsSnapshot -Reason "operator-device-token-missing"
        throw "OperatorPairingProofFailed: stored operator device token is missing."
    }
    Invoke-LoggedProcess "operator-stored-token-reconnect" "dotnet" @(
        "run", "--project", $cliProject, "--",
        "--probe-read", "--skip-chat", "--require-stored-device-token",
        "--connect-timeout-ms", "15000"
    ) -Environment (Get-ValidationAppEnvironment) -SensitiveOutput

    $script:summary.pairingValidation["operatorPaired"] = $true
    Add-Step "operator-pairing-proof" "Passed" "Stored operator device token reconnect succeeded."
}

function Invoke-WindowsNodePairingProof {
    # Windows tray IS the node (per Mike). Confirm the PairWindowsTrayNode phase
    # ran and that gateway node.list returns the tray node.
    if (-not $RequireWindowsNodePairing) {
        Add-Step "windows-node-pairing-proof" "Skipped" "Windows tray node pairing proof was not required."
        return
    }
    if (-not (Test-SetupHistoryPhase -Phase "PairWindowsTrayNode")) {
        Save-DiagnosticsSnapshot -Reason "windows-node-pair-phase-missing"
        throw "WindowsNodePairingProofFailed: setup state did not record PairWindowsTrayNode."
    }
    Invoke-LoggedProcess "windows-node-list-proof" "dotnet" @(
        "run", "--project", $cliProject, "--",
        "--probe-read", "--skip-chat", "--require-stored-device-token", "--require-node",
        "--connect-timeout-ms", "90000"
    ) -Environment (Get-ValidationAppEnvironment) -SensitiveOutput

    $script:summary.pairingValidation["windowsNodePaired"] = $true
    Add-Step "windows-node-pairing-proof" "Passed" "Gateway node.list returned the Windows tray node."
}

function Invoke-SmokeChecks {
    Invoke-LoggedProcess "wsl-list-after" "wsl.exe" @("--list", "--verbose") -IgnoreExitCode
    Save-LoopbackNetworkDiagnostics -Reason "post-install"

    # Gateway in WSL via systemd user unit (UpstreamInstall layout).
    Invoke-LoggedProcess "wsl-openclaw-version" "wsl.exe" @(
        "-d", $DistroName, "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "--version")
    Invoke-LoggedProcess "wsl-openclaw-config-validate" "wsl.exe" @(
        "-d", $DistroName, "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "config", "validate")
    Invoke-LoggedProcess "wsl-gateway-journal" "wsl.exe" @(
        "-d", $DistroName, "-u", "root", "--", "journalctl", "--user", "-u", "openclaw-gateway",
        "--no-pager", "-n", "200") -IgnoreExitCode -SensitiveOutput

    # Loopback-only health probe.
    $healthUri = Convert-GatewayUrlToHealthUri -Url $GatewayUrl
    $healthPath = Join-Path $commandsRoot "gateway-health.json"
    try {
        $h = Invoke-RestMethod -Uri $healthUri -TimeoutSec 10
        $h | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $healthPath -Encoding UTF8
        if (-not $h.ok) { throw "Gateway health response did not contain ok=true." }
        $gw = if ($h.PSObject.Properties.Name -contains "gateway") { $h.gateway } else { $null }
        $version = Get-JsonStringProperty $gw "version"
        $displayName = Get-JsonStringProperty $gw "displayName"
        $isDev = $version -like "*-dev*" -or $displayName -like "Dev OpenClaw*"
        $script:summary.pairingValidation["gatewayImplementation"] = if ($isDev) { "DevShim" } else { "ProductionCandidate" }
        Add-Step "gateway-health" "Passed" "Gateway health endpoint returned ok=true." @{ uri = $healthUri; path = $healthPath }
    } catch {
        throw "Gateway health check failed for ${healthUri}: $($_.Exception.Message). Diagnostics: https://aka.ms/wsllogs."
    }

    Invoke-BootstrapHandoffProbe
    Save-RedactedSettings
    Invoke-OperatorPairingProof
    Invoke-WindowsNodePairingProof

    $args = @(
        "run", "--project", $cliProject, "--",
        "--probe-read", "--skip-chat",
        "--message", "openclaw validation ping",
        "--connect-timeout-ms", "15000"
    )
    if ($RequireOperatorPairing) { $args += "--require-stored-device-token" }
    Invoke-LoggedProcess "openclaw-cli-probe" "dotnet" $args -Environment (Get-ValidationAppEnvironment) -SensitiveOutput
}

function Invoke-DistroUnregisterIfPresent {
    param([string]$Reason)
    Stop-WslKeepAliveProcesses
    # Authoritative repair primitive: `wsl --unregister`. NEVER `wsl --shutdown`.
    Invoke-LoggedProcess "wsl-unregister-$Reason" "wsl.exe" @("--unregister", $DistroName) -IgnoreExitCode

    if (Test-Path -LiteralPath $wslInstallLocation) {
        try {
            Remove-Item -LiteralPath $wslInstallLocation -Recurse -Force -ErrorAction Stop
            Add-Step "remove-install-location-$Reason" "Completed" "Removed install location directory." @{ path = $wslInstallLocation }
        } catch {
            Add-Step "remove-install-location-$Reason" "Skipped" "Could not remove install location: $($_.Exception.Message)" @{ path = $wslInstallLocation }
        }
    }
}

function Invoke-PreIterationCleanup {
    param([int]$Index)
    if ($Scenario -in @("FreshMachine", "Recreate")) {
        Invoke-DistroUnregisterIfPresent -Reason "iteration-$Index-pre"
        # Wipe isolated AppData so identity store starts empty.
        foreach ($p in @($validationAppDataRoot, $validationLocalAppDataRoot)) {
            if (Test-Path -LiteralPath $p) {
                try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop } catch { }
            }
        }
    } else {
        Stop-WslKeepAliveProcesses
    }
}

function Invoke-PostIterationCleanup {
    param([int]$Index, [bool]$IterationFailed)
    if ($Scenario -ne "Recreate") {
        $script:summary.cleanupStatus = if ($script:summary.cleanupStatus -eq "Failed") { "Failed" } else { "Skipped" }
        Add-Step "iteration-$Index-cleanup" "Skipped" "Post-iteration distro cleanup is only required in Recreate scenario."
        return "Skipped"
    }
    if ($IterationFailed -and $KeepFailedDistro) {
        $script:summary.cleanupStatus = if ($script:summary.cleanupStatus -eq "Failed") { "Failed" } else { "Skipped" }
        Add-Step "iteration-$Index-cleanup" "Skipped" "Keeping failed WSL distro for inspection (-KeepFailedDistro)." @{ distroName = $DistroName }
        return "Skipped"
    }
    if (-not $IterationFailed -and -not $CleanupAfterSuccess) {
        $script:summary.cleanupStatus = if ($script:summary.cleanupStatus -eq "Failed") { "Failed" } else { "Skipped" }
        Add-Step "iteration-$Index-cleanup" "Skipped" "Leaving successful distro (-CleanupAfterSuccess:`$false)." @{ distroName = $DistroName }
        return "Skipped"
    }
    try {
        $script:summary.cleanupStatus = "Running"
        Invoke-DistroUnregisterIfPresent -Reason "iteration-$Index-post"
        $script:summary.cleanupStatus = "Passed"
        Add-Step "iteration-$Index-cleanup" "Passed" "Cleaned recreated WSL distro after validation iteration." @{ distroName = $DistroName }
        return "Passed"
    } catch {
        $script:summary.cleanupStatus = "Failed"
        Add-Step "iteration-$Index-cleanup" "Failed" $_.Exception.Message
        if (-not $ContinueOnCleanupFailure) { throw }
        return "Failed"
    }
}

function New-IterationRecord {
    param([int]$Index)
    return [ordered]@{
        index = $Index
        distroName = $DistroName
        installLocation = $wslInstallLocation
        validationStatus = "Running"
        cleanupStatus = "NotStarted"
        error = $null
        cleanupError = $null
        startedAt = (Get-Date).ToString("o")
        finishedAt = $null
    }
}

function Invoke-ValidationIteration {
    param([int]$Index)
    $iteration = New-IterationRecord -Index $Index
    $script:summary.iterations += $iteration
    Add-Step "iteration-$Index" "Started" "Starting validation iteration $Index."
    $trayProcess = $null
    $iterationFailed = $false

    try {
        Invoke-RepositoryValidation
        Invoke-PreIterationCleanup -Index $Index
        $trayProcess = Invoke-TrayLocalSetup
        Invoke-SmokeChecks

        Add-Step "iteration-$Index" "Passed" "Validation iteration $Index passed."
        $iteration.validationStatus = "Passed"
        $script:summary.validationStatus = "Passed"
    } catch {
        $iterationFailed = $true
        $iteration.validationStatus = "Failed"
        $iteration.error = $_.Exception.Message
        $script:summary.validationStatus = "Failed"
        Save-DiagnosticsSnapshot -Reason "iteration-$Index-failed"
        throw
    } finally {
        try {
            Stop-TrayProcess -Process $trayProcess
            $iteration.cleanupStatus = Invoke-PostIterationCleanup -Index $Index -IterationFailed $iterationFailed
        } catch {
            $iteration.cleanupStatus = "Failed"
            $iteration.cleanupError = $_.Exception.Message
            throw
        } finally {
            $iteration.finishedAt = (Get-Date).ToString("o")
        }
    }
}

New-Item -ItemType Directory -Force -Path $runRoot, $commandsRoot, $screenshotsRoot | Out-Null

$exitCode = 0
try {
    Assert-DestructiveSafety
    Invoke-Preflight

    if ($Scenario -eq "PreflightOnly") {
        Add-Step "scenario" "Passed" "Preflight completed."
        $script:summary.validationStatus = "Passed"
        $script:summary.cleanupStatus = "Skipped"
    } elseif ($Scenario -eq "Recreate" -or $Iterations -gt 1) {
        if ($Iterations -lt 1) { throw "-Iterations must be at least 1." }
        for ($i = 1; $i -le $Iterations; $i++) {
            try { Invoke-ValidationIteration -Index $i }
            catch {
                Add-Step "iteration-$i" "Failed" $_.Exception.Message
                if (-not $ContinueOnFailure) { throw }
            }
        }
    } else {
        # UpstreamInstall or FreshMachine, single shot.
        Invoke-ValidationIteration -Index 1
    }

    if ($script:summary.validationStatus -eq "Running") { $script:summary.validationStatus = "Passed" }
    if ($script:summary.cleanupStatus -in @("Running", "NotStarted")) { $script:summary.cleanupStatus = "Skipped" }
    if ($script:summary.validationStatus -eq "Failed") {
        $script:summary.status = "Failed"; $exitCode = 1
    } else {
        $script:summary.status = if ($script:summary.cleanupStatus -eq "Failed") { "PassedWithCleanupFailure" } else { "Passed" }
    }
} catch {
    $script:summary.status = "Failed"
    if ($script:summary.validationStatus -eq "Running") { $script:summary.validationStatus = "Failed" }
    if ($script:summary.cleanupStatus -eq "Running")    { $script:summary.cleanupStatus = "Failed" }
    $script:summary.error = $_.Exception.Message
    Add-Step "validation" "Failed" $_.Exception.Message
    $exitCode = 1
} finally {
    Write-Summary
}

Write-Host "Validation summary: $summaryPath"
if ($script:summary.status -eq "Failed") {
    Write-Host "Diagnostics: see https://aka.ms/wsllogs for WSL networking/lifecycle logs."
}
exit $exitCode
