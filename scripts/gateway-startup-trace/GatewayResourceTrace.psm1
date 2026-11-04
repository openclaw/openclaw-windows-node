Set-StrictMode -Version Latest
$script:DefaultSamplingDelayMilliseconds = 500

function Initialize-ResourceSampler {
    if (-not ('OpenClaw.Trace.ResourceSampler' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'ResourceSampler.cs')
    }
}

function Write-ResourceTraceConfiguration {
    param([string]$ControlDirectory, [string[]]$PayloadRoots = @(), [int[]]$ProcessIds = @(), [string]$BuildIdentityPath)
    $buildIdentity = [ordered]@{}
    $samplingDelay = $script:DefaultSamplingDelayMilliseconds
    if (-not [string]::IsNullOrWhiteSpace($env:OPENCLAW_TRACE_SAMPLE_INTERVAL_MS)) {
        if (-not [int]::TryParse($env:OPENCLAW_TRACE_SAMPLE_INTERVAL_MS, [ref]$samplingDelay) -or
            $samplingDelay -lt 100 -or $samplingDelay -gt 10000) {
            throw 'OPENCLAW_TRACE_SAMPLE_INTERVAL_MS must be an integer from 100 through 10000.'
        }
    }
    if ($BuildIdentityPath) {
        $identity = Get-Content -LiteralPath $BuildIdentityPath -Raw | ConvertFrom-Json
        foreach ($property in $identity.PSObject.Properties) {
            $pattern = switch -Regex ($property.Name) {
                '^(companion|gateway|local_model)_sha256$' { '^[0-9a-fA-F]{64}$'; break }
                '^(companion|gateway|payload)_commit$' { '^[0-9a-fA-F]{40}$'; break }
                '^(companion|gateway|payload|node)_version$' { '^[0-9]+(?:\.[0-9A-Za-z-]+){1,6}$'; break }
                default { throw 'Build identity contains an unsupported field. Only fixed version, commit and SHA256 fields are allowed.' }
            }
            if ($property.Value -isnot [string] -or $property.Value -notmatch $pattern) { throw 'Build identity contains an invalid version, commit or SHA256.' }
            $buildIdentity[$property.Name] = $property.Value
        }
    }
    $processes = @(foreach ($processId in @($ProcessIds | Sort-Object -Unique)) {
        $process = Get-Process -Id $processId -ErrorAction Stop
        try {
            [ordered]@{ id = $process.Id; started_utc_ticks = $process.StartTime.ToUniversalTime().ToFileTimeUtc() }
        } finally { $process.Dispose() }
    })
    $sources = @(
        Get-ChildItem -LiteralPath $PSScriptRoot -File
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Analyzer') -File
        Get-Item -LiteralPath (Join-Path $PSScriptRoot '..\Invoke-GatewayStartupTrace.ps1')
        Get-Item -LiteralPath (Join-Path $PSScriptRoot '..\Invoke-GatewaySetupTrace.ps1')
        Get-Item -LiteralPath (Join-Path $PSScriptRoot '..\Invoke-GatewayChatTrace.ps1')
        Get-Item -LiteralPath (Join-Path $PSScriptRoot '..\Write-GatewayChatTraceMarker.ps1')
    )
    $sourceHashes = ($sources | Sort-Object FullName | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ''
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceHashes)))
    [ordered]@{ payload_roots = @($PayloadRoots); processes = $processes; context = [ordered]@{
        tool_sources_sha256 = $digest
        os_version = [Environment]::OSVersion.Version.ToString()
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        logical_processors = [Environment]::ProcessorCount
        sampling_delay_ms = $samplingDelay
        artifact_identity = $buildIdentity
        artifact_identity_source = if ($buildIdentity.Count) { 'operator-supplied; not independently hashed during recording' } else { 'not-supplied' }
    } } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ControlDirectory 'resource-config.json') -Encoding utf8
}

