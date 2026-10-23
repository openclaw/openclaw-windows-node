[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeRuntimeLibrary,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedNativeSha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$NativeProbeExecutable,
    [string]$NativeInitFailureExecutable,
    [string]$ExpectedHarnessSha256
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This real-desktop regression requires Windows.' }
$callerSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
if ($callerSession -eq 0) { throw 'Run this regression in an interactive user session, not session 0.' }
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$launcher = Join-Path $repository 'scripts\Invoke-PrivateDesktop.ps1'
$native = [IO.Path]::GetFullPath($NativeRuntimeLibrary)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $ExpectedNativeSha256) {
    throw 'Native executable does not match the supplied proof input digest.'
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) {
    throw 'Choose an empty output directory; existing proof artifacts will not be overwritten.'
}
New-Item -ItemType Directory -Path "$output\regression" -Force | Out-Null
. "$PSScriptRoot\HarnessPreparation.ps1"
$probe = Resolve-DesktopHarnessExecutable -Executable "$PSScriptRoot\bin\$Configuration\net10.0-windows\OpenClaw.DesktopHarness.exe" `
    -ExpectedSha256 $ExpectedHarnessSha256 -Build {
        dotnet build "$PSScriptRoot\OpenClaw.DesktopHarness.csproj" -c $Configuration -m:1 --disable-build-servers -p:UseSharedCompilation=false --nologo --verbosity quiet *> "$output\managed-probe-build.log"
        if ($LASTEXITCODE -ne 0) { throw "Managed probe build failed. See $output\managed-probe-build.log" }
    }

if ([string]::IsNullOrWhiteSpace($NativeProbeExecutable)) {
    $installer = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $installer)) { throw 'Visual Studio Installer discovery is unavailable; no installation attempted.' }
    $installations = @(& $installer -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'Could not identify one installed MSVC toolchain.' }
    $vcvars = Join-Path ([string]$installations[0]) 'VC\Auxiliary\Build\vcvars64.bat'
    if (!(Test-Path -LiteralPath $vcvars)) { throw 'The selected MSVC developer environment is unavailable.' }
    $NativeProbeExecutable = Join-Path $output 'token-probe.exe'
    $NativeInitFailureExecutable = Join-Path $output 'quiet-init-failure.exe'
    $object = Join-Path $output 'token-probe.obj'
    $command = 'call "{0}" >nul && cl.exe /nologo /O1 /MT /W4 /Fe:"{1}" /Fo:"{2}" "{3}" /link advapi32.lib' -f $vcvars,$NativeProbeExecutable,$object,"$PSScriptRoot\token-probe.c"
    & $env:ComSpec /d /c $command *> "$output\native-probe-build.log"
    if ($LASTEXITCODE -ne 0) { throw "Native probe build failed. See $output\native-probe-build.log" }
    $failureSource="$PSScriptRoot\token-probe.c"
    $command='call "{0}" >nul && cl.exe /nologo /O1 /MT /W4 /LD /DOPENCLAW_FAIL_ATTACH_DLL /Fe:"{1}\quiet-init-fixture.dll" /Fo:"{1}\quiet-init-fixture.obj" "{2}" /link /IMPLIB:"{1}\quiet-init-fixture.lib" && cl.exe /nologo /O1 /MT /W4 /DOPENCLAW_FAIL_ATTACH_EXE /Fe:"{1}\quiet-init-failure.exe" /Fo:"{1}\quiet-init-failure.obj" "{2}" /link "{1}\quiet-init-fixture.lib"' -f $vcvars,$output,$failureSource
    & $env:ComSpec /d /c $command *> "$output\native-failure-fixture-build.log"
    if ($LASTEXITCODE -ne 0) { throw "Native failure fixture build failed. See $output\native-failure-fixture-build.log" }
}
$NativeProbeExecutable = [IO.Path]::GetFullPath($NativeProbeExecutable)
if (!(Test-Path -LiteralPath $NativeProbeExecutable -PathType Leaf)) { throw 'Native token probe is missing.' }
if ([string]::IsNullOrWhiteSpace($NativeInitFailureExecutable) -or
    !(Test-Path -LiteralPath $NativeInitFailureExecutable -PathType Leaf)) {
    throw 'The real DLL-attach-failure fixture is required with an externally built native probe.'
}

. "$PSScriptRoot\Invoke-ProofCaseCollection.ps1"
$cases = @(Invoke-ProofCaseCollection -Cases @(
    @{ name='negative-token'; payload='native-probe'; negative=$true },
    @{ name='negative-powershell'; payload='powershell'; negative=$true },
    @{ name='fixed-token'; payload='native-probe'; negative=$false },
    @{ name='fixed-powershell'; payload='powershell'; negative=$false }
) -RunCase {
    param($case)
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
    $config = Get-Content -LiteralPath "$directory\actual-policy.json" -Raw | ConvertFrom-Json
    if ($result.ExecutionMode -ne 'Sandbox' -or $result.nativeSha256 -ne $ExpectedNativeSha256 -or
        !$config.processContainer -or $config.learningMode -or $config.desktopIsolation -ne 'Container' -or
        $config.uiDisabled -or $config.clipboard -ne 'None' -or $config.inputInjection -or
        $config.egress -ne 'Deny' -or $config.ingress -ne 'Deny') {
        throw "Policy/containment drift in $($case.name)"
    }
    if ($case.payload -eq 'native-probe') {
        if ($result.nativeProbeSha256 -ne (Get-FileHash -LiteralPath $NativeProbeExecutable -Algorithm SHA256).Hash) {
            throw 'The executed native probe differs from the supplied controlled binary.'
        }
        $lines = @($result.Stdout -split "`r?`n" | Where-Object Length -GT 0)
        $before = $lines[0] | ConvertFrom-Json
        $after = $lines[1] | ConvertFrom-Json
        if ($before.isAppContainer -ne 1 -or $before.integritySid -ne 'S-1-16-4096' -or
            $before.win32kMitigationFlags -ne 0 -or $before.sessionId -ne $callerSession) {
            throw "Unexpected actual child identity in $($case.name)"
        }
        if (!$after.loaded -or $after.loadError -ne 0 -or $result.ExitCode -ne 0 -or $result.TimedOut) {
            throw 'Published SDK container desktop did not preserve actual AppContainer USER32 access'
        }
        if (!$case.negative -and !$launch.aclAndLabelBytesEqual) {
            throw 'Fixed caller desktop security differs from Default'
        }
    }
    elseif ($result.ExitCode -ne 0 -or $result.TimedOut -or
        $result.Stdout.Trim() -ne 'OPENCLAW_NATIVE_STARTUP_PROBE' -or
        (!$case.negative -and !$launch.aclAndLabelBytesEqual)) {
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
    [ordered]@{
        name=$case.name; passed=$true; negativeControl=$case.negative
        desktop=$desktop; desktopRemoved=$true
        exitCode=$result.ExitCode; timedOut=$result.TimedOut; durationMs=$result.DurationMs
        aclAndLabelBytesEqual=$launch.aclAndLabelBytesEqual
    }
})
$ownershipPassed = $false
$ownershipError = $null
try {
    & "$PSScriptRoot\Test-LauncherOwnership.ps1" -Launcher $launcher -ProbeExecutable $probe `
        -NativeExecutable $native -NativeProbeExecutable $NativeProbeExecutable `
        -NativeInitFailureExecutable $NativeInitFailureExecutable `
        -OutputDirectory "$output\ownership"
    $ownershipPassed = $true
}
catch { $ownershipError = $_.Exception.Message }
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
    ownershipAndEnvironmentRegressionsPassed=$ownershipPassed
    ownershipError=$ownershipError
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$output\desktop-fidelity-regression.json" -Encoding utf8
$cases | ForEach-Object { [pscustomobject]$_ } | Select-Object name,passed,exitCode,desktopRemoved
$failedCases = @($cases | Where-Object { !$_.passed })
if ($failedCases.Count -or !$ownershipPassed) {
    throw "Desktop fidelity failed: $($failedCases.Count) case(s), ownership passed: $ownershipPassed. See $output\desktop-fidelity-regression.json"
}
