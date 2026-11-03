Set-StrictMode -Version Latest

<#
Pure helpers for scripts\Invoke-GatewayStartupTrace.ps1. Times are integers in a single
unit (FILETIME 100-ns ticks from Defender reports). Overlapping scans are merged before
reporting wall-clock coverage; summed scan durations are reported separately because
concurrent scans cannot be added as exclusive startup delay.
#>

function Get-IntervalUnionLength {
    param([object[]]$Intervals)
    $sorted = @($Intervals | Where-Object { $_.End -gt $_.Start } | Sort-Object Start)
    if ($sorted.Count -eq 0) { return [Int64]0 }
    [Int64]$total = 0
    [Int64]$currentStart = $sorted[0].Start
    [Int64]$currentEnd = $sorted[0].End
    foreach ($interval in $sorted | Select-Object -Skip 1) {
        if ($interval.Start -le $currentEnd) {
            if ($interval.End -gt $currentEnd) { $currentEnd = $interval.End }
        } else {
            $total += $currentEnd - $currentStart
            $currentStart = $interval.Start
            $currentEnd = $interval.End
        }
    }
    return $total + ($currentEnd - $currentStart)
}

function Get-ClippedIntervals {
    param([object[]]$Intervals, [Int64]$WindowStart, [Int64]$WindowEnd)
    foreach ($interval in $Intervals) {
        $start = [Math]::Max([Int64]$interval.Start, $WindowStart)
        $end = [Math]::Min([Int64]$interval.End, $WindowEnd)
        if ($end -gt $start) {
            $processId = if ($interval.PSObject.Properties['ProcessId']) { $interval.ProcessId } else { $null }
            [pscustomobject]@{ Path = $interval.Path; ProcessId = $processId; Start = $start; End = $end }
        }
    }
}

function Get-PayloadPackageGroup {
    param([string]$RelativePath)
    $path = $RelativePath -replace '/', '\'
    if ($path -match '^dist\\extensions\\([^\\]+)') { return "dist\extensions\$($Matches[1])" }
    if ($path -match '^node_modules\\(@[^\\]+\\[^\\]+|[^\\]+)') { return "node_modules\$($Matches[1])" }
    if ($path -match '^dist\\') { return 'dist\other' }
    return ($path -split '\\')[0]
}

function Get-StartupPhaseWindows {
    <#
    Returns contiguous windows in milliseconds since process start from a guard trace:
    the unclassified interval before the first recorded phase, the two recorded phases,
    the gap between them, and any tail before the guard cutoff.
    #>
    param($Trace)
    $import = $Trace.spans.'session-runtime-import'
    $plugin = $Trace.spans.'plugin-config'
    $importEnd = [double]$import.start_ms + [double]$import.elapsed_ms
    $pluginStart = [double]$plugin.start_ms
    $pluginEnd = $pluginStart + [double]$plugin.elapsed_ms
    $windows = @(
        [pscustomobject]@{ Phase = 'before-first-phase'; StartMs = 0.0; EndMs = [double]$import.start_ms }
        [pscustomobject]@{ Phase = 'session-runtime-import'; StartMs = [double]$import.start_ms; EndMs = $importEnd }
        [pscustomobject]@{ Phase = 'between-phases'; StartMs = $importEnd; EndMs = $pluginStart }
        [pscustomobject]@{ Phase = 'plugin-config'; StartMs = $pluginStart; EndMs = $pluginEnd }
    )
    if ([double]$Trace.elapsed_ms -gt $pluginEnd) {
        $windows += [pscustomobject]@{ Phase = 'after-plugin-config'; StartMs = $pluginEnd; EndMs = [double]$Trace.elapsed_ms }
    }
    return $windows
}

function ConvertTo-UtcFileTime {
    # ConvertFrom-Json in PowerShell 7 may already have turned ISO strings into DateTime values.
    param($Value)
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime.ToFileTimeUtc() }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { throw 'A trace timestamp has no time zone.' }
        return $Value.ToUniversalTime().ToFileTimeUtc()
    }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).UtcDateTime.ToFileTimeUtc()
}

function Get-CompleteMpPerformanceReport {
    # Get-MpPerformanceReport returns top-N lists. Reaching either cap means the report may omit
    # scans, so refuse to summarize rather than present partial data as complete.
    param([string]$Path, [int]$TopFiles, [int]$TopScansPerFile)
    $report = Get-MpPerformanceReport -Path $Path -TopFiles $TopFiles -TopScansPerFile $TopScansPerFile -Raw
    if (@($report.TopFiles).Count -ge $TopFiles) {
        throw "The Defender report reached -TopFiles $TopFiles and may be truncated. Rerun with a larger -TopFiles."
    }
    if (@($report.TopFiles | Where-Object { @($_.Scans).Count -ge $TopScansPerFile }).Count -gt 0) {
        throw "A file reached -TopScansPerFile $TopScansPerFile and its scans may be truncated. Rerun with a larger -TopScansPerFile."
    }
    return $report
}

