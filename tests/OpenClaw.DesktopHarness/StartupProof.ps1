enum StartupTraceMode { Disabled; Record }

class StartupTraceAdapter {
    [StartupTraceMode] $Mode
    [scriptblock] $StartOperation
    [scriptblock] $StopOperation
    StartupTraceAdapter([StartupTraceMode] $mode, [scriptblock] $start, [scriptblock] $stop) {
        if (!$start -or !$stop) { throw 'Both trace lifecycle operations are required.' }
        $this.Mode = $mode
        $this.StartOperation = $start
        $this.StopOperation = $stop
    }
    [object] Start([object] $context) { return (& $this.StartOperation $context $this.Mode) }
    [object] Stop([object] $context) { return (& $this.StopOperation $context $this.Mode) }
}

function Test-StartupProcessIdentity([int] $ExpectedPid, $CreationUtc, $ActualProcess) {
    try {
        return $null -ne $ActualProcess -and $ActualProcess.Id -eq $ExpectedPid -and
            ([DateTimeOffset]$ActualProcess.StartTime).UtcTicks -eq ([DateTimeOffset]$CreationUtc).UtcTicks
    } catch { return $false }
}

function Get-StartupBinding($Config, $Caller) {
    if ($Config.sdkVersion -cne '1.0.0' -or $Config.sourceHead -notmatch '^[a-f0-9]{40}$' -or
        $Caller.sourceHead -cne $Config.sourceHead -or $Caller.sid -cne $Config.actorSid -or
        $Caller.session -ne $Config.actorSession -or $Caller.session -le 0 -or
        $Caller.integritySid -cne 'S-1-16-8192' -or !$Caller.threadHasNoToken -or
        !$Caller.nativeLibraryPrimed -or !$Caller.completed -or
        $Caller.completed -isnot [bool] -or $Caller.threadHasNoToken -isnot [bool] -or
        $Caller.nativeLibraryPrimed -isnot [bool] -or
        !(Test-StartupProcessIdentity $Caller.pid $Caller.creationUtc $Caller.actualProcess)) {
        throw 'Exact source, live medium actor and native readiness are required.'
    }
    if (!$Caller.dotnetRoot -or $Caller.dotnetRoot -ine (Split-Path $Config.dotnet) -or
        $Caller.dotnetRootX64 -ine $Caller.dotnetRoot -or
        $Caller.resolvedDotnet -ine $Config.dotnet -or
        $Caller.dotnetVersion -cne $Config.dotnetVersion -or $Caller.dotnetExitCode -ne 0 -or
        @($Caller.path.Split(';') | Where-Object { $_ -ieq $Caller.dotnetRoot }).Count -ne 1) {
        throw 'Actual caller .NET root, PATH resolution and version must qualify before tracing.'
    }
    $observer = [IO.Path]::GetFullPath((Join-Path $Config.capsuleRoot 'observer-final\QuietLaneCaptureObserver.dll'))
    if ([IO.Path]::GetFullPath($Config.observerSource) -cne $observer) {
        throw 'Observer must occupy the actual wrapper capsule path.'
    }
    $files = @($Config.files | Sort-Object path)
    if (!$files.Count -or @($files | Where-Object path -CEQ $observer).Count -ne 1) {
        throw 'The exact capsule observer must have one input pin.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        if (!$seen.Add([IO.Path]::GetFullPath($file.path)) -or
            $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            (Get-FileHash -LiteralPath $file.path -ErrorAction Stop).Hash -ine $file.sha256) {
            throw 'Missing, duplicate or changed frozen input.'
        }
    }
    foreach ($path in @($Config.dotnet; $observer; $Config.requiredPaths)) {
        if (!$path -or !$seen.Contains([IO.Path]::GetFullPath($path))) {
            throw 'Every runtime, wrapper and native dependency must be pinned.'
        }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Config.capsuleRoot -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (!$entry.PSIsContainer -and !$seen.Contains($entry.FullName))) {
            throw 'Capsule layout contains an unpinned file or traversable link.'
        }
    }
    $runUrl = if ($Config.PSObject.Properties['runUrl']) { $Config.runUrl } else { $null }
    if ($null -ne $runUrl -and $runUrl -isnot [string]) { throw 'runUrl must be a string or null.' }
    # This private digest includes every configuration field, not an allowlist that can omit a new operand.
    $normalized = $Config | ConvertTo-Json -Depth 40 | ConvertFrom-Json
    $normalized | Add-Member -NotePropertyName runUrl -NotePropertyValue $runUrl -Force
    $value = [ordered]@{
        config = $normalized; files = $files
        dotnetRoot = $Caller.dotnetRoot; dotnetRootX64 = $Caller.dotnetRootX64
        path = $Caller.path; cwd = $Caller.cwd
        sid = $Caller.sid; session = $Caller.session; integrity = $Caller.integritySid
    } | ConvertTo-Json -Depth 45 -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))
    [pscustomobject]@{ digest = [Convert]::ToHexString($hash); runUrl = $runUrl }
}

