<#
.SYNOPSIS
    Sweeps llama-server preset overrides for the installed Local AI model and reports the fastest one.

.DESCRIPTION
    Reads the preset the tray last generated (llama-server-models.ini, which already includes any
    current recipe-overrides.ini), then launches the installed llama-server once per candidate
    override set on a private loopback port. Each candidate gets a warm-up request (excluded from
    results) and then every prompt is sent -Repetitions times with prompt caching disabled.

    Results go to results.csv in -OutputDirectory. The winner is the candidate with the highest
    median generation tokens/s whose median prefill tokens/s is at least 90% of the baseline's.
    When the winner is not the baseline, best-overrides.ini is written with only the keys that
    differ from the baseline. Merge those keys into the [<model-id>] section of
    <LocalAiRoot>\recipe-overrides.ini, replacing any value that section already sets for them.

    Stop Local AI on the Local AI page before running. The script refuses to run while the managed
    llama-server is running or while -Port is in use.

.PARAMETER LocalAiRoot
    Local AI data directory. Defaults to the release build's %LOCALAPPDATA%\OpenClawTray\LocalAI.
    Dev builds use %LOCALAPPDATA%\OpenClawTray-Dev\LocalAI.

.PARAMETER GridPath
    Optional JSON object mapping preset keys to candidate string values, for example
    { "ubatch-size": ["512", "1024"], "spec-draft-n-max": ["2", "4"] }. Candidates are the cartesian
    product. Defaults to batch-size 2048/4096 x ubatch-size 512/1024/2048, plus spec-draft-n-max
    2..5 when the baseline uses speculative decoding. Combinations where ubatch-size exceeds
    batch-size, or that match the baseline, are skipped. An empty value removes the key. Keys
    that recipe-overrides.ini rejects are rejected here too.

.PARAMETER PromptsPath
    Optional JSON array of prompts: [{ "name": "...", "content": "...", "maxTokens": 512 }].
    The first prompt is the generation prompt (ranked by generation tokens/s) and the last prompt
    is the prefill prompt (ranked by prompt tokens/s). Defaults to a coding generation prompt and
    a long-context prefill prompt.

.PARAMETER Port
    Loopback port for the benchmark llama-server. Must be free.

.PARAMETER Repetitions
    Measured requests per prompt per candidate.

.PARAMETER OutputDirectory
    Where candidate presets, logs, VRAM samples, results.csv, and best-overrides.ini are written.
    Defaults to <LocalAiRoot>\logs\bench-<timestamp>.
