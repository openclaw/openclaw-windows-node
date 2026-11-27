<#
.SYNOPSIS
    Private CI trace reduction. Never emits raw trace, stderr, paths, or memory.
.EXAMPLE
    . ./scripts/CiTestHostExit.ps1 # Used by Invoke-CiTest.ps1 -CaptureHostExit.
#>
$script:CiExitMaxFiles = 24
$script:CiExitMaxTraceBytes = 32MB
$script:CiExitMaxRecords = 32
# vstest can return while testhost is still tearing down and holding its own trace open. Reading
# or deleting a held file fails, so wait for the writers to close it. A writer that never closes
# still fails the capture; only the release wait is bounded here.
$script:CiExitTraceReleaseVariable = 'OPENCLAW_CI_HOST_EXIT_TRACE_RELEASE_SECONDS'
$script:CiExitTraceReleaseDefaultSeconds = 60
$script:CiExitTraceReleasePollMilliseconds = 250

function Get-CiExitTraceReleaseTimeout {
    $seconds = 0
    $configured = [Environment]::GetEnvironmentVariable($script:CiExitTraceReleaseVariable)
    if ([int]::TryParse($configured, [ref]$seconds) -and $seconds -ge 1 -and $seconds -le 600) { return [TimeSpan]::FromSeconds($seconds) }
    [TimeSpan]::FromSeconds($script:CiExitTraceReleaseDefaultSeconds)
}

# Content-free name for a trace file: the vstest runner, testhost, data collector, or other.
function Get-CiExitTraceKind([string]$Name) {
    if ($Name -ceq 'testplatform.log') { return 'runner' }
    if ($Name -clike 'testplatform.host.*') { return 'host' }
    if ($Name -clike 'testplatform.datacollector.*') { return 'datacollector' }
    'other'
}

# True when another handle still has the file open (Windows ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION).
function Test-CiExitTraceHeld([string]$Path) {
    try { [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None).Dispose(); return $false }
    catch [IO.IOException] {
        if ($_.Exception.HResult -in @([int]0x80070020, [int]0x80070021)) { return $true }
        throw
    }
}

# OnHeld, if given, receives each poll's held trace kinds; it observes and must not release files.
function Wait-CiExitTraceRelease {
    param([IO.FileInfo[]]$Files, [TimeSpan]$Timeout, [Collections.Generic.HashSet[string]]$HeldKinds, [scriptblock]$OnHeld)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $held = @($Files | Where-Object { Test-CiExitTraceHeld $_.FullName })
        foreach ($file in $held) { [void]$HeldKinds.Add((Get-CiExitTraceKind $file.Name)) }
        if ($held.Count -eq 0) { return [long]$clock.ElapsedMilliseconds }
        if ($clock.Elapsed -ge $Timeout) { throw [TimeoutException]::new('A diagnostic trace stayed open past the release deadline.') }
        if ($OnHeld) { & $OnHeld @($held | ForEach-Object { Get-CiExitTraceKind $_.Name }) }
        Start-Sleep -Milliseconds $script:CiExitTraceReleasePollMilliseconds
    }
}

function Invoke-CiNativeWithExit {
    param([scriptblock]$Command, [ref]$ExitCode)
    $ExitCode.Value = $null
    $global:LASTEXITCODE = $null # NativeCommandProcessor writes global, not a nearer shadow.
    try { & $Command }
    finally { $ExitCode.Value = $global:LASTEXITCODE }
}

function New-CiExitCounters {
    [ordered]@{ lines = 0L; envelopes = 0L; unboundCallbacks = 0L; invalidEnvelopes = 0L; callbacks = 0L; invalidHostIds = 0L; invalidExitCodes = 0L; unavailableHostIds = 0L; accepted = 0L }
}