$script:NativeSetupMilestonePatterns = [ordered]@{
    'step-prepare' = '^Native setup step: StepPrepare\.$'
    'session-prepare' = '^Preparing the (isolated Gateway session|packaged Gateway runtime)\.\.\.$'
    'config-validation-start' = '^Validating native Gateway configuration\.\.\.$'
    'step-verify' = '^Native setup step: StepVerify\.$'
    'gateway-start-requested' = '^\[WS\] Native Gateway start requested'
    'gateway-start-acknowledged' = '^\[WS\] Native Gateway start acknowledged'
    'readiness-timeout' = '^\[WS\] The package-managed Gateway did not report a verified listener'
}
# NativeGatewaySetupHost.ReportProgress(VerifyingEndpoint), logged verbatim by the setup page.
$script:ListenerVerifiedMessage = 'Verifying the Gateway endpoint before connecting...'

function Get-NativeSetupMilestones {
    <#
    Parses Companion native-setup JSONL lines for the last "setup attempt started" record and
    returns UTC FILETIME milestones plus an outcome. The setup log has no explicit success
    record. 'listener-verified' is only the exact info record "Verifying the Gateway endpoint
    before connecting..." after the start acknowledgment. NativeGatewaySetupSession reports it
    only after IsolatedGatewayRuntime.EnsureRunningAsync returns with a verified listener; other
    info records (for example a re-enqueued step log) are not listener evidence. It is not
    terminal: handshake, pairing and AI preparation can still fail, so parsing continues and
    the outcome stays 'incomplete' until a failure or cancellation record. Success (reaching the
    AI-setup page) is observed in the UI and supplied separately; see Resolve-NativeSetupOutcome.
    Only fixed message prefixes are read; message bodies are not returned.
    #>
    param([string[]]$Lines)
    $records = @(foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $entry = $line | ConvertFrom-Json -DateKind String } catch { continue }
        if ($null -eq $entry.PSObject.Properties['ts'] -or $null -eq $entry.PSObject.Properties['msg']) { continue }
        [pscustomobject]@{
            Ticks = [Int64](ConvertTo-UtcFileTime $entry.ts)
            Level = if ($entry.PSObject.Properties['level']) { [string]$entry.level } else { '' }
            Message = [string]$entry.msg
            Step = if ($entry.PSObject.Properties['data'] -and $entry.data -and $entry.data.PSObject.Properties['step']) { [string]$entry.data.step } else { $null }
        }
    })
    $startIndex = -1
    for ($i = 0; $i -lt $records.Count; $i++) {
        if ($records[$i].Message -eq 'Native Gateway setup attempt started.') { $startIndex = $i }
    }
    if ($startIndex -lt 0) {
        return [pscustomobject]@{ Outcome = 'not-started'; FailedStep = $null; Milestones = @() }
    }
    $attempt = @($records | Select-Object -Skip $startIndex)
    $milestones = [Collections.Generic.List[object]]::new()
    $milestones.Add([pscustomobject]@{ Name = 'attempt-started'; Ticks = $attempt[0].Ticks })
    $seen = @{}
    $outcome = 'incomplete'
    $failedStep = $null
    $validationOpen = $false
    $acknowledged = $false
    $listenerVerified = $false
    foreach ($record in $attempt | Select-Object -Skip 1) {
        if ($record.Message -like 'Native Gateway setup failed during *') {
            $outcome = 'failed'
            $failedStep = if ($record.Message -match '^Native Gateway setup failed during (\w+):') { $Matches[1] } else { $record.Step }
            $milestones.Add([pscustomobject]@{ Name = 'setup-failed'; Ticks = $record.Ticks })
            break
        }
        if ($record.Message -eq 'Native Gateway setup cancelled.') {
            $outcome = 'cancelled'
            $milestones.Add([pscustomobject]@{ Name = 'setup-cancelled'; Ticks = $record.Ticks })
            break
        }
        if ($validationOpen -and $record.Message -match '^cmd\.(done|cancelled|timeout)') {
            $validationOpen = $false
            $milestones.Add([pscustomobject]@{ Name = 'config-validation-end'; Ticks = $record.Ticks })
            continue
        }
        if ($acknowledged -and -not $listenerVerified -and $record.Level -eq 'info' -and $record.Message -ceq $script:ListenerVerifiedMessage) {
            $listenerVerified = $true
            $milestones.Add([pscustomobject]@{ Name = 'listener-verified'; Ticks = $record.Ticks })
            continue
        }
        foreach ($name in $script:NativeSetupMilestonePatterns.Keys) {
            if (-not $seen.ContainsKey($name) -and $record.Message -match $script:NativeSetupMilestonePatterns[$name]) {
                $seen[$name] = $true
                $milestones.Add([pscustomobject]@{ Name = $name; Ticks = $record.Ticks })
                if ($name -eq 'config-validation-start') { $validationOpen = $true }
                if ($name -eq 'gateway-start-acknowledged') { $acknowledged = $true }
                break
            }
        }
    }
    return [pscustomobject]@{ Outcome = $outcome; FailedStep = $failedStep; Milestones = @($milestones) }
}

