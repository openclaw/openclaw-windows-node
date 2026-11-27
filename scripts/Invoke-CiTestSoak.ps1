<#
.SYNOPSIS
Runs the tests a pull request adds or changes several times, some of them under CPU contention.
.DESCRIPTION
Reads the selection written by scripts\Get-CiChangedTests.ps1, builds each selected test project
once, and runs its selected tests repeatedly with `dotnet test --no-build`. Any failed iteration
fails the step, naming the failing tests, the iteration and whether it ran under contention.

Settings come from the environment so older checkouts never receive unknown parameters:
- OPENCLAW_CI_SOAK_ITERATIONS: runs per project (default 10, 1 to 100).
- OPENCLAW_CI_SOAK_CONTENTION_ITERATIONS: how many of those runs start CPU burners (default 3).
- OPENCLAW_CI_SOAK_CONTENTION_WORKERS: burner processes (default half the logical processors).
- OPENCLAW_CI_SOAK_HANG_SECONDS: blame-hang timeout for one test (default 300).
- OPENCLAW_CI_SOAK_FIXTURE_NATIVE_INPUT: 1 lets fixture UI tests inject native input, as the fixture
  lane's -NativeInputProof does; 0 or unset keeps it off (default).
- OPENCLAW_CI_STEP_TIMEOUT_MINUTES: the step budget. Builds and runs that would not finish inside 90%
  of it (estimated at 1.5 times the slowest comparable work) are not started. Repeat runs also stop at
  the project's share of the time left, so every selected project gets its first run.
Each run gets its own product data folders under the job's OPENCLAW_TRAY_* roots, and hang dumps are
off. Tray UI tests run as two subsets per run, each with its CI lane's environment: fixture UI tests
(Category=GatewayFixture) with OPENCLAW_RUN_GATEWAY_FIXTURE_UI=1 and the native-input setting above,
and the other UI tests with neither. Tests in lanes with their own environment (production resources,
animation preference, native onboarding, accessibility and native keyboard proof) are excluded; the
lanes still run them, and the soak lists any selected test that did not run.
.EXAMPLE
.\scripts\Invoke-CiTestSoak.ps1 -SelectionPath $env:RUNNER_TEMP\soak-selection.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SelectionPath,
    [string]$ResultsDirectory = 'TestResults\Soak'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stepBudget = [Diagnostics.Stopwatch]::StartNew()
. (Join-Path $PSScriptRoot 'CiStepBudget.ps1')
$stepTimeoutMinutes = Get-CiStepTimeoutMinutes

function Get-SoakSetting([string]$Name, [int]$Default, [int]$Minimum, [int]$Maximum) {
    $raw = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $Default }
    $value = 0
    if (-not [int]::TryParse($raw.Trim(), [ref]$value) -or $value -lt $Minimum -or $value -gt $Maximum) {
        throw "$Name must be an integer from $Minimum to $Maximum, not '$raw'."
    }
    return $value
}

$iterations = Get-SoakSetting 'OPENCLAW_CI_SOAK_ITERATIONS' 10 1 100
$contentionIterations = Get-SoakSetting 'OPENCLAW_CI_SOAK_CONTENTION_ITERATIONS' 3 0 $iterations
$workers = Get-SoakSetting 'OPENCLAW_CI_SOAK_CONTENTION_WORKERS' ([Math]::Max(1, [int][Math]::Ceiling([Environment]::ProcessorCount / 2))) 1 256
$hangSeconds = Get-SoakSetting 'OPENCLAW_CI_SOAK_HANG_SECONDS' 300 30 3600
$fixtureNativeInput = Get-SoakSetting 'OPENCLAW_CI_SOAK_FIXTURE_NATIVE_INPUT' 0 0 1
$laneExclusions = 'Category!=ProductionResources&Category!=AnimationPreferenceEnabled&Category!=AnimationPreferenceDisabled&Category!=NativeOnboardingProof&Category!=Accessibility&KeyboardProof!=Required'

# Sets a variable for child processes; $null removes it (SetEnvironmentVariable would keep it empty).
function Set-SoakVariable([string]$Name, $Value) {
    if ($null -eq $Value) { Remove-Item -LiteralPath "Env:\$Name" -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value) }
}

