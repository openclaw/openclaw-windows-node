[CmdletBinding()]
param([string]$RepoRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
. (Join-Path $RepoRoot 'scripts\NativeTestFailureMetadata.ps1')
# Keep child-process result paths below Windows PowerShell 5.1's directory limit.
$root = Join-Path ([IO.Path]::GetTempPath()) ('ci-native-metadata-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$marker = 'private-value-must-not-be-exported'
$eventXml = @"
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
<EventData>
<Data Name="AppName">dotnet.exe</Data><Data Name="ProcessId">0x1234</Data>
<Data Name="ModuleName">C:\$marker\Microsoft.UI.Xaml.dll</Data>
<Data Name="ModuleVersion">2.5.1.0</Data><Data Name="ExceptionCode">c0000005</Data>
<Data Name="AppPath">C:\$marker\dotnet.exe</Data>
<Data Name="CommandLine">$marker</Data><Data Name="Message">$marker</Data>
</EventData></Event>
"@
$checks = 0
try {
    $fault = ConvertTo-CiNativeFault $eventXml
    if ($fault.application -ne 'dotnet.exe' -or $fault.processId -ne '0x1234' -or
        $fault.module -ne 'Microsoft.UI.Xaml.dll' -or $fault.moduleVersion -ne '2.5.1.0' -or
        $fault.exceptionCode -ne '0xC0000005') { throw 'Native fault metadata was not projected correctly.' }
    if (($fault | ConvertTo-Json) -match $marker) { throw 'Private event fields or paths escaped.' }
    $checks++
    if ($null -ne (ConvertTo-CiNativeFault ($eventXml.Replace('dotnet.exe', 'unrelated.exe')))) {
        throw 'An unrelated application was admitted.'
    }
    $checks++
    $invalidXml = $eventXml.Replace('c0000005', $marker).Replace('2.5.1.0', $marker)
    $invalidXml = $invalidXml.Replace('0x1234', $marker).Replace('Microsoft.UI.Xaml.dll', "$marker value.dll")
    $invalid = ConvertTo-CiNativeFault $invalidXml
    if ($null -ne $invalid.exceptionCode -or $null -ne $invalid.moduleVersion -or
        $null -ne $invalid.processId -or $null -ne $invalid.module) { throw 'Malformed metadata escaped.' }
    $checks++
    $rejected = $false
    try { ConvertTo-CiNativeFault '<!DOCTYPE Event [<!ENTITY x SYSTEM "file:///unreadable">]><Event>&x;</Event>' }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'External entities were not rejected.' }
    $checks++

    $script:eventXml = $eventXml
    $script:queryMode = 'events'
    function Get-WinEvent {
        [CmdletBinding()]param([hashtable]$FilterHashtable)
        if ($FilterHashtable.LogName -ne 'Application' -or $FilterHashtable.Id -ne 1000 -or
            $FilterHashtable.StartTime -gt [datetime]::UtcNow) { throw 'Unexpected event query.' }
        if ($script:queryMode -eq 'denied') { throw [UnauthorizedAccessException]::new($marker) }
        if ($script:queryMode -eq 'empty') {
            $PSCmdlet.ThrowTerminatingError([Management.Automation.ErrorRecord]::new(
                [InvalidOperationException]::new($marker), 'NoMatchingEventsFound',
                [Management.Automation.ErrorCategory]::ObjectNotFound, $null))
        }
        $event = [pscustomobject]@{ Xml = $script:eventXml; Message = $marker }
        $event | Add-Member -MemberType ScriptMethod -Name ToXml -Value { $this.Xml } -PassThru
    }
    function Get-AppxPackage {
        [CmdletBinding()]param([string]$Name)
        if ($Name -ne 'Microsoft.WindowsAppRuntime.*') { throw 'Unexpected package query.' }
        [pscustomobject]@{
            Name = 'Microsoft.WindowsAppRuntime.2.5'; Version = '2.5.1.0'; Architecture = 'X64'
            InstallLocation = $marker; Publisher = $marker; PackageFamilyName = $marker
        }
    }
    foreach ($mode in @('events', 'empty', 'denied')) {
        $script:queryMode = $mode
        $path = Join-Path $root "$mode.json"
        $output = @(Write-CiNativeFailureMetadata -StartedUtc ([datetime]::UtcNow.AddMinutes(-1)) `
            -TestCommandExitCode 73 -OutputPath $path 3>&1)
        $json = Get-Content $path -Raw
        $summary = $json | ConvertFrom-Json
        $expected = switch ($mode) { 'events' { 'collected' }; 'empty' { 'no-matching-fault-events' }; 'denied' { 'unavailable' } }
        if ($summary.faultCollection -ne $expected -or $summary.testCommandExitCode -ne 73 -or
            $summary.memoryDumpsCollected -ne $false -or $summary.runtimePackages.Count -ne 1) {
            throw "$mode lost its result or overclaimed diagnostics."
        }
        if ($json -match $marker -or ($output -join "`n") -match $marker) { throw "$mode leaked private data." }
        $checks++
    }

    $harness = Join-Path $root 'runner.ps1'
    Set-Content -LiteralPath $harness -Value @'
param([string]$Repo, [string]$Results, [switch]$BlockMetadataWrite)
$ErrorActionPreference = 'Stop'
$env:OPENCLAW_CI_COLLECT_COVERAGE = 'false'
$global:blockMetadataWrite = $BlockMetadataWrite
function global:dotnet {
    if ($global:blockMetadataWrite) {
        $resultsIndex = [Array]::IndexOf($args, '--results-directory')
        if ($resultsIndex -lt 0) { throw 'The CI runner did not supply its results directory.' }
        New-Item -ItemType Directory -Path (Join-Path $args[$resultsIndex + 1] 'native-failure.json') | Out-Null
    }
    $global:LASTEXITCODE = 73
}
function global:Get-WinEvent {
    [CmdletBinding()]param([hashtable]$FilterHashtable)
    throw [UnauthorizedAccessException]::new('private-value-must-not-be-exported')
}
function global:Get-AppxPackage {
    [CmdletBinding()]param([string]$Name)
    throw [UnauthorizedAccessException]::new('private-value-must-not-be-exported')
}
& (Join-Path $Repo 'scripts\Invoke-CiTest.ps1') -Project 'unused.csproj' `
    -ResultsDirectory $Results -TrxFileName 'native.trx' -CollectNativeFailureMetadata
exit $LASTEXITCODE
'@
    $shell = (Get-Process -Id $PID).Path
    foreach ($blockWrite in @($false, $true)) {
        $resultsName = if ($blockWrite) { "runner-write-failure-$marker" } else { 'runner-results' }
        $results = Join-Path $root $resultsName
        $arguments = @('-NoProfile', '-File', $harness, '-Repo', $RepoRoot, '-Results', $results)
        if ($blockWrite) { $arguments += '-BlockMetadataWrite' }
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = @(& $shell @arguments 2>&1)
            $exitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $savedPreference }
        if ($exitCode -ne 73 -or ($output -join "`n") -match $marker) {
            $safeOutput = ($output -join "`n").Replace($marker, '[private fixture marker]')
            throw "Secondary diagnostics changed the test exit code or leaked an error message (exit=$exitCode): $safeOutput"
        }
        $report = @(Get-ChildItem $results -Filter native-failure.json -Recurse)
        if ($blockWrite) {
            if ($report.Count -ne 1 -or -not $report[0].PSIsContainer -or
                ($output -join "`n") -notmatch 'Secondary native diagnostic failure .*preserving test exit code 73') {
                throw 'The blocked metadata write did not exercise the secondary diagnostic failure handler.'
            }
        } elseif ($report.Count -ne 1 -or $report[0].PSIsContainer -or
            (Get-Content $report[0].FullName -Raw | ConvertFrom-Json).testCommandExitCode -ne 73) {
            throw 'The opted-in CI runner did not preserve its failure receipt.'
        }
        $checks++
    }
    Write-Host "Safe native failure metadata regressions passed: $checks cases."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
$global:LASTEXITCODE = 0
