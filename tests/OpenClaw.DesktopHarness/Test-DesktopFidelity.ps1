[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedNativeSha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$NativeProbeExecutable
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This real-desktop regression requires Windows.' }
$callerSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
if ($callerSession -eq 0) { throw 'Run this regression in an interactive user session, not session 0.' }
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$launcher = Join-Path $repository 'scripts\Invoke-PrivateDesktop.ps1'
$native = [IO.Path]::GetFullPath($NativeExecutable)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $ExpectedNativeSha256) {
    throw 'Native executable does not match the supplied proof input digest.'
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'Choose an empty output directory; existing proof artifacts will not be overwritten.'
}
New-Item -ItemType Directory -Path "$output\regression" -Force | Out-Null
dotnet build "$PSScriptRoot\OpenClaw.DesktopHarness.csproj" -c $Configuration -m:1 --nologo --verbosity quiet *> "$output\managed-probe-build.log"
if ($LASTEXITCODE -ne 0) { throw "Managed probe build failed. See $output\managed-probe-build.log" }
$probe = "$PSScriptRoot\bin\$Configuration\net10.0-windows\OpenClaw.DesktopHarness.exe"

if ([string]::IsNullOrWhiteSpace($NativeProbeExecutable)) {
    $installer = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $installer)) { throw 'Visual Studio Installer discovery is unavailable; no installation attempted.' }
    $installations = @(& $installer -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'Could not identify one installed MSVC toolchain.' }
    $vcvars = Join-Path ([string]$installations[0]) 'VC\Auxiliary\Build\vcvars64.bat'
    if (!(Test-Path -LiteralPath $vcvars)) { throw 'The selected MSVC developer environment is unavailable.' }
    $NativeProbeExecutable = Join-Path $output 'token-probe.exe'
    $object = Join-Path $output 'token-probe.obj'
    $command = 'call "{0}" >nul && cl.exe /nologo /O1 /MT /W4 /Fe:"{1}" /Fo:"{2}" "{3}" /link advapi32.lib' -f $vcvars,$NativeProbeExecutable,$object,"$PSScriptRoot\token-probe.c"
    & $env:ComSpec /d /c $command *> "$output\native-probe-build.log"
    if ($LASTEXITCODE -ne 0) { throw "Native probe build failed. See $output\native-probe-build.log" }
}
$NativeProbeExecutable = [IO.Path]::GetFullPath($NativeProbeExecutable)
if (!(Test-Path -LiteralPath $NativeProbeExecutable -PathType Leaf)) { throw 'Native token probe is missing.' }