# The test subsets of one run, each with the child environment of the CI lane that normally runs it.
# A $null value removes the variable. Fixture UI tests get native input only through the soak setting,
# never from an inherited step value, as scripts\test-gateway-fixture.ps1 does for -NativeInputProof.
function Get-SoakSubsets([bool]$Ui) {
    if (-not $Ui) { return , @([pscustomobject]@{ Name = 'tests'; Filter = ''; Environment = @{} }) }
    return , @(
        [pscustomobject]@{ Name = 'fixture-ui'; Filter = '&Category=GatewayFixture'; Environment = @{
                OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
                OPENCLAW_RUN_KEYBOARD_PROOF = $(if ($fixtureNativeInput -eq 1) { '1' } else { '0' })
            }
        },
        [pscustomobject]@{ Name = 'ui'; Filter = '&Category!=GatewayFixture'; Environment = @{
                OPENCLAW_RUN_GATEWAY_FIXTURE_UI = $null
                OPENCLAW_RUN_KEYBOARD_PROOF = $null
            }
        })
}

# Spreads the contended runs evenly from the start, for example runs 2, 5 and 8 of 10.
function Test-ContendedIteration([int]$Iteration) {
    for ($k = 0; $k -lt $contentionIterations; $k++) {
        if ([int][Math]::Round(($k + 0.5) * $iterations / $contentionIterations, [MidpointRounding]::AwayFromZero) -eq $Iteration) { return $true }
    }
    return $false
}

