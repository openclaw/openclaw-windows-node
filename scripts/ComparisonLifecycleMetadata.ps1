# Testhost-only schema at product 3876d834. Never export an unvalidated input line.
# No native calls, memory capture, process lookup or exception-content projection.
Set-StrictMode -Version Latest
$script:ComparisonLifecycleMaxBytes = 2097152
$script:ComparisonLifecycleMaxRecords = 4096
$script:ComparisonLifecycleMaxRecordBytes = 1024
$script:ComparisonLifecycleMaxOutputBytes = 2101248

function Get-ComparisonLifecycleHost {
    param([object[]]$ExitMetadata)
    if ($null -eq $ExitMetadata -or $ExitMetadata.Count -eq 0 -or $ExitMetadata.Count -gt 32) { throw 'Lifecycle host binding is absent or oversized' }
    foreach ($record in $ExitMetadata) {
        if ($record.hostProcessId -isnot [int] -or $record.hostProcessId -le 0 -or $record.reportedExitCode -isnot [int]) { throw 'Invalid typed host-exit binding' }
    }
    $pairs = @($ExitMetadata | ForEach-Object { "$($_.hostProcessId):$($_.reportedExitCode)" } | Select-Object -Unique)
    if ($pairs.Count -ne 1) { throw 'Lifecycle host binding is ambiguous' }
    return $ExitMetadata[0].hostProcessId
}

