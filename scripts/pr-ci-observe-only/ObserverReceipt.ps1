# Fixed private observer files only. Never emits paths, raw JSON, exception text or identifiers.
$ObserverReceiptByteCap = 16384
$BlameIdentityStates = @('unobserved','absent','expected-single','expected-equivalent-copies','conflicting-identity','query-failed')
$ObserverFailureReasons = @('none','initialization','host-query','source-query','receipt-write','collector-set','ambiguous-host','host-identity','event-order','ambiguous-source','source-mismatch','probe-failed','host-exited-before-case')
function New-ObserveCollectorStatus {
    return [ordered]@{ claim='unobserved'; ready='unobserved'; invalid='unobserved'; final='unobserved'; receiptReadStatus='not-inspected'; failureReason=$null; writersJoined=$false; blameIdentityState='unobserved' }
}
function Test-ObserveCollectorStatusReady($Status) {
    return $Status.writersJoined -eq $true -and $Status.claim -ceq 'present' -and $Status.ready -ceq 'present' -and
        $Status.invalid -ceq 'absent' -and $Status.final -ceq 'present' -and $Status.receiptReadStatus -ceq 'validated' -and $Status.failureReason -ceq 'none' -and $Status.blameIdentityState -cin @('expected-single','expected-equivalent-copies')
}
function Test-ObserverJsonKeys($Element, [string[]]$Names) {
    if ($Element.ValueKind.ToString() -cne 'Object') { return $false }
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($p in $Element.EnumerateObject()) { if(-not $keys.Add($p.Name) -or $p.Name -cnotin $Names) { return $false } }
    return $keys.Count -eq $Names.Count
}
function Test-ObserverReceiptShape($Root, [string]$ExpectedInvocation) {
    try {
        $names='schema invocation mode captureEnabled blameIdentityState hosts cases ends sourceMatched probePassed sessionEnded bound failure processRetained jobMatched hostPid hostCreation jobName binaries' -split ' '
        if(-not (Test-ObserverJsonKeys $Root $names)) { return $false }
        foreach($name in @('schema','hosts','cases','ends','hostPid')) {
            $number=0
            if(-not $Root.GetProperty($name).TryGetInt32([ref]$number) -or $number -lt 0) { return $false }
            if($name -eq 'schema' -and $number -ne 2) { return $false }
            if($name -in @('hosts','cases','ends') -and $number -gt 1024) { return $false }
        }
        $creation=[long]0
        if(-not $Root.GetProperty('hostCreation').TryGetInt64([ref]$creation) -or $creation -lt 0) { return $false }
        foreach($name in @('captureEnabled','sourceMatched','probePassed','sessionEnded','bound','processRetained','jobMatched')) {
            if($Root.GetProperty($name).ValueKind.ToString() -cnotin @('True','False')) { return $false }
        }
        if($Root.GetProperty('captureEnabled').GetBoolean()) { return $false }
        $nonce=$Root.GetProperty('invocation').GetString()
        if($nonce -cnotmatch '\A[a-f0-9]{32}\z' -or $nonce -cne $ExpectedInvocation -or $Root.GetProperty('mode').GetString() -cne 'ObserveOnly') { return $false }
        $identityState=$Root.GetProperty('blameIdentityState').GetString()
        if($identityState -cnotin $BlameIdentityStates) { return $false }
        $identityAccepted=$identityState -cin @('expected-single','expected-equivalent-copies')
        $reason=$Root.GetProperty('failure').GetString()
        if($reason -cnotin $ObserverFailureReasons) { return $false }
        $job=$Root.GetProperty('jobName').GetString()
        if($null -eq $job -or ($job.Length -ne 0 -and $job -cnotmatch '\ALocal\\OpenClawObserve-[a-f0-9]{32}\z')) { return $false }
        $binaries=$Root.GetProperty('binaries')
        if($binaries.ValueKind.ToString() -cne 'Array' -or $binaries.GetArrayLength() -gt 6) { return $false }
        $roles=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($binary in $binaries.EnumerateArray()) {
            if(-not (Test-ObserverJsonKeys $binary @('role','sha256','version','binding'))) { return $false }
            $role=$binary.GetProperty('role').GetString()
            if($role -cnotin @('object-model','observer','collector-corelib','collector-coreclr','host-image','blame') -or -not $roles.Add($role)) { return $false }
            if($binary.GetProperty('sha256').GetString() -cnotmatch '\A[a-f0-9]{64}\z' -or $binary.GetProperty('version').GetString() -cnotmatch '\A[0-9]{1,10}(\.[0-9]{1,10}){3}\z') { return $false }
            $binding=if($role -ceq 'host-image') {'framework-query'} else {'loaded'}
            if($binary.GetProperty('binding').GetString() -cne $binding) { return $false }
        }
        if($roles.Contains('blame') -ne $identityAccepted) { return $false }
        if($Root.GetProperty('bound').GetBoolean()) {
            if(-not $identityAccepted) { return $false }
            if($reason -cne 'none' -or $job.Length -eq 0 -or $creation -eq 0 -or $Root.GetProperty('hostPid').GetInt32() -eq 0 -or $roles.Count -ne 6) { return $false }
            foreach($name in @('hosts','cases','ends')) { if($Root.GetProperty($name).GetInt32() -ne 1) { return $false } }
            foreach($name in @('sourceMatched','probePassed','sessionEnded','processRetained','jobMatched')) { if(-not $Root.GetProperty($name).GetBoolean()) { return $false } }
        }
        return $true
    } catch { return $false }
}
function Get-ObserveCollectorSnapshot {
    param([AllowNull()][string]$Root, [bool]$Attempted, [bool]$WritersJoined, [AllowNull()][string]$ExpectedInvocation)
    $status=New-ObserveCollectorStatus; $receipt=$null
    if(-not $Attempted) { return [pscustomobject]@{ Status=$status; Receipt=$null } }
    $status.writersJoined=$WritersJoined
    try {
        if([string]::IsNullOrEmpty($Root) -or -not [IO.Path]::IsPathFullyQualified($Root) -or -not [IO.Directory]::Exists($Root)) { throw 'observer-root-unavailable' }
        for($p=[IO.Path]::GetFullPath($Root);$p;$p=[IO.Path]::GetDirectoryName($p)) {
            if([IO.File]::GetAttributes($p).HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'observer-root-unavailable' }
        }
        foreach($pair in @(@('claim','collector.claim'),@('ready','observer-ready.json'),@('invalid','observer-invalid'),@('final','observer.json'))) {
            try {
                $attributes=[IO.File]::GetAttributes((Join-Path $Root $pair[1]))
                $status[$pair[0]]=if(($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -eq 0) {'present'} else {'unobserved'}
            } catch [IO.FileNotFoundException] { $status[$pair[0]]='absent' }
              catch [IO.DirectoryNotFoundException] { $status[$pair[0]]='absent' }
              catch { $status[$pair[0]]='unobserved' }
        }
        # Presence is observational only. Never read/admit an active or unjoined writer's data.
        if(-not $WritersJoined) { return [pscustomobject]@{Status=$status;Receipt=$null} }
        if($status.final -ceq 'absent') { $status.receiptReadStatus='missing';return [pscustomobject]@{Status=$status;Receipt=$null} }
        if($status.final -cne 'present') { throw 'observer-receipt-unavailable' }
        $file=$null;$reader=$null;$document=$null
        try {
            $file=[IO.FileStream]::new((Join-Path $Root 'observer.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            if($file.Length -gt $ObserverReceiptByteCap) { $status.receiptReadStatus='oversize';return [pscustomobject]@{Status=$status;Receipt=$null} }
            $reader=[IO.StreamReader]::new($file,[Text.UTF8Encoding]::new($false,$true),$true,1024,$true)
            $json=$reader.ReadToEnd() # Length bounded and the retained handle denies concurrent writes/deletion.
            try { $options=[System.Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=8;$document=[System.Text.Json.JsonDocument]::Parse([string]$json,$options) }
            catch { $status.receiptReadStatus='invalid-json';return [pscustomobject]@{Status=$status;Receipt=$null} }
            if(-not (Test-ObserverReceiptShape $document.RootElement $ExpectedInvocation)) { $status.receiptReadStatus='invalid-shape';return [pscustomobject]@{Status=$status;Receipt=$null} }
            $receipt=ConvertFrom-Json -InputObject $json -AsHashtable
            $status.receiptReadStatus='validated';$status.failureReason=$receipt.failure;$status.blameIdentityState=$receipt.blameIdentityState
        } finally {
            if($null -ne $document){$document.Dispose()};if($null -ne $reader){$reader.Dispose()};if($null -ne $file){$file.Dispose()}
        }
    } catch {
        $status.receiptReadStatus=if($WritersJoined){'unreadable'}else{'not-inspected'}
        $status.failureReason=$null;$status.blameIdentityState='unobserved';$receipt=$null
    }
    return [pscustomobject]@{ Status=$status; Receipt=$receipt }
}
function Save-ObserveCollectorSnapshot {
    param([hashtable]$Report, [scriptblock]$ReadSnapshot)
    # Diagnostics are secondary: never touch native outcomes, failureWitness, or throw over a primary failure.
    try {
        $result=@(& $ReadSnapshot)
        if($result.Count -ne 1 -or $null -eq $result[0].Status) { throw 'observer-snapshot-shape' }
        $Report.collector=$result[0].Status
    } catch { $Report.collector=New-ObserveCollectorStatus }
}