function Get-ResourceWindowSummary {
    param([object[]]$Samples, [Int64]$StartTicks, [Int64]$EndTicks, [int[]]$ProcessIds = @())
    # Counter deltas only cover whole sample intervals inside this window. Never
    # proportionally assign a boundary-crossing CPU/I/O delta to an adjacent phase.
    $ordered = @($Samples)
    $lifetimes = @{}
    $allKeys = @($ordered | ForEach-Object {
        foreach ($process in $_.Processes) {
            if ($ProcessIds.Count -eq 0 -or $process.Id -in $ProcessIds) { "$($process.Id):$($process.StartedUtcTicks)" }
        }
    } | Sort-Object -Unique)
    $intervals = [Collections.Generic.List[object]]::new()
    $maximumGapMs = 0.0
    $hostCpuMs = 0.0
    $hostCapacityMs = 0.0
    $suspendOrClockGapMs = 0.0
    $clockAdjustmentMs = 0.0
    $availableBytes = $null
    foreach ($sample in $ordered) {
        if ($sample.UtcTicks -ge $StartTicks -and $sample.UtcTicks -le $EndTicks -and
            ($null -eq $availableBytes -or $sample.AvailableBytes -lt $availableBytes)) {
            $availableBytes = $sample.AvailableBytes
        }
        foreach ($process in $sample.Processes) {
            if ($process.ObservedUtcTicks -lt $StartTicks -or $process.ObservedUtcTicks -gt $EndTicks) { continue }
            if ($ProcessIds.Count -gt 0 -and $process.Id -notin $ProcessIds) { continue }
            $key = "$($process.Id):$($process.StartedUtcTicks)"
            if (-not $lifetimes.ContainsKey($key)) {
                $lifetimes[$key] = [ordered]@{
                    first = $process; last = $process; samples = 0
                    sampled_peak_working_set_bytes = 0L; sampled_peak_private_bytes = 0L
                }
            }
            $entry = $lifetimes[$key]
            $entry.last = $process
            $entry.samples++
            $entry.sampled_peak_working_set_bytes = [Math]::Max($entry.sampled_peak_working_set_bytes, $process.WorkingSetBytes)
            $entry.sampled_peak_private_bytes = [Math]::Max($entry.sampled_peak_private_bytes, $process.PrivateBytes)
        }
    }
    for ($i = 1; $i -lt $ordered.Count; $i++) {
        $previous = $ordered[$i - 1]
        $current = $ordered[$i]
        if ($previous.UtcTicks -lt $StartTicks -or $current.UtcTicks -gt $EndTicks) { continue }
        $wallMs = ($current.UtcTicks - $previous.UtcTicks) / 10000.0
        $elapsedMs = ($current.MonotonicTicks - $previous.MonotonicTicks) * 1000.0 / $current.MonotonicFrequency
        if ($elapsedMs -le 0) { continue }
        $clockAdjustmentMs += [Math]::Abs($wallMs - $elapsedMs)
        $maximumGapMs = [Math]::Max($maximumGapMs, $elapsedMs)
        $awakeMs = ($current.Awake100ns - $previous.Awake100ns) / 10000.0
        $suspendOrClockGapMs += [Math]::Max(0, $elapsedMs - $awakeMs)
        $capacity = (($current.Kernel100ns - $previous.Kernel100ns) +
            ($current.User100ns - $previous.User100ns)) / 10000.0
        $idle = ($current.Idle100ns - $previous.Idle100ns) / 10000.0
        if ($capacity -ge $idle -and $idle -ge 0) {
            $hostCapacityMs += $capacity
            $hostCpuMs += $capacity - $idle
        }
        $intervals.Add([pscustomobject]@{ Start = $previous.UtcTicks; End = $current.UtcTicks })
    }
    $index = 0
    $processes = @(foreach ($key in @($lifetimes.Keys | Sort-Object)) {
        $entry = $lifetimes[$key]
        $first = $entry.first
        $last = $entry.last
        $index = [Array]::IndexOf($allKeys, $key) + 1
        $hasDelta = $entry.samples -ge 2
        [ordered]@{
            process = "P$index"
            samples = $entry.samples
            cpu_user_ms = if ($hasDelta) { $last.CpuUserMs - $first.CpuUserMs } else { $null }
            cpu_kernel_ms = if ($hasDelta) { $last.CpuKernelMs - $first.CpuKernelMs } else { $null }
            sampled_peak_working_set_bytes = $entry.sampled_peak_working_set_bytes
            sampled_peak_private_bytes = $entry.sampled_peak_private_bytes
            logical_read_bytes = if ($hasDelta -and $null -ne $first.ReadBytes -and $null -ne $last.ReadBytes) { $last.ReadBytes - $first.ReadBytes } else { $null }
            logical_write_bytes = if ($hasDelta -and $null -ne $first.WriteBytes -and $null -ne $last.WriteBytes) { $last.WriteBytes - $first.WriteBytes } else { $null }
            logical_read_operations = if ($hasDelta -and $null -ne $first.ReadOperations -and $null -ne $last.ReadOperations) { $last.ReadOperations - $first.ReadOperations } else { $null }
            logical_write_operations = if ($hasDelta -and $null -ne $first.WriteOperations -and $null -ne $last.WriteOperations) { $last.WriteOperations - $first.WriteOperations } else { $null }
        }
    })
    [ordered]@{
        elapsed_ms = ($EndTicks - $StartTicks) / 10000.0
        sampled_intervals = $intervals.Count
        maximum_sample_gap_ms = $maximumGapMs
        non_awake_gap_ms = [Math]::Round($suspendOrClockGapMs, 1)
        clock_adjustment_magnitude_ms = [Math]::Round($clockAdjustmentMs, 1)
        host_cpu_fraction = if ($hostCapacityMs -gt 0) { [Math]::Round($hostCpuMs / $hostCapacityMs, 4) } else { $null }
        host_minimum_available_bytes = $availableBytes
        processes = $processes
        attribution = 'configured-processes-and-image-roots; split-by-native-creation-time'
        coverage = 'sampled-lower-bound; short-lived or inaccessible processes and boundary intervals may be absent'
    }
}