#>
[CmdletBinding()]
param(
    [string]$LocalAiRoot = (Join-Path $env:LOCALAPPDATA "OpenClawTray\LocalAI"),

    [string]$GridPath,

    [string]$PromptsPath,

    [ValidateRange(1, 65535)]
    [int]$Port = 28799,

    [ValidateRange(1, 100)]
    [int]$Repetitions = 3,

    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$invariant = [Globalization.CultureInfo]::InvariantCulture
# Mirrors LocalAiRecipeOverrides (src/OpenClaw.Connection/LocalAi/LocalAiRecipeOverrides.cs), the
# source of truth, so a grid cannot recommend a key the tray refuses at startup. It is an
# allowlist of canonical long llama-server option names: llama.cpp registers aliases beyond the
# two-character ones (-ag is --agent, -hft is --hf-token) and its preset parser resolves every
# alias, so name-based deny rules can be bypassed.
$allowedOverrideKeys = @(
    "threads", "threads-batch", "cpu-mask", "cpu-range", "cpu-strict", "prio", "poll",
    "batch-size", "ubatch-size",
    "flash-attn", "swa-full", "cache-type-k", "cache-type-v",
    "cache-reuse", "cache-idle-slots", "context-shift", "kv-offload", "repack",
    "cont-batching", "cache-prompt", "defrag-thold",
    "spec-type", "spec-draft-n-max", "spec-draft-backend-sampling",
    "temp", "top-k", "top-p", "min-p", "typical", "xtc-threshold", "xtc-probability",
    "repeat-penalty", "presence-penalty", "frequency-penalty",
    "dry-multiplier", "dry-base", "dry-allowed-length", "dry-penalty-last-n",
    "samplers", "dynatemp-range",
    "reasoning", "reasoning-budget", "reasoning-format", "reasoning-preserve",
    "rope-scaling", "rope-scale", "rope-freq-base", "rope-freq-scale",
    "yarn-orig-ctx", "yarn-ext-factor", "yarn-attn-factor", "yarn-beta-fast", "yarn-beta-slow",
    "gpu-layers", "main-gpu", "tensor-split", "split-mode")
$negatableOverrideKeys = @(
    "cache-idle-slots", "context-shift", "kv-offload", "repack", "cont-batching",
    "cache-prompt", "flash-attn", "swa-full", "cache-reuse", "reasoning",
    "reasoning-preserve", "spec-draft-backend-sampling")

function Assert-OverridableKey {
    param([string]$Key)
    if ($Key -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw "The grid key '$Key' is not a llama-server option name." }
    $option = $Key
    $negated = $false
    if ($option.StartsWith("no-", [StringComparison]::Ordinal)) { $option = $option.Substring(3); $negated = $true }
    if ($allowedOverrideKeys -cnotcontains $option) { throw "The grid key '$Key' is not an allowed llama-server tuning option." }
    if ($negated -and ($negatableOverrideKeys -cnotcontains $option)) { throw "The grid key '$Key' cannot be negated. Write '$option = true|false' instead." }
}

function Test-ChangesBaseline {
    # An empty value removes the key, matching recipe-overrides.ini.
    param($Baseline, $Override)
    $current = Find-EntryValue $Baseline.Entries $Override.Key
    if ($Override.Value -eq "") { return $null -ne $current }
    return $current -cne $Override.Value
}

function Get-OptionalProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Read-BaselinePreset {
    param([string]$Path)
    $modelId = $null
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($rawLine in [IO.File]::ReadAllLines($Path)) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#") -or $line.StartsWith(";")) { continue }
        if ($line.StartsWith("[") -and $line.EndsWith("]")) {
            if ($null -ne $modelId) { break }
            $modelId = $line.Substring(1, $line.Length - 2).Trim()
            continue
        }
        if ($null -eq $modelId) { continue }
        $separator = $line.IndexOf("=")
        if ($separator -lt 0) { continue }
        $entries.Add([pscustomobject]@{
            Key   = $line.Substring(0, $separator).Trim()
            Value = $line.Substring($separator + 1).Trim()
        })
    }
    if ([string]::IsNullOrWhiteSpace($modelId)) {
        throw "llama-server-models.ini does not contain a [model-id] section."
    }
    return [pscustomobject]@{ ModelId = $modelId; Entries = $entries }
}

function Find-EntryValue {
    param($Entries, [string]$Key)
    foreach ($entry in $Entries) {
        if ($entry.Key -ceq $Key) { return $entry.Value }
    }
    return $null
}

function Merge-Entries {
    # Same semantics as the tray: an empty value removes the key, an existing key keeps its
    # position with the new value, and a new key is appended.
    param($Baseline, $Overrides)
    $merged = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $Baseline) { $merged.Add([pscustomobject]@{ Key = $entry.Key; Value = $entry.Value }) }
    foreach ($override in $Overrides) {
        $existing = $null
        foreach ($entry in $merged) { if ($entry.Key -ceq $override.Key) { $existing = $entry; break } }
        if ($override.Value -eq "") { if ($null -ne $existing) { [void]$merged.Remove($existing) } }
        elseif ($null -ne $existing) { $existing.Value = $override.Value }
        else { $merged.Add([pscustomobject]@{ Key = $override.Key; Value = $override.Value }) }
    }
    return $merged
}

function Get-Median {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2
}

function Format-Number {
    param($Value)
    if ($null -eq $Value) { return "" }
    return ([double]$Value).ToString("0.##", $invariant)
}