$cases = @()
foreach ($case in @(
    @{ name='negative-token'; payload='native-probe'; negative=$true },
    @{ name='negative-powershell'; payload='powershell'; negative=$true },
    @{ name='fixed-token'; payload='native-probe'; negative=$false },
    @{ name='fixed-powershell'; payload='powershell'; negative=$false }
)) {
    $directory = Join-Path $output "regression\$($case.name)"
    $desktop = "NativeRegression-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
    $arguments = '"{0}" "{1}" {2}' -f $directory,$native,$case.payload
    if ($case.payload -eq 'native-probe') { $arguments += ' "' + $NativeProbeExecutable + '"' }
    $launchPath = Join-Path $output "regression\$($case.name)-launch.json"
    $parameters = @{
        Executable=$probe
        ArgumentLine=$arguments
        DataDirectory="$directory-controller"
        McpPort=0
        NamePrefix=$desktop
        ConsoleProcess=$true
        WaitForExit=$true
        ProcessTimeoutSeconds=45
        ResultPath=$launchPath
        NullSecurityNegativeControl=$case.negative
    }
    & $launcher @parameters
    $result = Get-Content -LiteralPath "$directory\result.json" -Raw | ConvertFrom-Json
    $launch = Get-Content -LiteralPath $launchPath -Raw | ConvertFrom-Json
    $desktop = $launch.desktop.Split('\')[-1]
    if ($result.caller.desktop -cne $desktop) { throw 'The actual child desktop does not match the generated owned desktop.' }
    $config = Get-Content -LiteralPath "$directory\actual-config.json" -Raw | ConvertFrom-Json
    if ($result.ContainmentTag -ne 'mxc' -or $result.nativeSha256 -ne $ExpectedNativeSha256 -or
        $config.ui.disable -or $config.ui.clipboard -ne 'none' -or $config.ui.injection -or
        $config.network.defaultPolicy -ne 'block') {
        throw "Policy/containment drift in $($case.name)"
    }
    if ($case.payload -eq 'native-probe') {
        $lines = @($result.Stdout -split "`r?`n" | Where-Object Length -GT 0)
        $before = $lines[0] | ConvertFrom-Json
        $after = $lines[1] | ConvertFrom-Json
        if ($before.isAppContainer -ne 1 -or $before.integritySid -ne 'S-1-16-4096' -or
            $before.win32kMitigationFlags -ne 0 -or $before.sessionId -ne $callerSession) {
            throw "Unexpected actual child identity in $($case.name)"
        }
        if ($case.negative) {
            if ($after.loaded -or $after.loadError -ne 1114 -or
                $before.currentDesktopDaclAccess.writeObjects) {
                throw 'Old NULL-attributes negative control did not reproduce the USER32/access failure'
            }
        }
        elseif (!$after.loaded -or $after.loadError -ne 0 -or
            !$before.currentDesktopDaclAccess.writeObjects -or !$launch.aclAndLabelBytesEqual) {
            throw 'Fixed desktop did not preserve actual AppContainer USER32 access'
        }
    }
    elseif ($case.negative) {
        if ($result.ExitCode -eq 0 -or $result.Stdout -like '*OPENCLAW_NATIVE_STARTUP_PROBE*') {
            throw 'Old NULL-attributes PowerShell negative control unexpectedly succeeded'
        }
    }
    elseif ($result.ExitCode -ne 0 -or $result.TimedOut -or
        $result.Stdout.Trim() -ne 'OPENCLAW_NATIVE_STARTUP_PROBE' -or
        !$launch.aclAndLabelBytesEqual) {
        throw 'Fixed private desktop did not execute the controlled PowerShell in MXC'
    }
    $handle = [ProofDesktop]::OpenDesktopW($desktop, 0, $false, 1)
    if ($handle -ne [IntPtr]::Zero) {
        [void][ProofDesktop]::CloseDesktop($handle)
        throw "Owned desktop remains after proof: $desktop"
    }
    if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
        throw "Could not verify owned desktop removal: $desktop"
    }
    $cases += [ordered]@{
        name=$case.name; passed=$true; negativeControl=$case.negative
        desktop=$desktop; desktopRemoved=$true
        exitCode=$result.ExitCode; timedOut=$result.TimedOut; durationMs=$result.DurationMs
        aclAndLabelBytesEqual=$launch.aclAndLabelBytesEqual
    }
}
& "$PSScriptRoot\Test-LauncherOwnership.ps1" -Launcher $launcher -ProbeExecutable $probe `
    -NativeExecutable $native -NativeProbeExecutable $NativeProbeExecutable `
    -OutputDirectory "$output\ownership"
$sourceHead = @(git -C $repository rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Could not record the tested source head.' }
[ordered]@{
    completedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    sourceCommit=[string]$sourceHead[0]
    nativeSha256=$ExpectedNativeSha256
    nativeProbeSha256=(Get-FileHash -LiteralPath $NativeProbeExecutable -Algorithm SHA256).Hash
    harnessSha256=(Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    cases=$cases
    productOrSdkPolicyChanged=$false
    existingDesktopsModified=$false
    ownershipAndEnvironmentRegressionsPassed=$true
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$output\desktop-fidelity-regression.json" -Encoding utf8
$cases | ForEach-Object { [pscustomobject]$_ } | Select-Object name,passed,exitCode,desktopRemoved
