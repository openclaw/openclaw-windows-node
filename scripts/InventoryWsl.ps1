# Domain-specific read-only queries. No shell, distro launch, installer or service control.
$MaxQueryBytes = 32768
$MaxRows = 128
$VersionTimeoutSeconds = 5
$StatusTimeoutSeconds = 10
$ListTimeoutSeconds = 10
$QueryJoinMilliseconds = 5000

function New-SkippedQuery([string]$Kind) {
    return @{ kind = $Kind; outcome = 'not-run'; rootStarted = $false; handleRetained = $false; rootExitObserved = $null; exitCode = $null; rootTerminationRequested = $false; asyncSettled = $true; settled = $true; privateOut = $null; privateError = $null }
}
function Convert-QueryText([byte[]]$Bytes) {
    if ($Bytes.Length -gt $MaxQueryBytes) { return $null }
    try {
        if (($Bytes.Length -ge 2 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 254) -or
            ($Bytes.Length -ge 2 -and $Bytes[1] -eq 0)) {
            $s = [Text.UnicodeEncoding]::new($false, $false, $true).GetString($Bytes)
        } else { $s = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
        $s = $s.TrimStart([char]0xFEFF)
        if ($s -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') { return $null }
        return $s
    } catch { return $null }
}
function Invoke-WslRead([string]$Executable, [ValidateSet('version', 'status', 'list')][string]$Kind) {
    $result = New-SkippedQuery $Kind; $result.outcome = 'query-failed'
    $p = [Diagnostics.Process]::new(); $ownedHandle = $null; $streams = @(); $timeoutCts = $null; $exitTask = $null
    $readCts = [Threading.CancellationTokenSource]::new()
    $seconds = switch ($Kind) { 'version' { $VersionTimeoutSeconds }; 'status' { $StatusTimeoutSeconds }; 'list' { $ListTimeoutSeconds } }
    try {
        $p.StartInfo.FileName = $Executable
        $p.StartInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
        $p.StartInfo.UseShellExecute = $false; $p.StartInfo.CreateNoWindow = $true
        $p.StartInfo.RedirectStandardOutput = $true; $p.StartInfo.RedirectStandardError = $true; $p.StartInfo.RedirectStandardInput = $true
        switch ($Kind) {
            'version' { $p.StartInfo.ArgumentList.Add('--version') }
            'status' { $p.StartInfo.ArgumentList.Add('--status') }
            'list' { $p.StartInfo.ArgumentList.Add('--list'); $p.StartInfo.ArgumentList.Add('--verbose') }
        }
        $result.rootStarted = $p.Start()
        if (-not $result.rootStarted) { throw 'query-not-started' }
        # Retain the actual Start-created handle. Never reacquire a process by PID/name.
        $ownedHandle = $p.SafeHandle
        $result.handleRetained = -not $ownedHandle.IsInvalid -and -not $ownedHandle.IsClosed
        if (-not $result.handleRetained) { throw 'query-handle-unavailable' }
        $p.StandardInput.Close() # No answer, elevation consent, or install prompt input.
        foreach ($s in @($p.StandardOutput.BaseStream, $p.StandardError.BaseStream)) {
            $entry = @{ stream = $s; buffer = [byte[]]::new(4096); bytes = [IO.MemoryStream]::new(); overflow = $false; eof = $false; task = $null }
            $streams += $entry
            $entry.task = $s.ReadAsync($entry.buffer, 0, $entry.buffer.Length, $readCts.Token)
        }
        $exitTask = $p.WaitForExitAsync($readCts.Token)
        $timeoutCts = [Threading.CancellationTokenSource]::new()
        $timeout = [Threading.Tasks.Task]::Delay([TimeSpan]::FromSeconds($seconds), $timeoutCts.Token)
        while ($true) {
            foreach ($s in $streams) {
                if (-not $s.eof -and $s.task.IsCompleted) {
                    $n = $s.task.GetAwaiter().GetResult()
                    if ($n -eq 0) { $s.eof = $true; continue }
                    if ($s.bytes.Length + $n -le $MaxQueryBytes -and -not $s.overflow) { $s.bytes.Write($s.buffer, 0, $n) }
                    else { $s.overflow = $true }
                    $s.task = $s.stream.ReadAsync($s.buffer, 0, $s.buffer.Length, $readCts.Token)
                }
            }
            if ($exitTask.IsCompleted -and $streams[0].eof -and $streams[1].eof) {
                $null = $exitTask.GetAwaiter().GetResult()
                $result.rootExitObserved = $true; $result.exitCode = $p.ExitCode
                if ($streams[0].overflow -or $streams[1].overflow) { $result.outcome = 'output-limit' }
                else {
                    $result.privateOut = Convert-QueryText $streams[0].bytes.ToArray()
                    $result.privateError = Convert-QueryText $streams[1].bytes.ToArray()
                    if ($null -eq $result.privateOut -or $null -eq $result.privateError) { $result.outcome = 'decode-failed' }
                    elseif ($result.exitCode -ne 0) { $result.outcome = 'nonzero' }
                    else { $result.outcome = 'completed' }
                }
                break
            }
            if ($timeout.IsCompleted) { $result.outcome = 'timed-out'; break }
            $wait = @($timeout)
            if (-not $exitTask.IsCompleted) { $wait += $exitTask }
            foreach ($s in $streams) { if (-not $s.eof) { $wait += $s.task } }
            $null = [Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]$wait).GetAwaiter().GetResult()
        }
    } catch { $result.outcome = 'query-failed' }
    finally {
        if ($result.rootStarted -and $result.rootExitObserved -ne $true) {
            try {
                if (-not $p.HasExited -and $result.handleRetained) {
                    $result.rootTerminationRequested = $true
                    $p.Kill($false) # Authorized cleanup of THIS root only. Never a tree/service/distro.
                }
            } catch { } # Joining below determines the observed outcome, not Kill's return.
            try {
                $result.rootExitObserved = $p.WaitForExit($QueryJoinMilliseconds)
                if ($result.rootExitObserved) { $result.exitCode = $p.ExitCode }
            } catch { $result.rootExitObserved = $null }
        }
        $cleanupFault = $false
        try { $readCts.Cancel() } catch { $cleanupFault = $true }
        foreach ($s in $streams) { try { $s.stream.Dispose() } catch { $cleanupFault = $true } }
        $pending = @($streams | ForEach-Object { if ($null -ne $_.task) { $_.task } })
        if ($null -ne $exitTask) { $pending += $exitTask }
        if ($pending.Count -gt 0) {
            $joined = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$pending)
            try { $null = $joined.Wait($QueryJoinMilliseconds) } catch { }
            $result.asyncSettled = $joined.IsCompleted
            if ($joined.IsFaulted) { $null = $joined.Exception } # Observe faults without printing content.
        }
        foreach ($s in $streams) { try { $s.bytes.Dispose() } catch { $cleanupFault = $true } }
        if ($null -ne $timeoutCts) { try { $timeoutCts.Cancel(); $timeoutCts.Dispose() } catch { $cleanupFault = $true } }
        try { $readCts.Dispose(); $p.Dispose() } catch { $cleanupFault = $true }
        # Process owned the retained SafeHandle throughout; no PID reuse lookup.
        $result.settled = ((-not $result.rootStarted) -or $result.rootExitObserved -eq $true) -and $result.asyncSettled -and -not $cleanupFault
        if (-not $result.settled) { $result.outcome = 'cleanup-unconfirmed' }
    }
    return $result
}
function Get-PublicQuery($Query) {
    return [ordered]@{ kind = $Query.kind; outcome = $Query.outcome; rootStarted = $Query.rootStarted; handleRetained = $Query.handleRetained; rootExitObserved = $Query.rootExitObserved; exitCode = $Query.exitCode; rootTerminationRequested = $Query.rootTerminationRequested; asyncSettled = $Query.asyncSettled; settled = $Query.settled }
}