function Assert-StartupDryRun($Receipt, [string] $Digest, [int] $TraceStartsCounter) {
    foreach ($name in @('completed','functionalContractsPassed','resourcesReaped',
        'controllerResourcesReaped','traceInstanceAbsent','observerExecutionBlocked')) {
        if (!$Receipt -or !$Receipt.PSObject.Properties[$name] -or $Receipt.$name -isnot [bool]) {
            throw 'Trace prohibited: incomplete or nonboolean dry-run status.'
        }
    }
    if ($TraceStartsCounter -ne 0 -or !$Receipt -or
        $Receipt.mode -cne 'Disabled' -or $Receipt.bindingHash -cne $Digest -or
        $Receipt.traceStartsCounter -ne 0 -or !$Receipt.completed -or
        !$Receipt.functionalContractsPassed -or !$Receipt.resourcesReaped -or
        !$Receipt.controllerResourcesReaped -or
        !$Receipt.traceInstanceAbsent -or $Receipt.observerExecutionBlocked -or
        @($Receipt.stages).Count -ne 3 -or
        ($Receipt.stages.name -join ',') -cne 'managed,sdk-four-controls,ownership-timeout-tree' -or
        @($Receipt.stages | Where-Object { $_.contractVerified -isnot [bool] -or
            $_.resourcesReaped -isnot [bool] -or !$_.contractVerified -or !$_.resourcesReaped }).Count) {
        throw 'Trace prohibited: the exact full wrapper dry-run must complete and reap every resource.'
    }
}

