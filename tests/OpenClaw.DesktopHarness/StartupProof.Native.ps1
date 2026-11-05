function ConvertFrom-StartupReceiptJson {
    param([AllowNull()][AllowEmptyString()][string] $Text)
    $parsed = $Text | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
    if ($parsed -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'Receipt must contain one completed JSON object.'
    }
    $parsed
}

function Wait-StartupReceipt([string] $Path, [int] $Seconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $directory = [IO.Path]::GetFullPath((Split-Path $Path))
    while (!(Test-Path -LiteralPath $directory -PathType Container)) {
        $parent = Split-Path $directory
        if (!$parent -or $parent -eq $directory) { throw 'Receipt has no existing ancestor directory.' }
        $directory = $parent
    }
    $watcher = [IO.FileSystemWatcher]::new($directory)
    $watcher.IncludeSubdirectories = $true
    try {
        while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
            if (Test-Path -LiteralPath $Path) {
                try { return (ConvertFrom-StartupReceiptJson ([IO.File]::ReadAllText($Path))) }
                catch { }
            }
            $null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All,100)
        }
        throw "Bounded receipt wait expired: $([IO.Path]::GetFileName($Path))"
    } finally { $watcher.Dispose() }
}

function New-StartupNativeOperations {
    @{
        Preflight = {
            param($ctx)
            $c = $ctx.config
            if ($env:COMPUTERNAME -cne $c.computer -or $c.leaseId -notmatch '^cbx_[0-9a-f]+$') {
                throw 'Exact fresh authorized guest required.'
            }
            $manifest = Get-Content -LiteralPath $c.manifest -Raw | ConvertFrom-Json
            if ($manifest.pr -ne 1791 -or $manifest.sdkVersion -cne '1.0.0' -or
                $manifest.leaseId -cne $c.leaseId -or $manifest.expectedHead -cne $c.sourceHead -or
                ($manifest.stages.name -join ',') -cne 'managed,sdk-four-controls,ownership-timeout-tree') {
                throw 'Full pinned 1791 source and stage manifest required.'
            }
            $head = & git -C $manifest.repository rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or $head -cne $c.sourceHead) { throw 'Source HEAD mismatch.' }
            $dirty = & git -C $manifest.repository status --porcelain
            if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Source must be clean.' }
            $required = @($c.manifest, $c.harness, $c.dotnet, $manifest.powerShell,
                $manifest.jobSource, $manifest.scopeApprovalReceipt, $c.observerSource,
                $manifest.inputs.nativeRuntime, $manifest.inputs.nativeProbe, $manifest.inputs.nativeInitFailure,
                $c.wpr, $c.profile, $c.powerShell)
            foreach ($file in @('StartupProof.ps1','StartupProof.Native.ps1','Invoke-StartupProof.ps1',
                'Invoke-StartupRecorder.ps1','Invoke-StartupProofGuest.ps1')) {
                $required += Join-Path $PSScriptRoot $file
            }
            $required += @($manifest.stages.script) + @($manifest.supportScripts.path) + @($manifest.artifacts.path)
            $required += @(Join-Path (Split-Path $c.harness) 'Microsoft.Mxc.Sdk.dll')
            foreach ($path in $required) {
                if (@($c.files | Where-Object { $_.path -ieq $path }).Count -ne 1) {
                    throw 'Unpinned native wrapper operand.'
                }
            }
            foreach ($stage in $manifest.stages) {
                if ($stage.deadlineSeconds -lt 1 -or $stage.deadlineSeconds -gt 900 -or
                    (Get-FileHash -LiteralPath $stage.script).Hash -ine $stage.sha256) {
                    throw 'Original stage script and deadline must qualify.'
                }
            }
            foreach ($item in $manifest.supportScripts) {
                if ((Get-FileHash -LiteralPath $item.path).Hash -ine $item.sha256) { throw 'Support pin mismatch.' }
            }
            if ((Get-FileHash -LiteralPath $manifest.jobSource).Hash -ine $manifest.jobSha256 -or
                (Get-FileHash -LiteralPath $manifest.inputs.nativeRuntime).Hash -ine $manifest.inputs.nativeRuntimeSha256 -or
                (Get-FileHash -LiteralPath $manifest.inputs.nativeProbe).Hash -ine $manifest.inputs.nativeProbeSha256 -or
                (Get-FileHash -LiteralPath $manifest.inputs.nativeInitFailure).Hash -ine $manifest.inputs.nativeInitFailureSha256) {
                throw 'Native job or SDK fixture provenance mismatch.'
            }
            $env:DOTNET_ROOT = Split-Path $c.dotnet
            $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
            $env:PATH = $c.callerPath
            Add-Type -Path $c.observerSource
            Add-Type -Path $manifest.jobSource
            $token = [ObserverTokenEvidence]::Capture()
            $nativePath = Join-Path $ctx.output 'caller-native.json'
            & $c.harness --caller-preflight $manifest.inputs.nativeRuntime $nativePath *> (Join-Path $ctx.output 'caller-native-private.log')
            if ($LASTEXITCODE -ne 0) { throw 'Actual native caller preflight failed.' }
            $native = Get-Content -LiteralPath $nativePath -Raw | ConvertFrom-Json
            if (!$native.completed -or $native.sourceVersion -cnotlike "*+$($c.sourceHead)" -or
                $native.nativeSha256 -ine $manifest.inputs.nativeRuntimeSha256 -or
                $native.sdkSha256 -ine $manifest.inputs.sdkManagedSha256 -or
                $native.context.station -cne 'WinSta0' -or $native.context.desktop -cne 'Default' -or
                $native.inputDesktop -cne 'Default') { throw 'Source/native/desktop caller mismatch.' }
            $resolved = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
            $version = & $c.dotnet --version
            $versionExit = $LASTEXITCODE
            $self = [Diagnostics.Process]::GetCurrentProcess()
            $caller = [pscustomobject]@{
                completed = $true; pid = $PID; creationUtc = $self.StartTime.ToUniversalTime().ToString('O')
                actualProcess = $self; sourceHead = $head; sid = $native.context.userSid
                session = $self.SessionId; integritySid = $token['processToken']['integritySid']
                threadHasNoToken = $token['threadHasNoToken']; nativeLibraryPrimed = $native.completed
                dotnetRoot = $env:DOTNET_ROOT; dotnetRootX64 = $env:DOTNET_ROOT_X64
                resolvedDotnet = $resolved; dotnetVersion = $version; dotnetExitCode = $versionExit
                path = $env:PATH; cwd = [Environment]::CurrentDirectory
            }
            $ctx | Add-Member -NotePropertyName state -NotePropertyValue @{
                manifest = $manifest; observer = $null; stop = $null
                observations = [Collections.Generic.List[object]]::new()
                baseline = @([NativeProofJob]::Desktops())
            }
            $manifest | Add-Member -NotePropertyName output -NotePropertyValue $ctx.output -Force
            $manifest | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $ctx.output 'manifest.json') -Encoding utf8NoBOM
            $caller
        }
        Enter = {
            param($ctx)
            $s = $ctx.state
            $s.stop = [Threading.ManualResetEvent]::new($false)
            $s.observer = [QuietLaneCaptureObserver]::new($s.stop,100,2000,$true,$true)
            $s.observer.Start()
            if (!$s.observer.Ready.WaitOne(10000) -or $s.observer.ExecutionBlocked) {
                throw 'Observer failed to qualify on the original medium desktop.'
            }
        }
        Stage = {
            param($ctx,$name)
            $s = $ctx.state; $m = $s.manifest
            $spec = @($m.stages | Where-Object name -CEQ $name)[0]
            $job = [NativeProofJob]::new(); $p = $null; $errorText = $null; $jobEmpty = $false
            $saved = $env:OPENCLAW_NATIVE_PROOF_SUPERVISOR
            $members = [Collections.Generic.Dictionary[long,object]]::new()
            $rootPid = $null; $rootCreationUtc = $null
            try {
                $env:OPENCLAW_NATIVE_PROOF_SUPERVISOR = "$PID|$($m.leaseId)|$env:COMPUTERNAME"
                $stageArguments = @('-NoLogo','-NoProfile','-NonInteractive','-File',$spec.script,
                    '-Manifest',(Join-Path $ctx.output 'manifest.json'),'-EvidenceDirectory',$ctx.output,'-Stage',$name)
                $p = $job.Start($m.powerShell,[string[]]$stageArguments,$m.repository)
                $rootPid = $p.Id; $rootCreationUtc = $p.StartTime.ToUniversalTime().ToString('O')
                $s.observer.TrackProcess([uint32]$p.Id,$p.StartTime.ToUniversalTime().ToString('O'))
                $watch = [Diagnostics.Stopwatch]::StartNew()
                while (!$p.WaitForExit(100)) {
                    foreach ($pidValue in $job.Pids()) {
                        $identity = $job.Identity($pidValue)
                        if (!$members.ContainsKey($pidValue)) { $members.Add($pidValue,$identity) }
                        if ($identity.StartedUtc) { $s.observer.TrackProcess([uint32]$pidValue,$identity.StartedUtc) }
                    }
                    foreach ($desktop in [NativeProofJob]::Desktops()) {
                        if ($desktop -notin $s.baseline) { $s.observer.TrackDesktop($desktop,$false) }
                    }
                    $record = $null
                    while ($s.observer.Records.TryDequeue([ref]$record)) { $s.observations.Add($record); $record = $null }
                    if ($s.observer.ExecutionBlocked -or $watch.Elapsed.TotalSeconds -ge $spec.deadlineSeconds) {
                        throw 'Stage observation or original deadline blocked execution.'
                    }
                }
                if ($p.ExitCode -ne 0) { throw "Functional stage exit $($p.ExitCode)." }
                $contract = Get-Content (Join-Path $ctx.output "$name.contract.json") -Raw | ConvertFrom-Json
                if (!$contract.contractVerified) { throw 'Actual full functional contract not verified.' }
                if (@($job.Pids()).Count) { throw 'Unexpected surviving stage descendants.' }
            } catch { $errorText = $_.Exception.Message }
            finally {
                $env:OPENCLAW_NATIVE_PROOF_SUPERVISOR = $saved
                try {
                    if (@($job.Pids()).Count) { $job.Stop() }
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    do {
                        $jobEmpty = @($job.Pids()).Count -eq 0
                        if ($jobEmpty) { break }
                        $null = $s.observer.Changed.WaitOne(50)
                    } while ($clock.Elapsed.TotalSeconds -lt 10)
                } finally { if ($p) { $p.Dispose() }; $job.Dispose() }
            }
            $desktopsGone = @([NativeProofJob]::Desktops() | Where-Object { $_ -notin $s.baseline }).Count -eq 0
            $result = [pscustomobject]@{
                name = $name; contractVerified = [bool](!$errorText -and $jobEmpty -and $desktopsGone)
                resourcesReaped = [bool]($jobEmpty -and $desktopsGone)
                executionBlocked = $s.observer.ExecutionBlocked; error = $errorText
                rootPid = $rootPid; rootCreationUtc = $rootCreationUtc; memberIdentities = @($members.Values)
            }
            $result | ConvertTo-Json | Set-Content (Join-Path $ctx.output "$name.finalized.json")
            $result
        }
        Cleanup = {
            param($ctx)
            if (!$ctx.PSObject.Properties['state']) {
                return @{ resourcesReaped = $true; observerFailed = $false; executionBlocked = $false; attributionLimitationCount = 0 }
            }
            $s = $ctx.state; $stopped = $true
            try {
                if ($s.observer) {
                    $stopped = $s.observer.Stop(10000)
                    $record = $null
                    while ($s.observer.Records.TryDequeue([ref]$record)) { $s.observations.Add($record); $record = $null }
                    ConvertTo-Json -InputObject @($s.observations.ToArray()) -Depth 14 |
                        Set-Content (Join-Path $ctx.output 'passive-window-observer.json')
                }
                @{
                    resourcesReaped = [bool]($stopped -and
                        @([NativeProofJob]::Desktops() | Where-Object { $_ -notin $s.baseline }).Count -eq 0)
                    observerFailed = [bool]($s.observer -and $s.observer.Failed)
                    executionBlocked = [bool]($s.observer -and $s.observer.ExecutionBlocked)
                    attributionLimitationCount = if ($s.observer) { $s.observer.AttributionLimitations } else { 0 }
                }
            } finally {
                if ($s.observer) { $s.observer.Dispose() }
                if ($s.stop) { $s.stop.Dispose() }
            }
        }
    }
}