$script:AiSetupReachedSignal = 'ai-setup-reached'

function Resolve-NativeSetupOutcome {
    <#
    Combines the parsed setup log with how recording stopped. A logged failure or cancellation
    wins. 'reached-ai-setup' means the task-owned Companion showed the AI-setup page: Gateway
    preparation reached AI setup, not full onboarding. 'failed-ui-only' means the setup page
    showed Retry without a logged failure; some unexpected-cancellation paths only Trace.
    #>
    param([string]$ParsedOutcome, [string]$StopReason)
    if ($ParsedOutcome -in @('failed', 'cancelled', 'not-started')) { return $ParsedOutcome }
    if ($StopReason -eq $script:AiSetupReachedSignal) { return 'reached-ai-setup' }
    if ($StopReason -eq 'ui-retry') { return 'failed-ui-only' }
    return $ParsedOutcome
}

$script:NativeSetupPageIds = @('NativeGatewaySetupStatus')
# WinUI uses x:Name as the UI Automation ID when no explicit ID is set. ChoicesScroller and
# OnboardingLocalAiCard exist only on AiSetupPage.
$script:AiSetupPageIds = @('OnboardingLocalAiCard', 'ChoicesScroller')

function Get-NativeSetupUiStateFromIds {
    <#
    Classifies visible UI Automation IDs from the task-owned Companion setup window:
    'ai-setup' when the AI-setup page is shown and the native setup page is not; 'retry' when
    the native setup page shows Retry and not Cancel; 'busy' while Cancel is visible on it;
    otherwise 'unknown'. Collapsed WinUI elements are absent from the automation tree.
    #>
    param([string[]]$AutomationIds)
    $ids = [Collections.Generic.HashSet[string]]::new([string[]]@($AutomationIds | Where-Object { $_ }))
    $onSetupPage = @($script:NativeSetupPageIds | Where-Object { $ids.Contains($_) }).Count -gt 0
    $onAiPage = @($script:AiSetupPageIds | Where-Object { $ids.Contains($_) }).Count -gt 0
    if ($onAiPage -and -not $onSetupPage) { return 'ai-setup' }
    if ($onSetupPage -and $ids.Contains('RetryButton') -and -not $ids.Contains('CancelButton')) { return 'retry' }
    if ($onSetupPage -and $ids.Contains('CancelButton')) { return 'busy' }
    return 'unknown'
}

function Get-NativeSetupUiState {
    # Reads only automation IDs (no names or values) from top-level windows of one process.
    param([Parameter(Mandatory)][int]$ProcessId)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $automation = [Windows.Automation.AutomationElement]
    $wanted = @($script:NativeSetupPageIds + $script:AiSetupPageIds + 'RetryButton', 'CancelButton')
    $idConditions = [Windows.Automation.Condition[]]@($wanted | ForEach-Object {
        [Windows.Automation.PropertyCondition]::new($automation::AutomationIdProperty, $_)
    })
    $anyWanted = [Windows.Automation.OrCondition]::new($idConditions)
    $windows = $automation::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new($automation::ProcessIdProperty, $ProcessId))
    $found = foreach ($window in $windows) {
        try {
            foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $anyWanted)) {
                if (-not $element.Current.IsOffscreen) { $element.Current.AutomationId }
            }
        } catch [Windows.Automation.ElementNotAvailableException] { }
    }
    return Get-NativeSetupUiStateFromIds -AutomationIds @($found)
}

function Read-SetupStopSignal {
    # Returns 'ai-setup-reached' for that exact signal content, otherwise 'manual'.
    param([string]$Path)
    $content = ([IO.File]::ReadAllText($Path)).Trim()
    if ($content -ceq $script:AiSetupReachedSignal) { return $script:AiSetupReachedSignal }
    return 'manual'
}