function ConvertTo-CiHostExit {
    param([string]$Line, [Collections.IDictionary]$Counts)
    $Counts.lines++
    # VSTest 18.6/18.7 PlatformEqtTrace envelope and TestHostManagerCallbacks producer.
    $envelope = '^TpTrace (?:Error|Information):[ \t]*\d+[ \t]*:[ \t]*(?<runner>\d+),[ \t]*(?<thread>\d+),[ \t]*(?<date>\d{4}/\d{2}/\d{2}),[ \t]*(?<time>\d{2}:\d{2}:\d{2}\.\d{3}),[ \t]*(?<ticks>\d+),[ \t]*[A-Za-z0-9_.-]+,[ \t]*(?<payload>[^\r\n]*)\z'
    if ($Line -cnotmatch $envelope) {
        if ($Line.Contains('TestHostManagerCallbacks.ExitCallBack:')) { $Counts.unboundCallbacks++ }
        return $null
    }
    $Counts.envelopes++
    $payload = $Matches.payload
    $runner = 0; $thread = 0; $ticks = 0L; $when = [DateTime]::MinValue
    if (-not [int]::TryParse($Matches.runner, [ref]$runner) -or $runner -le 0 -or
        -not [int]::TryParse($Matches.thread, [ref]$thread) -or $thread -le 0 -or
        -not [long]::TryParse($Matches.ticks, [ref]$ticks) -or $ticks -lt 0 -or
        -not [DateTime]::TryParseExact(($Matches.date + ' ' + $Matches.time), 'yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$when)) { $Counts.invalidEnvelopes++; return $null }
    if ($payload -cnotmatch '^TestHostManagerCallbacks\.ExitCallBack: Testhost processId: (?<host>-?\d+) exited with exitcode: (?<code>-?\d+) error:') { return $null }
    $Counts.callbacks++
    $hostId = 0; $code = 0
    if (-not [int]::TryParse($Matches.host, [ref]$hostId) -or ($hostId -le 0 -and $hostId -ne -1)) { $Counts.invalidHostIds++; return $null }
    if (-not [int]::TryParse($Matches.code, [ref]$code)) { $Counts.invalidExitCodes++; return $null }
    if ($hostId -eq -1) { $Counts.unavailableHostIds++ }
    $Counts.accepted++
    [pscustomobject][ordered]@{
        runnerProcessId = $runner
        hostProcessId = $(if ($hostId -eq -1) { $null } else { $hostId })
        hostProcessIdAvailable = ($hostId -ne -1)
        reportedExitCode = $code
        reportedExitCodeHex = ('0x{0:X8}' -f ([long]$code -band 4294967295L))
        # Producer ignores TryGetExitCode's Boolean; these values need not prove OS status.
        valueMayRepresentUnavailableCode = ($code -eq 0 -or $code -eq -1)
    }
}

function Complete-CiHostExitTrace {
    param([string]$Root, $NativeExitCode, [string]$NativeErrorType, [scriptblock]$OnTraceHeld)
    $counts = New-CiExitCounters
    $records = [Collections.Generic.List[object]]::new()
    $errorType = $null; $removed = $false; $fileCount = 0
    $heldKinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); $releaseWaitMs = $null
    try {
        $files = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)
        $fileCount = $files.Count
        if ($fileCount -gt $script:CiExitMaxFiles) { throw 'Diagnostic file limit exceeded.' }
        foreach ($file in $files) {
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $file.Name -cnotlike 'testplatform*' -or $file.Length -gt $script:CiExitMaxTraceBytes) { throw 'Unexpected diagnostic inventory.' }
        }
        $releaseWaitMs = Wait-CiExitTraceRelease -Files $files -Timeout (Get-CiExitTraceReleaseTimeout) -HeldKinds $heldKinds -OnHeld $OnTraceHeld
        # Read exactly the inventory that was admitted and released.
        $released = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop | ForEach-Object Name | Sort-Object)
        if ((@($files | ForEach-Object Name | Sort-Object) -join '|') -cne ($released -join '|')) { throw 'Unexpected diagnostic inventory.' }
        foreach ($file in $files) {
            # Recheck: a held writer can grow the file after inventory.
            $file.Refresh()
            if ($file.Length -gt $script:CiExitMaxTraceBytes) { throw 'Unexpected diagnostic inventory.' }
            $reader = [IO.File]::OpenText($file.FullName)
            try {
                while ($null -ne ($line = $reader.ReadLine())) {
                    $record = ConvertTo-CiHostExit -Line $line -Counts $counts
                    if ($null -ne $record) {
                        if ($records.Count -ge $script:CiExitMaxRecords) { throw 'Diagnostic record limit exceeded.' }
                        $records.Add($record)
                    }
                }
            }
            finally { $reader.Dispose() }
        }
    }
    catch { $errorType = $_.Exception.GetType().FullName }
    finally {
        try {
            Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop
            $removed = -not (Test-Path -LiteralPath $Root)
        }
        catch {
            # Keep the first failure; rawTraceRemoved separately reports that cleanup failed.
            if (-not $errorType) { $errorType = $_.Exception.GetType().FullName }
        }
    }
    [pscustomobject][ordered]@{
        source = 'vstest-exit-callback-reported'; nativeExitCode = $NativeExitCode; nativeErrorType = $NativeErrorType
        traceFiles = $fileCount; counters = $counts; records = @($records.ToArray())
        traceReleaseWaitMs = $releaseWaitMs; heldTraceKinds = @($heldKinds | Sort-Object)
        diagnosticErrorType = $errorType; rawTraceRemoved = $removed
    }
}