function Get-WslDiagnosticKind([string]$Text) {
    if ($Text -match '(?i)WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED|0x8007019e|requires the Windows Subsystem for Linux Optional Component|Optional components needed to run WSL are not installed|aka\.ms/wslinstall|not installed|not recognized') { return 'Installable' }
    if ($Text -match '(?i)Invalid command line option|unrecognized option|unknown option') { return 'UpdateRequired' }
    if ($Text -match '(?i)virtualization is not enabled|WSL2 is not supported with your current machine configuration|0x80370102') { return 'EnvironmentBlocked' }
    if ($Text -match '(?i)press any key|press enter|elevation|administrator approval') { return 'PromptOrConsentRequired' }
    return 'None'
}
function Read-WslVersion([string]$Text) {
    $versions = @()
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -notmatch '(?i)(?<![A-Za-z0-9])WSL(?![A-Za-z0-9])') { continue }
        $matches = [regex]::Matches($line, '(?<![0-9.])([0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(?:\.[0-9]{1,5})?)(?![0-9.])')
        if ($matches.Count -ne 1) { return $null }
        $versions += $matches[0].Groups[1].Value
    }
    if ($versions.Count -eq 1) { return $versions[0] }
    return $null
}
function Read-StatusReadiness([string]$Text, [string]$ErrorText) {
    $default = $null; $seenDistribution = $false; $unknown = @()
    foreach ($line in ($Text -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -cmatch '\ADefault Distribution:[ \t]*[^\r\n]+\z' -and -not $seenDistribution) { $seenDistribution = $true; continue }
        if ($line -cmatch '\ADefault Version:[ \t]*([12])[ \t]*\z' -and $null -eq $default) { $default = [int]$Matches[1]; continue }
        $unknown += $line
    }
    if (-not [string]::IsNullOrWhiteSpace($ErrorText)) { $unknown += $ErrorText }
    if ($unknown.Count -gt 0) {
        $kind = Get-WslDiagnosticKind ($unknown -join [Environment]::NewLine)
        if ($kind -ceq 'None') { $kind = 'InspectionFailed' }
        return @{ kind = $kind; defaultVersion = $default }
    }
    if ($null -eq $default) { return @{ kind = 'InspectionFailed'; defaultVersion = $null } }
    if ($default -ne 2) { return @{ kind = 'DefaultVersionNot2'; defaultVersion = $default } }
    return @{ kind = 'Ready'; defaultVersion = 2 }
}
function Read-ListSummary([string]$Text) {
    $unknown = [ordered]@{ parse = 'unknown'; count = $null; wsl1 = $null; wsl2 = $null; running = $null; stopped = $null }
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt $MaxQueryBytes) { return $unknown }
    $lines = @($Text -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -gt ($MaxRows + 1) -or $lines[0] -cnotmatch '\A[ \t]*NAME[ \t]+STATE[ \t]+VERSION[ \t]*\z') { return $unknown }
    $one = 0; $two = 0; $running = 0; $stopped = 0
    foreach ($line in ($lines | Select-Object -Skip 1)) {
        # Parse the two rightmost columns; never copy the private name into the result.
        $parts = @($line.Trim().TrimStart('*').Trim() -split '[ \t]+')
        if ($parts.Count -lt 3 -or $parts[-2] -cnotin @('Running', 'Stopped') -or $parts[-1] -cnotin @('1', '2')) { return $unknown }
        if ($parts[-2] -ceq 'Running') { $running++ } else { $stopped++ }
        if ($parts[-1] -ceq '1') { $one++ } else { $two++ }
    }
    return [ordered]@{ parse = 'recognized'; count = $one + $two; wsl1 = $one; wsl2 = $two; running = $running; stopped = $stopped }
}
function Get-WslReadiness($VersionQuery, $StatusQuery, $ListQuery) {
    $r = [ordered]@{ kind = 'InspectionFailed'; version = $null; defaultVersion = $null; distributions = Read-ListSummary '' }
    if (-not $VersionQuery.settled -or $VersionQuery.outcome -cne 'completed') {
        if ($VersionQuery.outcome -ceq 'nonzero') { $kind = Get-WslDiagnosticKind ($VersionQuery.privateOut + [Environment]::NewLine + $VersionQuery.privateError); if ($kind -cne 'None') { $r.kind = $kind } }
        return $r
    }
    if (-not [string]::IsNullOrWhiteSpace($VersionQuery.privateError)) { $kind = Get-WslDiagnosticKind $VersionQuery.privateError; if ($kind -cne 'None') { $r.kind = $kind }; return $r }
    $versionDiagnostic = Get-WslDiagnosticKind $VersionQuery.privateOut
    if ($versionDiagnostic -cne 'None') { $r.kind = $versionDiagnostic; return $r }
    $r.version = Read-WslVersion $VersionQuery.privateOut
    if ($null -eq $r.version) { return $r }
    if ([version]$r.version -lt [version]'2.4.4') { $r.kind = 'UpdateRequired'; return $r }
    if (-not $StatusQuery.settled -or $StatusQuery.outcome -cne 'completed') {
        if ($StatusQuery.outcome -ceq 'nonzero') { $kind = Get-WslDiagnosticKind ($StatusQuery.privateOut + [Environment]::NewLine + $StatusQuery.privateError); if ($kind -cne 'None') { $r.kind = $kind } }
        return $r
    }
    $status = Read-StatusReadiness $StatusQuery.privateOut $StatusQuery.privateError
    $r.kind = $status.kind; $r.defaultVersion = $status.defaultVersion
    if ($r.kind -cne 'Ready') { return $r }
    $r.kind = 'InspectionFailed'
    if (-not $ListQuery.settled -or $ListQuery.outcome -cne 'completed' -or -not [string]::IsNullOrWhiteSpace($ListQuery.privateError)) { return $r }
    $r.distributions = Read-ListSummary $ListQuery.privateOut
    if ($r.distributions.parse -ceq 'recognized') { $r.kind = 'Ready' }
    return $r
}
function Get-AmbientPresence([string[]]$Names) {
    # Caller requests names only. Never read, emit or change any variable's value.
    return [ordered]@{
        observed = $true
        stateDirectoryOverride = @($Names | Where-Object { $_ -ieq 'OPENCLAW_STATE_DIR' }).Count -gt 0
        setupOverrides = @($Names | Where-Object { $_.StartsWith('OPENCLAW_SETUP_', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        trayExecutableOverride = @($Names | Where-Object { $_ -ieq 'OPENCLAW_E2E_TRAY_EXE' }).Count -gt 0
        gatewayVersionOverride = @($Names | Where-Object { $_ -ieq 'OPENCLAW_E2E_GATEWAY_VERSION' }).Count -gt 0
        gatewayPackageOverride = @($Names | Where-Object { $_ -ieq 'OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ' }).Count -gt 0
        protocolResultOverride = @($Names | Where-Object { $_ -ieq 'OPENCLAW_E2E_PROTOCOL_MISMATCH_RESULT' }).Count -gt 0
    }
}
function Test-AmbientClear($Ambient) {
    return $Ambient.observed -and -not ($Ambient.stateDirectoryOverride -or $Ambient.setupOverrides -or $Ambient.trayExecutableOverride -or $Ambient.gatewayVersionOverride -or $Ambient.gatewayPackageOverride -or $Ambient.protocolResultOverride)
}
function Test-ReadOnlyQualification($Report, $Desktop, $Ambient, $Readiness) {
    return $Report.os.observed -and $Report.os.productType -eq 1 -and $Report.os.nativeProcessMatches -and
        $Report.session.userInteractive -eq $true -and $Report.session.nonzeroSession -eq $true -and
        $Desktop.accessible -eq $true -and $Desktop.handleClosed -eq $true -and
        (Test-AmbientClear $Ambient) -and $Readiness.kind -ceq 'Ready'
}