function Invoke-ResourceTraceAnalysis {
    param([string]$TracePath, [object[]]$Windows, [string[]]$PayloadRoots, [int[]]$ProcessIds = @())
    $directory = Split-Path -Parent $TracePath
    $samplesPath = Join-Path $directory 'resource-samples.jsonl'
    if (-not (Test-Path -LiteralPath $samplesPath -PathType Leaf)) {
        return [ordered]@{ status = 'not-recorded'; reason = 'resource-samples-missing'; windows = @(); etl = $null }
    }
    $samples = @(Get-Content -LiteralPath $samplesPath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $instances = @{}
    foreach ($sample in $samples) {
        foreach ($process in $sample.Processes) {
            if ($ProcessIds.Count -gt 0 -and $process.Id -notin $ProcessIds) { continue }
            $key = "$($process.Id):$($process.StartedUtcTicks)"
            if (-not $instances.ContainsKey($key)) {
                $instances[$key] = [ordered]@{ Id = $process.Id; StartedUtcTicks = $process.StartedUtcTicks; ObservedTicks = $process.ObservedUtcTicks }
            }
        }
    }
    $labels = @{}
    $sortedKeys = @($instances.Keys | Sort-Object)
    for ($i = 0; $i -lt $sortedKeys.Count; $i++) { $labels[$sortedKeys[$i]] = "P$($i + 1)" }
    $timelineOrigin = if ($samples.Count -gt 0) { $samples[0].UtcTicks } else { 0L }
    $timeline = @(foreach ($sample in $samples) {
        [ordered]@{
            offset_ms = ($sample.UtcTicks - $timelineOrigin) / 10000.0
            available_bytes = $sample.AvailableBytes
            processes = @(foreach ($process in $sample.Processes) {
                $key = "$($process.Id):$($process.StartedUtcTicks)"
                if (-not $labels.ContainsKey($key)) { continue }
                [ordered]@{
                    process = $labels[$key]
                    offset_ms = ($process.ObservedUtcTicks - $timelineOrigin) / 10000.0
                    working_set_bytes = $process.WorkingSetBytes
                    private_bytes = $process.PrivateBytes
                }
            })
        }
    })
    [ordered]@{ schema = 'openclaw-resource-timeline/v1'; samples = $timeline } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $directory 'resource-timeline.json') -Encoding utf8
    $validWindows = @($Windows | Where-Object { $_.EndTicks -gt $_.StartTicks })
    $requestPath = Join-Path $directory 'resource-analysis-request.json'
    [ordered]@{ Windows = $validWindows; Processes = @($sortedKeys | ForEach-Object { $instances[$_] }); PayloadRoots = @($PayloadRoots) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $requestPath -Encoding utf8
    $analyzer = Join-Path $PSScriptRoot 'Analyzer\bin\Debug\net10.0\GatewayTraceAnalyzer.dll'
    $etlPath = Join-Path $directory 'resource-etl-summary.json'
    $etl = $null
    $etlStatus = if ($validWindows.Count -eq 0) { 'not-analyzed-no-valid-windows' } else { 'unavailable-build-analyzer' }
    if ((Test-Path -LiteralPath $analyzer -PathType Leaf) -and $validWindows.Count -gt 0) {
        & dotnet $analyzer $TracePath $requestPath $etlPath
        $analyzerExit = $LASTEXITCODE
        if ($analyzerExit -notin @(0, 2)) { throw "Resource ETL analysis failed (exit $analyzerExit)." }
        $etl = Get-Content -LiteralPath $etlPath -Raw | ConvertFrom-Json
        $etlStatus = if ($analyzerExit -eq 0) { 'parsed-event-coverage-unverified' } else { 'incomplete-events-lost' }
    } elseif ($validWindows.Count -gt 0) {
        Write-Warning 'Resource ETL analysis unavailable. Build scripts\gateway-startup-trace\Analyzer\GatewayTraceAnalyzer.csproj and rerun the summary.'
    }
    $statusPath = Join-Path $directory 'control\recorder-status.json'
    $recorderStatus = if (Test-Path -LiteralPath $statusPath -PathType Leaf) { Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } else { $null }
    [ordered]@{
        status = if ($samples.Count -gt 1) { 'sampled' } else { 'insufficient-samples' }
        etl_status = $etlStatus
        sample_count = $samples.Count
        timeline = 'resource-timeline.json'
        sampler_maximum_capture_ms = if ($recorderStatus -and $recorderStatus.PSObject.Properties['resource_maximum_capture_ms']) { $recorderStatus.resource_maximum_capture_ms } else { $null }
        context = (Get-Content -LiteralPath (Join-Path $directory 'control\resource-config.json') -Raw | ConvertFrom-Json).context
        inaccessible_or_exited_process_observations = ($samples | Measure-Object UnavailableProcesses -Sum).Sum
        selected_process_lifetimes = $instances.Count
        windows = @(foreach ($window in $validWindows) {
            [ordered]@{
                phase = $window.Name
                resources = Get-ResourceWindowSummary -Samples $samples -StartTicks $window.StartTicks -EndTicks $window.EndTicks -ProcessIds $ProcessIds
            }
        })
        etl = $etl
        interpretation = 'CPU, logical I/O, physical disk and Defender scan activity overlap. None is an exclusive wall-clock contribution. Memory peaks are sampled, not lifetime peaks. Process I/O includes non-disk I/O. Empty process coverage can mean MXC guest visibility is unavailable.'
    }
}

function Get-ChatTimingSummary {
    param([object[]]$Markers)
    $allowed = @('send', 'first-response', 'completed', 'failed', 'cancelled')
    $byName = @{}
    $previousTicks = 0L
    foreach ($marker in $Markers) {
        if ($marker.name -notin $allowed -or $byName.ContainsKey($marker.name) -or
            [Int64]$marker.ticks -lt $previousTicks) { throw 'Chat markers are invalid, duplicated or out of order.' }
        $byName[$marker.name] = [Int64]$marker.ticks
        $previousTicks = [Int64]$marker.ticks
    }
    $terminalNames = @($allowed | Where-Object { $_ -in @('completed', 'failed', 'cancelled') -and $byName.ContainsKey($_) })
    if (-not $byName.ContainsKey('send') -or $terminalNames.Count -gt 1 -or $Markers[0].name -ne 'send') {
        throw 'Chat requires send first and at most one terminal marker.'
    }
    if ($terminalNames.Count -eq 1 -and $Markers[-1].name -ne $terminalNames[0]) {
        throw 'A chat terminal marker must be last.'
    }
    [ordered]@{
        source = 'operator-observed; includes human reaction and marker-command scheduling; polling affects recording stop only'
        outcome = if ($terminalNames.Count -eq 1) { $terminalNames[0] } else { 'incomplete' }
        observed_time_to_first_response_ms = if ($byName.ContainsKey('first-response')) { ($byName['first-response'] - $byName.send) / 10000.0 } else { $null }
        observed_send_to_terminal_ms = if ($terminalNames.Count -eq 1) { ($byName[$terminalNames[0]] - $byName.send) / 10000.0 } else { $null }
        server_time_to_first_token_ms = $null
    }
}

function Get-ChatTraceMarkers {
    param([string]$ControlDirectory, [long]$StartTicks, [long]$EndTicks)
    $markers = @(foreach ($name in @('send', 'first-response', 'completed', 'failed', 'cancelled')) {
        $path = Join-Path $ControlDirectory "chat-marker-$name.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($value.name -ne $name -or [long]$value.ticks -lt $StartTicks -or [long]$value.ticks -gt $EndTicks) {
            throw 'Chat marker name or recording boundary is invalid.'
        }
        [pscustomobject]@{ name = $name; ticks = [long]$value.ticks }
    })
    $markers | Sort-Object ticks
}

Export-ModuleMember -Function Initialize-ResourceSampler, Write-ResourceTraceConfiguration, Get-ResourceWindowSummary, Get-ChatTimingSummary, Invoke-ResourceTraceAnalysis, Get-ChatTraceMarkers