function Get-NativeSetupWindows {
    # Contiguous windows between consecutive milestones, ending at EndTicks (recording stop).
    param([object[]]$Milestones, [Int64]$EndTicks)
    $ordered = @($Milestones | Sort-Object Ticks)
    for ($i = 0; $i -lt $ordered.Count; $i++) {
        $end = if ($i + 1 -lt $ordered.Count) { [Int64]$ordered[$i + 1].Ticks } else { $EndTicks }
        $to = if ($i + 1 -lt $ordered.Count) { $ordered[$i + 1].Name } else { 'recording-stop' }
        if ($end -gt $ordered[$i].Ticks) {
            [pscustomobject]@{ Phase = "$($ordered[$i].Name)..$to"; Start = [Int64]$ordered[$i].Ticks; End = $end }
        }
    }
}

function Test-IsElevated {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-PrivateDirectory {
    # Creates a directory readable only by the current user, SYSTEM and Administrators.
    param([string]$Path)
    $directory = New-Item -ItemType Directory -Path $Path
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $sids = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
    foreach ($sid in $sids) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $directory.FullName -AclObject $acl
    return $directory.FullName
}

function Wait-ForFile {
    param([string[]]$Paths, [int]$TimeoutSeconds)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        foreach ($path in $Paths) {
            if (Test-Path -LiteralPath $path) { return $path }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Test-InstanceRecording {
    # wpr requires -instancename to be the last argument.
    param([string]$Name)
    $wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
    $output = & $wpr -status -instancename $Name 2>&1 | Out-String
    return ($output -notmatch 'not recording')
}

function Start-GatewayTraceRecorder {
    <#
    Launches the elevated recorder through UAC and waits for it to report a started instance.
    Returns the recorder control paths. Throws if the recorder does not report ready.
    #>
    param([string]$ToolDirectory, [string]$InstanceName, [string]$TracePath, [string]$ControlDirectory,
        [int]$MaxRecordingSeconds, [int]$StartTimeoutSeconds)
    $paths = [pscustomobject]@{
        Ready = Join-Path $ControlDirectory 'recorder-ready.json'
        Stop = Join-Path $ControlDirectory 'recorder-stop'
        Status = Join-Path $ControlDirectory 'recorder-status.json'
    }
    Write-Host "Approve the UAC prompt to start WPR instance $InstanceName."
    Write-Host "If the recorder window is closed early, run elevated: wpr.exe -cancel -instancename $InstanceName"
    $pwsh = (Get-Process -Id $PID).Path
    $recorder = Join-Path $ToolDirectory 'Start-GatewayStartupRecorder.ps1'
    $arguments = "-NoProfile -File `"$recorder`" -InstanceName $InstanceName -TracePath `"$TracePath`" " +
        "-ControlDirectory `"$ControlDirectory`" -MaxRecordingSeconds $MaxRecordingSeconds"
    Start-Process -FilePath $pwsh -Verb RunAs -ArgumentList $arguments | Out-Null
    $signal = Wait-ForFile -Paths @($paths.Ready, $paths.Status) -TimeoutSeconds $StartTimeoutSeconds
    if ($signal -ne $paths.Ready) { throw 'The elevated recorder did not report a started recording.' }
    return $paths
}

function Stop-GatewayTraceRecorder {
    param([string]$InstanceName, $Paths, [int]$StopTimeoutSeconds)
    [IO.File]::WriteAllText($Paths.Stop, 'stop')
    $signal = Wait-ForFile -Paths @($Paths.Status) -TimeoutSeconds $StopTimeoutSeconds
    if ($null -ne $signal) {
        $status = Get-Content -LiteralPath $Paths.Status -Raw | ConvertFrom-Json
        Write-Host "Recorder outcome: $($status.outcome); cancel: $($status.cancel)"
    }
    if ($null -eq $signal -or (Test-InstanceRecording $InstanceName)) {
        Write-Warning "WPR instance $InstanceName may still be recording. Run elevated: wpr.exe -cancel -instancename $InstanceName"
    }
}

Export-ModuleMember -Function Get-IntervalUnionLength, Get-ClippedIntervals, Get-PayloadPackageGroup, Get-StartupPhaseWindows, ConvertTo-UtcFileTime, Get-CompleteMpPerformanceReport, Get-NativeSetupMilestones, Get-NativeSetupWindows, Resolve-NativeSetupOutcome, Read-SetupStopSignal, Get-NativeSetupUiStateFromIds, Get-NativeSetupUiState, Test-IsElevated, New-PrivateDirectory, Wait-ForFile, Test-InstanceRecording, Start-GatewayTraceRecorder, Stop-GatewayTraceRecorder