function ConvertFrom-ComparisonLifecycleBytes {
    param([byte[]]$Bytes, [string]$ExpectedSource, [string]$InvocationId, [int]$HostProcessId)
    try {
        if ($null -eq $Bytes -or $Bytes.Length -eq 0 -or $Bytes.Length -gt $script:ComparisonLifecycleMaxBytes -or
            $ExpectedSource -cnotmatch '^[a-f0-9]{40}$' -or $InvocationId -cnotmatch '^[a-f0-9]{32}$' -or $HostProcessId -le 0) {
            throw 'Invalid lifecycle input boundary'
        }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        $lines = $text.Split([char]10)
        if ($lines.Count -lt 2 -or $lines.Count -gt $script:ComparisonLifecycleMaxRecords + 1 -or $lines[-1] -cne '') { throw 'Invalid lifecycle line boundary' }
        $keys = @('schema','sequence','source','processId','managedThreadId','invocationId','configuredSourceRevision',
            'scopeId','phase','lastStartedDispatchId','lastStartedDispatchState','sourceDispatchId',
            'exceptionAvailable','exceptionReadFailed','exceptionType','reportedHResult','reportedHResultHex')
        $sources = @('ready','lifecycle','WinUiUnhandled','DispatchCatch','ChatUnhandled','Unknown','overflow','closed')
        $phases = @('Unknown','Idle','UiStarting','UiReady','UiStopping','UiExited','ChatSetup','ChatBody',
            'ChatShowingFlyout','ChatFlyoutShown','ChatClosingPopups','ChatDisposingWindow','ChatDisposingHost',
            'ChatClosingWindow','ChatWindowClosed','ChatDisposingProvider','ChatClosed','ChatScopeEnded')
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 3
        $records = [Collections.Generic.List[object]]::new()
        $terminal = $false
        for ($i = 0; $i -lt $lines.Count - 1; $i++) {
            $line = $lines[$i]
            if ($terminal -or $line.Length -eq 0 -or [Text.Encoding]::UTF8.GetByteCount($line) + 1 -gt $script:ComparisonLifecycleMaxRecordBytes) { throw 'Invalid lifecycle record boundary' }
            $document = [Text.Json.JsonDocument]::Parse([string]$line, $options)
            try {
                $root = $document.RootElement
                if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Invalid lifecycle object' }
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($property in $root.EnumerateObject()) {
                    if ($property.Name -cnotin $keys -or -not $seen.Add($property.Name)) { throw 'Unknown or duplicate lifecycle field' }
                }
                if ($seen.Count -ne $keys.Count) { throw 'Missing lifecycle field' }
                $row = [ordered]@{}
                foreach ($name in @('schema','sequence','processId','managedThreadId','scopeId','lastStartedDispatchId','sourceDispatchId')) {
                    $element = $root.GetProperty($name)
                    $number = 0L
                    if ($element.ValueKind -ne [Text.Json.JsonValueKind]::Number -or -not $element.TryGetInt64([ref]$number) -or $number -lt 0) { throw 'Invalid lifecycle integer' }
                    $row[$name] = $number
                }
                if ($row.schema -ne 1 -or $row.sequence -ne $i + 1 -or $row.processId -ne $HostProcessId -or
                    $row.managedThreadId -le 0 -or $row.managedThreadId -gt [int]::MaxValue) { throw 'Invalid lifecycle identity or sequence' }
                foreach ($name in @('source','phase','invocationId','configuredSourceRevision','lastStartedDispatchState')) {
                    $element = $root.GetProperty($name)
                    if ($element.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Invalid lifecycle string type' }
                    $row[$name] = $element.GetString()
                }
                if ($row.source -cnotin $sources -or $row.phase -cnotin $phases -or
                    $row.lastStartedDispatchState -cnotin @('none','running','completed','faulted') -or
                    $row.invocationId -cne $InvocationId -or $row.configuredSourceRevision -cne $ExpectedSource) { throw 'Invalid lifecycle identity or enum' }
                foreach ($name in @('exceptionAvailable','exceptionReadFailed')) {
                    $element = $root.GetProperty($name)
                    if ($element.ValueKind -notin @([Text.Json.JsonValueKind]::True,[Text.Json.JsonValueKind]::False)) { throw 'Invalid lifecycle Boolean' }
                    $row[$name] = $element.GetBoolean()
                }
                foreach ($name in @('exceptionType','reportedHResultHex')) {
                    $element = $root.GetProperty($name)
                    if ($element.ValueKind -eq [Text.Json.JsonValueKind]::Null) { $row[$name] = $null }
                    elseif ($element.ValueKind -eq [Text.Json.JsonValueKind]::String) { $row[$name] = $element.GetString() }
                    else { throw 'Invalid nullable lifecycle string' }
                }
                $element = $root.GetProperty('reportedHResult')
                $hresult = 0
                if ($element.ValueKind -eq [Text.Json.JsonValueKind]::Null) { $row.reportedHResult = $null }
                elseif ($element.ValueKind -eq [Text.Json.JsonValueKind]::Number -and $element.TryGetInt32([ref]$hresult)) { $row.reportedHResult = $hresult }
                else { throw 'Invalid lifecycle HRESULT' }
                if ($row.exceptionAvailable) {
                    if ($null -eq $row.exceptionType -or $row.exceptionType -cnotmatch '^[A-Za-z0-9_.+]{1,128}$' -or
                        $null -eq $row.reportedHResult -or $row.exceptionReadFailed -or
                        $row.reportedHResultHex -cne ('0x{0:X8}' -f ([long]$row.reportedHResult -band 4294967295L))) { throw 'Invalid lifecycle exception metadata' }
                } elseif ($null -ne $row.exceptionType -or $null -ne $row.reportedHResult -or $null -ne $row.reportedHResultHex) { throw 'Unexpected lifecycle exception metadata' }
                if ($row.source -cin @('ready','lifecycle','overflow','closed') -and
                    ($row.exceptionAvailable -or $row.exceptionReadFailed -or $row.sourceDispatchId -ne 0)) { throw 'Unexpected lifecycle payload' }
                if (($i -eq 0) -ne ($row.source -ceq 'ready')) { throw 'Invalid lifecycle ready boundary' }
                if ($i -eq 0 -and ($row.scopeId -ne 0 -or $row.phase -cne 'Idle' -or $row.lastStartedDispatchId -ne 0 -or $row.lastStartedDispatchState -cne 'none')) { throw 'Invalid lifecycle ready state' }
                $terminal = $row.source -cin @('overflow','closed')
                $records.Add($row)
            } finally { $document.Dispose() }
        }
        return [ordered]@{
            schema = 'validated-native-ui-lifecycle-v1'
            status = 'validated'
            sourceCommit = $ExpectedSource
            invocationId = $InvocationId
            hostProcessId = $HostProcessId
            recordCount = $records.Count
            closed = $records[-1].source -ceq 'closed'
            overflowed = $records[-1].source -ceq 'overflow'
            records = @($records.ToArray())
        }
    } catch {
        # JsonException text can quote input. Export neither it nor inner exceptions.
        throw [IO.InvalidDataException]::new('Lifecycle receipt rejected by its fixed schema or identity boundary.')
    }
}

function Read-ComparisonLifecycle {
    param([string]$Path, [string]$ExpectedSource, [string]$InvocationId, [int]$HostProcessId)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -le 0 -or $file.Length -gt $script:ComparisonLifecycleMaxBytes) {
        throw [IO.InvalidDataException]::new('Lifecycle file is outside its bounded inventory.')
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $size = $stream.Length
        if ($size -le 0 -or $size -gt $script:ComparisonLifecycleMaxBytes) { throw 'Lifecycle file size changed' }
        $bytes = [byte[]]::new([int]$size)
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        if ($stream.ReadByte() -ne -1) { throw 'Lifecycle file size changed' }
    } finally { $stream.Dispose() }
    return ConvertFrom-ComparisonLifecycleBytes -Bytes $bytes -ExpectedSource $ExpectedSource -InvocationId $InvocationId -HostProcessId $HostProcessId
}