function Read-Grid {
    param([string]$Path, $Baseline)
    $grid = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $grid.Add([pscustomobject]@{ Key = "batch-size"; Values = @("2048", "4096") })
        $grid.Add([pscustomobject]@{ Key = "ubatch-size"; Values = @("512", "1024", "2048") })
        if ($null -ne (Find-EntryValue $Baseline.Entries "spec-type")) {
            $grid.Add([pscustomobject]@{ Key = "spec-draft-n-max"; Values = @("2", "3", "4", "5") })
        }
        return $grid
    }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($property in $json.PSObject.Properties) {
        $key = [string]$property.Name
        Assert-OverridableKey $key
        $values = @($property.Value | ForEach-Object { ([string]$_).Trim() })
        if ($values.Count -eq 0) { throw "The grid key '$key' has no values." }
        foreach ($value in $values) {
            if ($value -match '\p{Cc}') { throw "A value for the grid key '$key' contains a control character." }
        }
        $grid.Add([pscustomobject]@{ Key = $key; Values = $values })
    }
    if ($grid.Count -eq 0) { throw "The grid file does not contain any keys." }
    return $grid
}

function Read-Prompts {
    param([string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        # Windows PowerShell 5.1 emits a parsed JSON array as one object; piping unrolls it.
        $parsed = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $prompts = @($parsed | ForEach-Object { $_ })
        if ($prompts.Count -eq 0) { throw "The prompts file does not contain any prompts." }
        foreach ($prompt in $prompts) {
            if ([string]::IsNullOrWhiteSpace((Get-OptionalProperty $prompt "name")) -or
                [string]::IsNullOrWhiteSpace((Get-OptionalProperty $prompt "content")) -or
                $null -eq (Get-OptionalProperty $prompt "maxTokens")) {
                throw "Every prompt needs name, content, and maxTokens."
            }
        }
        return $prompts
    }

    $paragraph = @"
The city archive keeps maintenance records for every bridge, tunnel, and pumping station built since the
river was first channeled. Each record lists the inspection date, the crew that performed it, the parts
that were replaced, and the measurements taken at fixed reference points. Engineers use the records to
predict which structures need attention before the spring floods, and auditors use them to check that
contracts were fulfilled. Over the decades the format changed several times, so older entries mix units
and abbreviations that newer staff often misread.
"@
    $builder = New-Object System.Text.StringBuilder
    while ($builder.Length -lt 24000) { [void]$builder.AppendLine($paragraph) }
    [void]$builder.AppendLine("Summarize the text above in three sentences.")

    return @(
        [pscustomobject]@{
            name      = "generation"
            content   = "Implement an LRU cache in C# with unit tests and explain each design choice."
            maxTokens = 512
        },
        [pscustomobject]@{
            name      = "prefill"
            content   = $builder.ToString()
            maxTokens = 128
        }
    )
}

function New-Candidates {
    param($Grid, $Baseline)
    $combos = New-Object System.Collections.Generic.List[object]
    $combos.Add(@())
    foreach ($dimension in $Grid) {
        $next = New-Object System.Collections.Generic.List[object]
        foreach ($combo in $combos) {
            foreach ($value in $dimension.Values) {
                $next.Add(@($combo) + @([pscustomobject]@{ Key = $dimension.Key; Value = $value }))
            }
        }
        $combos = $next
    }

    $candidates = New-Object System.Collections.Generic.List[object]
    $candidates.Add([pscustomobject]@{ Name = "baseline"; Overrides = @() })
    $index = 0
    foreach ($combo in $combos) {
        # A combination equal to the baseline would only re-measure it and could "win" on noise.
        $differs = $false
        foreach ($entry in $combo) {
            if (Test-ChangesBaseline $Baseline $entry) { $differs = $true; break }
        }
        if (-not $differs) { continue }
        $batch = Find-EntryValue $combo "batch-size"
        if ($null -eq $batch) { $batch = Find-EntryValue $Baseline.Entries "batch-size" }
        $ubatch = Find-EntryValue $combo "ubatch-size"
        if ($null -eq $ubatch) { $ubatch = Find-EntryValue $Baseline.Entries "ubatch-size" }
        $batchNumber = 0
        $ubatchNumber = 0
        if ([int]::TryParse([string]$batch, [ref]$batchNumber) -and
            [int]::TryParse([string]$ubatch, [ref]$ubatchNumber) -and
            $ubatchNumber -gt $batchNumber) {
            continue
        }
        $index++
        $candidates.Add([pscustomobject]@{ Name = ("c{0:D2}" -f $index); Overrides = @($combo) })
    }
    return $candidates
}

function Write-CandidatePreset {
    param([string]$Path, [string]$ModelId, $Entries)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine("version = 1")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("[$ModelId]")
    foreach ($entry in $Entries) { [void]$builder.AppendLine("$($entry.Key) = $($entry.Value)") }
    [IO.File]::WriteAllText($Path, $builder.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

function Test-ServerReady {
    param([string]$BaseUrl)
    foreach ($path in @("/health", "/v1/models")) {
        try {
            $response = Invoke-WebRequest -Uri ($BaseUrl + $path) -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return $true }
        }
        catch {
        }
    }
    return $false
}

function Invoke-ChatCompletion {
    param([string]$BaseUrl, [string]$ModelId, [string]$Content, [int]$MaxTokens, [bool]$Measured)
    $body = [ordered]@{
        model      = $ModelId
        messages   = @(@{ role = "user"; content = $Content })
        max_tokens = $MaxTokens
        stream     = $false
    }
    if ($Measured) {
        $body["cache_prompt"] = $false
        $body["seed"] = 42
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6 -Compress))
    return Invoke-RestMethod -Method Post -Uri ($BaseUrl + "/v1/chat/completions") `
        -ContentType "application/json; charset=utf-8" -Body $bytes -TimeoutSec 600
}

function Get-TokensPerSecond {
    param($Timings, $Usage, [string]$TimingCountName, [string]$UsageCountName, [string]$MillisecondsName)
    $milliseconds = Get-OptionalProperty $Timings $MillisecondsName
    $count = Get-OptionalProperty $Timings $TimingCountName
    if ($null -eq $count) { $count = Get-OptionalProperty $Usage $UsageCountName }
    if ($null -eq $milliseconds -or $null -eq $count -or [double]$milliseconds -le 0) { return $null }
    return [double]$count / [double]$milliseconds * 1000
}

function Get-PeakVram {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $peak = $null
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $value = 0
        if ([int]::TryParse($line.Trim(), [ref]$value)) {
            if ($null -eq $peak -or $value -gt $peak) { $peak = $value }
        }
    }
    return $peak
}

function New-ResultRow {
    param($Candidate, [string]$Prompt, $Repetition, [string]$ErrorText)
    return [pscustomobject][ordered]@{
        candidate         = $Candidate.Name
        overrides         = (@($Candidate.Overrides | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ";")
        prompt            = $Prompt
        repetition        = $Repetition
        prompt_tokens     = ""
        prompt_tps        = ""
        generated_tokens  = ""
        gen_tps           = ""
        draft_n           = ""
        draft_accepted    = ""
        draft_accept_rate = ""
        peak_vram_mib     = ""
        error             = $ErrorText
    }
}

function Stop-ProcessQuietly {
    param($Process, [int]$TimeoutSeconds)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            [void]$Process.WaitForExit($TimeoutSeconds * 1000)
        }
    }
    catch {
    }
}

function Invoke-Candidate {
    param($Candidate, $Baseline, $Prompts, [string]$Executable, [string]$GpuId, [string]$NvidiaSmi)
    $rows = New-Object System.Collections.Generic.List[object]
    $presetPath = Join-Path $OutputDirectory "$($Candidate.Name).ini"
    $vramPath = Join-Path $OutputDirectory "$($Candidate.Name).vram.txt"
    Write-CandidatePreset $presetPath $Baseline.ModelId (Merge-Entries $Baseline.Entries $Candidate.Overrides)

    $baseUrl = "http://127.0.0.1:$Port"
    $server = $null
    $monitor = $null
    try {
        if ($null -ne $NvidiaSmi) {
            $monitor = Start-Process -FilePath $NvidiaSmi -PassThru -NoNewWindow `
                -ArgumentList "--query-gpu=memory.used --format=csv,noheader,nounits -i $GpuId -lms 500" `
                -RedirectStandardOutput $vramPath `
                -RedirectStandardError (Join-Path $OutputDirectory "$($Candidate.Name).vram.err.txt")
        }

        $arguments = "--host 127.0.0.1 --port $Port --models-preset `"$presetPath`" --models-max 1 " +
            "--models-autoload --no-webui --metrics --offline --log-verbosity 4 --no-log-prefix --no-log-timestamps"
        $server = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -NoNewWindow `
            -WorkingDirectory (Split-Path -Parent $Executable) `
            -RedirectStandardOutput (Join-Path $OutputDirectory "$($Candidate.Name).stdout.log") `
            -RedirectStandardError (Join-Path $OutputDirectory "$($Candidate.Name).stderr.log")

        $deadline = (Get-Date).AddSeconds(120)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            if ($server.HasExited) { break }
            if (Test-ServerReady $baseUrl) { $ready = $true; break }
            Start-Sleep -Seconds 1
        }
        if (-not $ready) {
            $reason = "llama-server did not become ready within 120 s."
            if ($server.HasExited) { $reason = "llama-server exited during startup. See $($Candidate.Name).stderr.log." }
            $rows.Add((New-ResultRow $Candidate "" "" $reason))
            return $rows
        }

        try {
            [void](Invoke-ChatCompletion $baseUrl $Baseline.ModelId "Say ready." 8 $false)
        }
        catch {
            $rows.Add((New-ResultRow $Candidate "" "" "Warm-up request failed: $($_.Exception.Message)"))
            return $rows
        }

        foreach ($prompt in $Prompts) {
            for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
                Write-Host ("  {0} {1} #{2}" -f $Candidate.Name, $prompt.name, $repetition)
                $row = New-ResultRow $Candidate ([string]$prompt.name) $repetition ""
                try {
                    $response = Invoke-ChatCompletion $baseUrl $Baseline.ModelId ([string]$prompt.content) ([int]$prompt.maxTokens) $true
                    $usage = Get-OptionalProperty $response "usage"
                    $timings = Get-OptionalProperty $response "timings"
                    if ($null -eq $timings) { throw "The response did not include timings." }
                    $row.prompt_tokens = [string](Get-OptionalProperty $usage "prompt_tokens")
                    $row.generated_tokens = [string](Get-OptionalProperty $usage "completion_tokens")
                    $row.prompt_tps = Format-Number (Get-TokensPerSecond $timings $usage "prompt_n" "prompt_tokens" "prompt_ms")
                    $row.gen_tps = Format-Number (Get-TokensPerSecond $timings $usage "predicted_n" "completion_tokens" "predicted_ms")
                    $draftN = Get-OptionalProperty $timings "draft_n"
                    $draftAccepted = Get-OptionalProperty $timings "draft_n_accepted"
                    if ($null -ne $draftN) { $row.draft_n = [string]$draftN }
                    if ($null -ne $draftAccepted) { $row.draft_accepted = [string]$draftAccepted }
                    if ($null -ne $draftN -and $null -ne $draftAccepted -and [double]$draftN -gt 0) {
                        $row.draft_accept_rate = Format-Number ([double]$draftAccepted / [double]$draftN)
                    }
                }
                catch {
                    $row.error = "Request failed: $($_.Exception.Message)"
                }
                $rows.Add($row)
            }
        }
        return $rows
    }
    finally {
        Stop-ProcessQuietly $server 30
        Stop-ProcessQuietly $monitor 5
        Start-Sleep -Seconds 3
        $peak = Get-PeakVram $vramPath
        if ($null -ne $peak) {
            foreach ($row in $rows) { $row.peak_vram_mib = [string]$peak }
        }
    }
}

