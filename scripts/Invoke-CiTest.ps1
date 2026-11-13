<#
.SYNOPSIS
    Runs a CI test project with optional push/tag coverage collection.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$TrxFileName,
    [string]$Runtime,
    [string]$Filter,
    [ValidateRange(0, 3600)][int]$HangTimeoutSeconds = 0,
    [switch]$CollectNativeFailureMetadata,
    [switch]$CaptureHostExit,
    [ValidateSet('Unchanged', 'Enabled', 'Disabled')][string]$AnimationPreference = 'Unchanged',
    [switch]$AllowUserAnimationPreferenceChange,
    [switch]$RequireAllTestsPassed,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(1, 2147483647)][int]$ExpectedTestCount
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($AnimationPreference -ne 'Unchanged' -and
    -not ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ENVIRONMENT -eq 'github-hosted') -and
    -not $AllowUserAnimationPreferenceChange) {
    throw 'Changing the native animation preference requires a disposable GitHub-hosted runner or explicit -AllowUserAnimationPreferenceChange.'
}
. (Join-Path $PSScriptRoot "TestResultContract.ps1")
$invocation = New-TestInvocation -ResultsDirectory $ResultsDirectory -TrxFileName $TrxFileName
$ResultsDirectory = $invocation.directory

$testArguments = [System.Collections.Generic.List[string]]::new()
@(
    "test",
    $Project,
    "--no-build",
    "-c",
    $Configuration
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $testArguments.Add("-r")
    $testArguments.Add($Runtime)
}

@(
    "--verbosity",
    "normal",
    "--results-directory",
    $ResultsDirectory,
    "--logger",
    "trx;LogFileName=$TrxFileName"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArguments.Add("--filter")
    $testArguments.Add($Filter)
}

if ($HangTimeoutSeconds -gt 0) {
    # Keep the interrupted test sequence, not a potentially secret-bearing memory dump.
    @(
        "--blame-hang",
        "--blame-hang-timeout",
        "${HangTimeoutSeconds}s",
        "--blame-hang-dump-type",
        "none"
    ) | ForEach-Object { $testArguments.Add($_) }
}

$script:testExitCode = $null
$runTests = {
    $collectCoverage = $env:OPENCLAW_CI_COLLECT_COVERAGE -eq "true"
    if ($CaptureHostExit) {
        . (Join-Path $PSScriptRoot "CiTestHostExit.ps1")
        if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or -not [IO.Path]::IsPathFullyQualified($env:RUNNER_TEMP)) {
            throw 'Host-exit diagnostics require an absolute RUNNER_TEMP outside the checkout.'
        }
        $tempRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
        $checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
        if ($tempRoot.StartsWith($checkout, [StringComparison]::OrdinalIgnoreCase)) { throw 'Raw diagnostics must stay outside the checkout and its artifacts.' }
        $rawRoot = Join-Path $tempRoot ("ci-testhost-exit-" + $invocation.id)
        $testArguments.Add('--diag')
        $testArguments.Add((Join-Path $rawRoot 'testplatform.log') + ';tracelevel=info')
        New-Item -ItemType Directory -Path $rawRoot -ErrorAction Stop | Out-Null
        $testExit = $null; $nativeErrorType = $null
        # Ordinary UI and steady-resource UI lanes opt in with CaptureHostExit. The invocation
        # owns this curated, content-free file under the already-uploaded TestResults.
        $previousMetadataPath = $env:OPENCLAW_UI_EXCEPTION_METADATA_PATH
        $previousMetadataInvocation = $env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID
        try {
            $env:OPENCLAW_UI_EXCEPTION_METADATA_PATH = Join-Path $ResultsDirectory 'native-ui-exceptions.jsonl'
            $env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID = $invocation.id
            Invoke-CiNativeWithExit -ExitCode ([ref]$testExit) -Command {
                if ($collectCoverage) {
                    $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
                    & dotnet-coverage collect --output $coverageOutput --output-format cobertura dotnet @testArguments
                } else {
                    & dotnet @testArguments
                }
            }
        }
        catch { $nativeErrorType = $_.Exception.GetType().FullName }
        finally {
            try {
                $env:OPENCLAW_UI_EXCEPTION_METADATA_PATH = $previousMetadataPath
                $env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID = $previousMetadataInvocation
            } catch {
                # Diagnostic cleanup must not replace the observed native result.
                try { Write-Host 'CI native UI metadata: environmentRestoreFailed=true' } catch { }
            }
            $diagnostic = Complete-CiHostExitTrace -Root $rawRoot -NativeExitCode $testExit -NativeErrorType $nativeErrorType
            Write-Host ('CI host exit metadata: ' + ($diagnostic | ConvertTo-Json -Depth 5 -Compress))
        }
        $script:testExitCode = $testExit
        # Return to the preference owner before the outer script propagates a failed native exit.
        if ($null -ne $testExit -and $testExit -ne 0) { return }
        if ($null -eq $testExit -or $nativeErrorType) { throw 'Test command failed without a successful native exit.' }
        if ($diagnostic.diagnosticErrorType -or -not $diagnostic.rawTraceRemoved) { throw 'Host-exit diagnostic extraction or cleanup failed.' }
    } elseif ($collectCoverage) {
        $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
        & dotnet-coverage collect `
            --output $coverageOutput `
            --output-format cobertura `
            dotnet @testArguments
    } else {
        & dotnet @testArguments
    }
    if (-not $CaptureHostExit) { $script:testExitCode = $LASTEXITCODE }
}
$projectPath = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Project))
$projectName = [IO.Path]::GetFileName($projectPath.TrimEnd([char[]]'\/'))
$isNativeUiProject = $projectName -in @('OpenClaw.Tray.UITests', 'OpenClaw.Tray.UITests.csproj')
if ($AnimationPreference -ne 'Unchanged' -or $isNativeUiProject) {
    . (Join-Path $PSScriptRoot 'NativeAnimationPreference.ps1')
    # The parent survives a failed/crashed testhost and restores the preference before result admission.
    if ($AnimationPreference -eq 'Unchanged') {
        Invoke-WithNativeAnimationPreference -PreservePreference -Action $runTests
    } else {
        Invoke-WithNativeAnimationPreference -Enabled ($AnimationPreference -eq 'Enabled') -Action $runTests
    }
} else {
    & $runTests
}

if ($null -eq $script:testExitCode) { throw 'The test command did not return an exit code.' }
if ($script:testExitCode -ne 0) {
    if ($CollectNativeFailureMetadata) {
        try {
            . (Join-Path $PSScriptRoot "NativeTestFailureMetadata.ps1")
            Write-CiNativeFailureMetadata -StartedUtc $invocation.startedUtc `
                -TestCommandExitCode $script:testExitCode `
                -OutputPath (Join-Path $ResultsDirectory "native-failure.json")
        } catch {
            Write-Warning ("Secondary native diagnostic failure ({0}); preserving test exit code {1}." -f
                $_.Exception.GetType().Name, $script:testExitCode)
        }
    }
    exit $script:testExitCode
}
$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped:(-not $RequireAllTestsPassed)
if ($PSBoundParameters.ContainsKey('ExpectedTestCount') -and $result.discovered -ne $ExpectedTestCount) {
    throw "Expected $ExpectedTestCount selected tests, but the report contains $($result.discovered)."
}
Write-Host ("CI test results: " + ($result | ConvertTo-Json -Compress))
Write-Host "CI TRX: $($invocation.report)"
# Ordinary CI retains environment-gated skips. This is not native/custom proof.
$global:LASTEXITCODE = 0