# Adds each burner to $Burners as soon as it starts, so a failed launch still leaves them stoppable.
function Start-CpuBurners([Collections.Generic.List[object]]$Burners) {
    $shell = (Get-Process -Id $PID).Path
    for ($k = 0; $k -lt $workers; $k++) {
        $Burners.Add((Start-Process -FilePath $shell -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', 'while ($true) { }') -PassThru -WindowStyle Hidden))
    }
}

function Stop-CpuBurners([Collections.Generic.List[object]]$Burners) {
    foreach ($burner in $Burners) {
        try { if (-not $burner.HasExited) { $burner.Kill($true) } } catch [InvalidOperationException] { }
    }
    $Burners.Clear()
}

# Test display names can carry theory arguments; escape them so they cannot form workflow commands.
function Format-CiCommandData([string]$Text) {
    $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

# dotnet test writes one report even when its filter matches nothing; any other count is an error.
function Read-SoakResult([string]$Directory) {
    $report = @(Get-ChildItem -LiteralPath $Directory -Filter *.trx -Recurse -ErrorAction SilentlyContinue)
    if ($report.Count -ne 1) { throw "Expected one test report in $Directory, found $($report.Count)." }
    [xml]$trx = Get-Content -LiteralPath $report[0].FullName -Raw
    $namespace = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $namespace.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
    $counters = $trx.SelectSingleNode('//t:ResultSummary/t:Counters', $namespace)
    if (-not $counters) { throw "The test report in $Directory has no result counters." }
    $results = @($trx.SelectNodes('//t:UnitTestResult', $namespace))
    [pscustomobject]@{
        Executed = [int]$counters.executed
        Passed = [int]$counters.passed
        Failed = @($results | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
        # Theory cases report as Method(arguments); the method name is what the selection lists.
        Ran = @($results | Where-Object { $_.outcome -ne 'NotExecuted' } | ForEach-Object { ($_.testName -replace '\(.*$', '') } | Sort-Object -Unique)
    }
}

# Stops before work that would not finish by the deadline (seconds into the step), estimating 1.5
# times the slowest comparable work so far. With no estimate yet, it keeps a quarter of the budget.
function Test-SoakBudget([double]$NextSeconds, [double]$DeadlineSeconds) {
    if ($stepTimeoutMinutes -le 0) { return $true }
    $estimate = if ($NextSeconds -gt 0) { 1.5 * $NextSeconds } else { $stepTimeoutMinutes * 60 * 0.25 }
    return $stepBudget.Elapsed.TotalSeconds + $estimate -le $DeadlineSeconds
}

$selection = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
$projects = @($selection.projects | Where-Object { $_ })
if (-not $projects.Count) {
    Write-Host 'New-test soak: no added or changed tests were selected.'
    return
}
$summary = [Collections.Generic.List[string]]::new()
$summary.Add('### New and changed test soak')
$summary.Add('')
$summary.Add("$iterations run(s) per project; $contentionIterations with $workers CPU burner(s).")
$summary.Add('')
$summary.Add('| Project | Tests | Runs completed | Result |')
$summary.Add('|---|---|---|---|')
$failure = $null
$slowestBuild = 0d
$limit = $stepTimeoutMinutes * 60 * 0.9
# Every run and subset gets fresh product data folders, as each CI lane does, so leftover state cannot fail later tests.
$dataVariables = @('OPENCLAW_TRAY_DATA_DIR', 'OPENCLAW_TRAY_APPDATA_DIR', 'OPENCLAW_TRAY_LOCALAPPDATA_DIR')
$dataRoots = @{}
foreach ($name in $dataVariables) { $dataRoots[$name] = [Environment]::GetEnvironmentVariable($name) }
$burners = [Collections.Generic.List[object]]::new()
try {
    for ($index = 0; $index -lt $projects.Count; $index++) {
        $project = $projects[$index]
        # Builds and each project's first run may use the whole budget. Repeat runs stop at the project's
        # share of what is left, so a slow project cannot starve the ones after it of their first runs.
        $projectDeadline = $stepBudget.Elapsed.TotalSeconds + ($limit - $stepBudget.Elapsed.TotalSeconds) / ($projects.Count - $index)
        $slowestRun = 0d
        $runtimeArguments = if ($project.runtime) { @('-r', $project.runtime) } else { @() }
        $built = $true
        $build = [Diagnostics.Stopwatch]::StartNew()
        foreach ($target in @(@($project.prebuild | Where-Object { $_ }) + $project.project)) {
            if (-not (Test-SoakBudget $slowestBuild $limit)) { $built = $false; break }
            $timer = [Diagnostics.Stopwatch]::StartNew()
            & dotnet build $target -c Debug @runtimeArguments -nologo | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Building $target for the soak failed." }
            $slowestBuild = [Math]::Max($slowestBuild, $timer.Elapsed.TotalSeconds)
        }
        if (-not $built) {
            Write-Host ("::warning title=New-test soak budget::{0} was not soaked: the {1}-minute step budget is nearly used." -f $project.name, $stepTimeoutMinutes)
            $summary.Add(("| {0} | {1} | 0 of {2} | not run (budget) |" -f $project.name, @($project.tests).Count, $iterations))
            continue
        }
        Write-Host ("Built {0} in {1:0.0} s." -f $project.project, $build.Elapsed.TotalSeconds)
        $names = @($project.tests | ForEach-Object fullyQualifiedName)
        $filter = '(' + (($names | ForEach-Object { "FullyQualifiedName=$_" }) -join '|') + ')&' + $laneExclusions
        $subsets = Get-SoakSubsets ([bool]$project.ui)
        $completed = 0
        $result = 'passed'
        for ($iteration = 1; $iteration -le $iterations; $iteration++) {
            if (-not (Test-SoakBudget $slowestRun $(if ($iteration -eq 1) { $limit } else { $projectDeadline }))) {
                Write-Host ("::warning title=New-test soak budget::{0} ran {1} of {2} soak runs to stay within its share of the {3}-minute step budget." -f $project.name, $completed, $iterations, $stepTimeoutMinutes)
                $result = 'passed (budget-limited)'
                break
            }
            $contended = Test-ContendedIteration $iteration
            $runName = 'run-{0:00}' -f $iteration
            $run = [pscustomobject]@{ Executed = 0; Passed = 0; Failed = [Collections.Generic.List[string]]::new(); Ran = [Collections.Generic.List[string]]::new() }
            $exitCode = 0
            $failedSubset = $null
            $timer = [Diagnostics.Stopwatch]::StartNew()
            try {
                if ($contended) { Start-CpuBurners $burners }
                foreach ($subset in $subsets) {
                    $runPath = Join-Path $project.name (Join-Path $runName $subset.Name)
                    $directory = Join-Path $ResultsDirectory $runPath
                    New-Item -ItemType Directory -Force -Path $directory | Out-Null
                    # Each subset of each run gets fresh product data folders, as each CI lane does.
                    foreach ($name in $dataVariables) {
                        if ($dataRoots[$name]) { Set-SoakVariable $name (Join-Path $dataRoots[$name] $runPath) }
                    }
                    $saved = @{}
                    foreach ($variable in $subset.Environment.Keys) {
                        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
                        Set-SoakVariable $variable $subset.Environment[$variable]
                    }
                    try {
                        # Hang dumps can hold user data and are large; the hang itself fails the run.
                        & dotnet test $project.project --no-build -c Debug @runtimeArguments --filter ($filter + $subset.Filter) `
                            --results-directory $directory --logger 'trx;LogFileName=soak.trx' `
                            --blame-hang --blame-hang-timeout "$($hangSeconds)s" --blame-hang-dump-type none | Out-Host
                        $subsetExit = $LASTEXITCODE
                    } finally {
                        foreach ($variable in $saved.Keys) { Set-SoakVariable $variable $saved[$variable] }
                    }
                    $part = Read-SoakResult $directory
                    $run.Executed += $part.Executed
                    $run.Passed += $part.Passed
                    foreach ($failed in $part.Failed) { $run.Failed.Add($failed) }
                    foreach ($ran in $part.Ran) { $run.Ran.Add($ran) }
                    if ($subsetExit -ne 0) { $exitCode = $subsetExit }
                    $subset | Add-Member -NotePropertyName Executed -NotePropertyValue $part.Executed -Force
                    if ($exitCode -ne 0 -or $run.Failed.Count) { $failedSubset = $subset.Name; break }
                }
            } finally {
                Stop-CpuBurners $burners
            }
            $slowestRun = [Math]::Max($slowestRun, $timer.Elapsed.TotalSeconds)
            $label = 'run {0} of {1}{2}' -f $iteration, $iterations, $(if ($contended) { ' under CPU contention' } else { '' })
            if ($exitCode -ne 0 -or $run.Failed.Count) {
                $where = if ($subsets.Count -gt 1) { " ($failedSubset tests)" } else { '' }
                $failure = "$($project.name) $label$where failed: $(@($run.Failed) -join ', ')"
                if (-not $run.Failed.Count) { $failure += "(exit $exitCode, no failed result recorded)" }
                $result = 'failed'
                break
            }
            $completed++
            Write-Host ("Soak {0} {1}: {2} executed, {3} passed in {4:0.0} s." -f $project.name, $label, $run.Executed, $run.Passed, $timer.Elapsed.TotalSeconds)
            if ($iteration -eq 1) {
                $notRun = @($names | Where-Object { $_ -notin $run.Ran })
                if ($notRun.Count) {
                    Write-Host ("::warning title=New-test soak::{0}: {1} selected test(s) did not run here (skipped, gated, or in a lane with its own environment): {2}" -f $project.name, $notRun.Count, (Format-CiCommandData ($notRun -join ', ')))
                }
                # A subset that ran nothing in the first run would run nothing later; skip its startup cost.
                $kept = @($subsets | Where-Object { $_.Executed -gt 0 })
                if ($kept.Count) { $subsets = $kept }
            }
            if ($run.Executed -eq 0) {
                $result = 'not executed'
                break
            }
        }
        $summary.Add(("| {0} | {1} | {2} of {3} | {4} |" -f $project.name, $names.Count, $completed, $iterations, $result))
        if ($failure) { break }
    }
} finally {
    Stop-CpuBurners $burners
    foreach ($name in $dataVariables) { Set-SoakVariable $name $dataRoots[$name] }
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    $summary | ForEach-Object { Write-Host $_ }
    if ($stepTimeoutMinutes -gt 0) {
        Write-CiStepBudget -Elapsed $stepBudget.Elapsed -StepTimeoutMinutes $stepTimeoutMinutes -Label 'New-test soak'
    }
}
if ($failure) {
    Write-Host "::error title=New-test soak::$(Format-CiCommandData $failure)"
    throw "New-test soak failed: $failure"
}