function Get-Summary {
    param($Candidates, $Rows, [string]$GenerationPrompt, [string]$PrefillPrompt)
    $summary = New-Object System.Collections.Generic.List[object]
    foreach ($candidate in $Candidates) {
        $candidateRows = @($Rows | Where-Object { $_.candidate -eq $candidate.Name })
        $errors = @($candidateRows | Where-Object { -not [string]::IsNullOrEmpty($_.error) } | ForEach-Object { $_.error })
        $generation = [double[]]@($candidateRows |
            Where-Object { $_.prompt -eq $GenerationPrompt -and $_.gen_tps -ne "" } |
            ForEach-Object { [double]::Parse($_.gen_tps, $invariant) })
        $prefill = [double[]]@($candidateRows |
            Where-Object { $_.prompt -eq $PrefillPrompt -and $_.prompt_tps -ne "" } |
            ForEach-Object { [double]::Parse($_.prompt_tps, $invariant) })
        $acceptRates = @($candidateRows |
            Where-Object { $_.draft_accept_rate -ne "" } |
            ForEach-Object { [double]::Parse($_.draft_accept_rate, $invariant) })
        $meanAccept = $null
        if ($acceptRates.Count -gt 0) { $meanAccept = ($acceptRates | Measure-Object -Average).Average }
        $peak = ""
        if ($candidateRows.Count -gt 0) { $peak = $candidateRows[0].peak_vram_mib }
        $firstError = ""
        if ($errors.Count -gt 0) { $firstError = $errors[0] }

        $summary.Add([pscustomobject][ordered]@{
            Candidate         = $candidate.Name
            Overrides         = (@($candidate.Overrides | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ";")
            MedianGenTps      = Get-Median $generation
            MedianPrefillTps  = Get-Median $prefill
            MeanDraftAccept   = $meanAccept
            PeakVramMiB       = $peak
            Error             = $firstError
            Succeeded         = ($errors.Count -eq 0 -and $generation.Count -gt 0)
        })
    }
    return $summary
}

# Preconditions.
$LocalAiRoot = [IO.Path]::GetFullPath($LocalAiRoot)
$presetPath = Join-Path $LocalAiRoot "llama-server-models.ini"
if (-not (Test-Path -LiteralPath $presetPath -PathType Leaf)) {
    throw "Start Local AI once from the tray so llama-server-models.ini is generated. Not found: $presetPath"
}
$baseline = Read-BaselinePreset $presetPath

$statePath = Join-Path $LocalAiRoot "state.json"
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    throw "Local AI is not installed in $LocalAiRoot (state.json is missing)."
}
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$relativeExecutable = [string](Get-OptionalProperty $state "executablePath")
$gpuId = [string](Get-OptionalProperty $state "selectedGpuId")
if ([string]::IsNullOrWhiteSpace($relativeExecutable)) { throw "state.json does not record executablePath." }
if ([string]::IsNullOrWhiteSpace($gpuId)) { throw "state.json does not record selectedGpuId." }
$executable = [IO.Path]::GetFullPath((Join-Path $LocalAiRoot $relativeExecutable))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "llama-server was not found at $executable." }

