# Fixed preparation-only evidence for Invoke-VoicePair. Dot-source, enter a fixed
# phase, invoke one native command, then copy the safe witness on failure.
# No process-management framework, trace collection, arbitrary error export or retry.
Set-StrictMode -Version Latest
$script:VoicePreparation = $null
$script:VoicePreparationRoot = $null
$script:VoicePreparationClock = $null
$script:VoiceCodeMaxBytes = 2MB
$script:VoiceCodeMaxLine = 8192
$script:VoiceCodeMaxLines = 16384
$script:VoiceCodeMaxCount = 8

function New-VoiceCodeWitness([string]$Status, [string[]]$Codes = @(), [bool]$Truncated = $false) {
    return [ordered]@{ status = $Status; codes = @($Codes); truncated = $Truncated }
}
function ConvertTo-VoiceCompilerCodes([string]$Text) {
    if ([Text.Encoding]::UTF8.GetByteCount($Text) -gt $script:VoiceCodeMaxBytes) { return New-VoiceCodeWitness 'oversized' }
    $lines = $Text -split '\r?\n'
    if ($lines.Count -gt $script:VoiceCodeMaxLines) { return New-VoiceCodeWitness 'oversized' }
    $codes = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $truncated = $false
    # Admit a fixed compiler producer or a conservative diagnostic source-path
    # grammar. Neither the path nor anything after the code colon is projected.
    $file = '(?:(?:[A-Za-z]:[\\/]|\.{1,2}[\\/])[^:\r\n"<>|?*]{1,1000}|[A-Za-z0-9_.\\/-]{1,1000})\.(?:cs|csproj|props|targets|xaml|sln)'
    $prefix = '^\s*(?:\d+>)?(?:(?:' + $file + ')(?:\(\d+(?:,\d+){0,3}\))?\s*:\s*|(?:CSC|MSBUILD|NuGet)\s*:\s*)?error\s+'
    foreach ($line in $lines) {
        if ($line.Length -gt $script:VoiceCodeMaxLine) { return New-VoiceCodeWitness 'oversized-line' }
        $match = [regex]::Match($line, $prefix + '(?<code>(?:CS|MSB|NU|NETSDK|WMC)[0-9]{4}):', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) { continue }
        $code = $match.Groups['code'].Value
        if ($codes.Count -lt $script:VoiceCodeMaxCount -or $codes.Contains($code)) { [void]$codes.Add($code) }
        else { $truncated = $true }
    }
    $values = [string[]]::new($codes.Count)
    $codes.CopyTo($values)
    return New-VoiceCodeWitness $(if ($codes.Count) { 'found' } else { 'none' }) $values $truncated
}
function Read-VoiceCompilerCodes {
    # Caller cannot nominate a path. Only this phase's direct owned log is read.
    $suffix = switch ($script:VoicePreparation.phase) {
        'default-restore' { 'restore' }; 'default-build' { 'build' }
        'fixture-restore' { 'fixture-restore' }; 'fixture-build' { 'fixture-build' }
        default { return New-VoiceCodeWitness 'not-applicable' }
    }
    if ($script:VoicePreparation.arm -cnotin @('main', 'approval')) { return New-VoiceCodeWitness 'invalid' }
    $path = Join-Path $script:VoicePreparationRoot ($script:VoicePreparation.arm + '-' + $suffix + '.log')
    try {
        foreach ($entry in @($script:VoicePreparationRoot, $path)) {
            if ((Get-Item -LiteralPath $entry -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) { return New-VoiceCodeWitness 'invalid' }
        }
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $bytes = [byte[]]::new($script:VoiceCodeMaxBytes + 1)
            $used = 0
            while ($used -lt $bytes.Length) {
                $count = $stream.Read($bytes, $used, $bytes.Length - $used)
                if ($count -eq 0) { break }
                $used += $count
            }
            if ($used -gt $script:VoiceCodeMaxBytes) { return New-VoiceCodeWitness 'oversized' }
            try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, 0, $used).TrimStart([char]0xFEFF) }
            catch { return New-VoiceCodeWitness 'invalid' }
            return ConvertTo-VoiceCompilerCodes $text
        } finally { $stream.Dispose() }
    } catch { return New-VoiceCodeWitness 'unavailable' }
}
function Start-VoicePreparation([string]$PrivateRoot) {
    $script:VoicePreparationRoot = [IO.Path]::GetFullPath($PrivateRoot)
    $script:VoicePreparationClock = [Diagnostics.Stopwatch]::StartNew()
    Enter-VoicePreparation 'identity' 'none' 'source-guard'
}
function Write-VoicePreparationBreadcrumb {
    # Information stream only, fixed enums/Boolean/numeric values; never errors.
    try {
        $native = if ($null -eq $script:VoicePreparation.nativeExitCode) { 'unavailable' } else { $script:VoicePreparation.nativeExitCode.ToString([Globalization.CultureInfo]::InvariantCulture) }
        Write-Host ('Voice preparation phase={0} arm={1} state={2} category={3} nativeExit={4}' -f
            $script:VoicePreparation.phase, $script:VoicePreparation.arm, $script:VoicePreparation.state, $script:VoicePreparation.category, $native)
    } catch { }
}
function Enter-VoicePreparation {
    param(
        [ValidateSet('identity','directories','witness-controls','python-controls','output-controls','source-before','default-restore','default-build','fixture-restore','fixture-build','target-query','target-json','target-contract','required-files','app-sdk','source-after','runtime-version','runtime-download','runtime-install','desktop','sdk-query','sdk-contract','image-contract','manifest')][string]$Phase,
        [ValidateSet('none','main','approval')][string]$Arm = 'none',
        [ValidateSet('unknown','source-guard','file-io','controls','target-query-json','target-query-contract','dependency-contract','runtime-download','desktop-contract','image-contract')][string]$Category = 'unknown'
    )
    $script:VoicePreparation = [ordered]@{ phase = $Phase; arm = $Arm; state = 'entered'; category = $Category
        nativeAttempted = $false; nativeReturned = $false; nativeExitCode = $null
        compiler = (New-VoiceCodeWitness 'not-applicable') }
    Write-VoicePreparationBreadcrumb
}
function Complete-VoicePreparation {
    $script:VoicePreparation.state = 'returned'
    Write-VoicePreparationBreadcrumb
}
function Invoke-VoicePreparationNative([scriptblock]$Command) {
    $script:VoicePreparation.nativeAttempted = $true
    $global:LASTEXITCODE = $null
    try {
        try { & $Command; $script:VoicePreparation.nativeReturned = $true }
        finally {
            # Same-call capture. A failed launch cannot inherit a previous native0.
            if ($global:LASTEXITCODE -is [int]) { $script:VoicePreparation.nativeExitCode = $global:LASTEXITCODE }
        }
        if ($null -eq $script:VoicePreparation.nativeExitCode) { throw 'Preparation native result unavailable.' }
        if ($script:VoicePreparation.nativeExitCode -ne 0) { throw 'Preparation native command failed.' }
        Complete-VoicePreparation
    } catch {
        $script:VoicePreparation.state = 'failed'
        $script:VoicePreparation.category = if ($null -eq $script:VoicePreparation.nativeExitCode) { 'native-no-result' } else { 'native-nonzero-or-invocation' }
        try { $script:VoicePreparation.compiler = Read-VoiceCompilerCodes } catch { $script:VoicePreparation.compiler = New-VoiceCodeWitness 'unavailable' }
        Write-VoicePreparationBreadcrumb
        throw # Preserve the original exception; diagnostic failures never replace it.
    }
}
function Get-VoicePreparationFailure {
    if ($null -eq $script:VoicePreparation) { return $null }
    $script:VoicePreparation.state = 'failed'
    $safe = [ordered]@{ phase = $script:VoicePreparation.phase; arm = $script:VoicePreparation.arm
        state = 'failed'; category = $script:VoicePreparation.category
        elapsedMs = [Math]::Min([int]::MaxValue, $script:VoicePreparationClock.ElapsedMilliseconds)
        nativeAttempted = $script:VoicePreparation.nativeAttempted; nativeReturned = $script:VoicePreparation.nativeReturned
        nativeExitCode = $script:VoicePreparation.nativeExitCode; compiler = $script:VoicePreparation.compiler }
    return $safe
}
function Save-VoicePreparationFailure([Collections.IDictionary]$Receipt, [scriptblock]$Writer) {
    try { $Receipt['preparation'] = Get-VoicePreparationFailure } catch { }
    try { & $Writer $Receipt | Out-Null } catch { }
}