function Invoke-StartupProofRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)][StartupTraceAdapter] $Trace,
        [Parameter(Mandatory)][hashtable] $Operations,
        [Parameter(Mandatory)][string] $RunDirectory,
        $DryRunReceipt,
        [int] $TraceStartsCounter = 0
    )
    if (Test-Path -LiteralPath $RunDirectory) { throw 'Never overwrite an earlier dry-run or recording.' }
    $null = New-Item -ItemType Directory -Path $RunDirectory
    $stages = [Collections.Generic.List[object]]::new()
    $context = [pscustomobject]@{ config = $Config; output = $RunDirectory; bindingHash = $null }
    $entered = $false; $traceStopped = $false; $traceAbsent = $false
    $actualCounter = $TraceStartsCounter
    $errors = [Collections.Generic.List[string]]::new()
    $cleanup = $null; $binding = $null
    try {
        foreach ($key in @('Preflight','Enter','Stage','Cleanup')) {
            if (!$Operations.ContainsKey($key) -or $Operations[$key] -isnot [scriptblock]) {
                throw "Missing real wrapper operation: $key"
            }
        }
        $callerOutput = @(& $Operations.Preflight $context)
        if ($callerOutput.Count -ne 1) { throw 'Preflight must return one actual caller receipt, not mixed console output.' }
        $caller = $callerOutput[0]
        $binding = Get-StartupBinding $Config $caller
        $context.bindingHash = $binding.digest
        $context | Add-Member -NotePropertyName caller -NotePropertyValue $caller
        if ($Trace.Mode -eq [StartupTraceMode]::Record) {
            Assert-StartupDryRun $DryRunReceipt $binding.digest $TraceStartsCounter
        } elseif ($TraceStartsCounter -ne 0) { throw 'Dry-run cannot follow a trace attempt.' }
        & $Operations.Enter $context
        $entered = $true
        $ready = $Trace.Start($context)
        $expected = if ($Trace.Mode -eq [StartupTraceMode]::Record) { 1 } else { 0 }
        if ($ready.ready -isnot [bool] -or !$ready.ready -or $ready.traceStartsCounter -ne $expected) {
            throw 'Tracing adapter returned the wrong recording boundary.'
        }
        $actualCounter = $ready.traceStartsCounter
        foreach ($name in @('managed','sdk-four-controls','ownership-timeout-tree')) {
            $stage = & $Operations.Stage $context $name
            if (!$stage -or $stage.name -cne $name -or
                $stage.contractVerified -isnot [bool] -or $stage.resourcesReaped -isnot [bool] -or
                $stage.executionBlocked -isnot [bool]) {
                throw 'Stage lost its typed contract or actual resource finalization receipt.'
            }
            $stages.Add($stage)
            if (!$stage.resourcesReaped -or $stage.executionBlocked) {
                throw 'Unsafe stage finalization or blocking observation: no further controls.'
            }
        }
    } catch { $errors.Add($_.Exception.Message) }
    finally {
        try {
            if ($Operations.Cleanup) { $cleanup = & $Operations.Cleanup $context }
            if (!$cleanup -or $cleanup.resourcesReaped -isnot [bool] -or !$cleanup.resourcesReaped -or
                $cleanup.observerFailed -isnot [bool] -or $cleanup.executionBlocked -isnot [bool]) {
                throw 'Wrapper resource cleanup not verified.'
            }
            if ($binding -and (Get-StartupBinding $Config $caller).digest -cne $binding.digest) {
                throw 'Frozen artifacts or caller inputs changed during the wrapper.'
            }
        } catch { $errors.Add($_.Exception.Message) }
        try {
            $stopped = $Trace.Stop($context)
            if ($stopped.stopped -isnot [bool] -or $stopped.instanceAbsent -isnot [bool] -or
                ($actualCounter -eq 1 -and $stopped.traceStartsCounter -ne 1)) {
                throw 'Trace cleanup status is malformed or rolls back an actual attempt.'
            }
            $traceStopped = $stopped.stopped
            $traceAbsent = $stopped.instanceAbsent
            $actualCounter = $stopped.traceStartsCounter
            if (!$traceStopped -or !$traceAbsent) { throw 'Exact trace instance cleanup not verified.' }
        } catch { $errors.Add($_.Exception.Message) }
    }
    $functional = $stages.Count -eq 3 -and
        @($stages | Where-Object { !$_.contractVerified -or !$_.resourcesReaped -or $_.executionBlocked }).Count -eq 0
    $complete = $entered -and !$errors.Count -and $traceStopped -and $traceAbsent -and
        $null -ne $cleanup -and $cleanup.resourcesReaped
    $run = [ordered]@{
        completed = [bool]$complete; mode = [string]$Trace.Mode
        bindingHash = if ($binding) { $binding.digest } else { $null }
        sourceHead = $Config.sourceHead; runUrl = if ($binding) { $binding.runUrl } else { $null }
        functionalContractsPassed = [bool]$functional
        resourcesReaped = [bool]($cleanup -and $cleanup.resourcesReaped)
        observerFailed = [bool]($cleanup -and $cleanup.observerFailed)
        observerExecutionBlocked = [bool]($cleanup -and $cleanup.executionBlocked)
        attributionLimitationCount = if ($cleanup) { $cleanup.attributionLimitationCount } else { 0 }
        noDialogVerified = [bool]($complete -and $functional -and !$cleanup.observerFailed)
        traceInstanceAbsent = $traceAbsent
        traceStartsCounter = $actualCounter
        stages = @($stages.ToArray()); errors = @($errors.ToArray())
    }
    $path = Join-Path $RunDirectory 'run.json'
    $run | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$path.writing" -Encoding utf8NoBOM
    Move-Item -LiteralPath "$path.writing" -Destination $path
    # A passed dry-run includes the real final formatter and a fully parsed final receipt.
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