$running = @(Get-Process -Name "llama-server" -ErrorAction SilentlyContinue | Where-Object {
    $processPath = $null
    try { $processPath = $_.Path } catch { }
    $null -ne $processPath -and [string]::Equals($processPath, $executable, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw "Stop Local AI on the Local AI page before benchmarking." }
if (@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Port $Port is already in use. Pass a free port with -Port."
}

$grid = Read-Grid $GridPath $baseline
$prompts = @(Read-Prompts $PromptsPath)
$candidates = @(New-Candidates $grid $baseline)

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $LocalAiRoot ("logs\bench-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)

$nvidiaSmi = $null
$nvidiaSmiCommand = Get-Command "nvidia-smi" -ErrorAction SilentlyContinue
if ($null -ne $nvidiaSmiCommand) { $nvidiaSmi = $nvidiaSmiCommand.Source }
else { Write-Host "nvidia-smi was not found. Peak VRAM will be left blank." }

Write-Host "Model: $($baseline.ModelId)"
Write-Host "Candidates: $($candidates.Count), prompts: $($prompts.Count), repetitions: $Repetitions"
Write-Host "Output: $OutputDirectory"

$previousCudaDevices = $env:CUDA_VISIBLE_DEVICES
$allRows = New-Object System.Collections.Generic.List[object]
try {
    $env:CUDA_VISIBLE_DEVICES = $gpuId
    foreach ($candidate in $candidates) {
        $label = (@($candidate.Overrides | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ";")
        if ($label -eq "") { $label = "(current recipe)" }
        Write-Host "Running $($candidate.Name): $label"
        foreach ($row in (Invoke-Candidate $candidate $baseline $prompts $executable $gpuId $nvidiaSmi)) {
            $allRows.Add($row)
        }
    }
}
finally {
    $env:CUDA_VISIBLE_DEVICES = $previousCudaDevices
    if ($allRows.Count -gt 0) {
        $allRows | Export-Csv -LiteralPath (Join-Path $OutputDirectory "results.csv") -NoTypeInformation -Encoding UTF8
    }
}

$generationPrompt = [string]$prompts[0].name
$prefillPrompt = [string]$prompts[$prompts.Count - 1].name
$summary = @(Get-Summary $candidates $allRows $generationPrompt $prefillPrompt)
$summary |
    Select-Object Candidate, Overrides,
        @{ Name = "GenTps"; Expression = { Format-Number $_.MedianGenTps } },
        @{ Name = "PrefillTps"; Expression = { Format-Number $_.MedianPrefillTps } },
        @{ Name = "DraftAccept"; Expression = { Format-Number $_.MeanDraftAccept } },
        PeakVramMiB, Error |
    Format-Table -AutoSize -Wrap | Out-String -Width 4096 | Write-Host

$baselineSummary = $summary | Where-Object { $_.Candidate -eq "baseline" } | Select-Object -First 1
$eligible = @($summary | Where-Object { $_.Succeeded })
if ($null -ne $baselineSummary -and $baselineSummary.Succeeded -and $null -ne $baselineSummary.MedianPrefillTps) {
    $floor = 0.9 * $baselineSummary.MedianPrefillTps
    $eligible = @($eligible | Where-Object { $null -ne $_.MedianPrefillTps -and $_.MedianPrefillTps -ge $floor })
}
else {
    Write-Host "The baseline did not produce a prefill measurement, so candidates are ranked without the 90% prefill floor."
}
if ($eligible.Count -eq 0) {
    Write-Host "No candidate completed successfully. See results.csv and the candidate logs in $OutputDirectory."
    exit 1
}

# Candidates are in run order (baseline first), so a tie keeps the earlier candidate and a
# change is only recommended when it is strictly faster.
$winner = $null
foreach ($entry in $eligible) {
    if ($null -eq $winner -or $entry.MedianGenTps -gt $winner.MedianGenTps) { $winner = $entry }
}
if ($winner.Candidate -eq "baseline") {
    Write-Host "The current recipe is already the fastest measured candidate."
    exit 0
}

$winnerCandidate = $candidates | Where-Object { $_.Name -eq $winner.Candidate } | Select-Object -First 1
$changed = @($winnerCandidate.Overrides | Where-Object {
    Test-ChangesBaseline $baseline $_
})
$block = New-Object System.Text.StringBuilder
[void]$block.AppendLine("[$($baseline.ModelId)]")
foreach ($entry in $changed) { [void]$block.AppendLine("$($entry.Key) = $($entry.Value)") }
$bestPath = Join-Path $OutputDirectory "best-overrides.ini"
[IO.File]::WriteAllText($bestPath, $block.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Winner: $($winner.Candidate) ($(Format-Number $winner.MedianGenTps) generated tokens/s)"
Write-Host ""
Write-Host $block.ToString()
Write-Host "Merge these keys into the [$($baseline.ModelId)] section of $(Join-Path $LocalAiRoot 'recipe-overrides.ini'), replacing any value it already sets for them (or create the file with this block), then press Restart on the Local AI page."
