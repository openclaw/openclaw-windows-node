# No target arguments or debugger/capture operations exist in this module.
$PreparationDownloadBytes=16MB;$PreparationExpandedBytes=32MB;$PreparationArchiveMembers=8;$PreparationToolSeconds=30;$PreparationToolOutputBytes=2MB
function Get-PreparationArchivePlan($Archive) {
    $allowed=@('procdump.exe','procdump64.exe','procdump64a.exe','eula.txt')
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$plan=@();$total=[long]0
    if($Archive.Entries.Count -lt 2 -or $Archive.Entries.Count -gt $PreparationArchiveMembers){throw 'archive-count'}
    foreach($entry in $Archive.Entries){
        $name=$entry.FullName
        if($name -cnotmatch '\A[A-Za-z0-9.-]{1,80}\z' -or $name.ToLowerInvariant() -notin $allowed -or -not $seen.Add($name)){throw 'archive-member'}
        $attributes=[uint32]([long]$entry.ExternalAttributes -band 0xffffffffL);$mode=($attributes -shr 16) -band 0xf000
        if(($attributes -band 0x410) -ne 0 -or $mode -notin @(0,0x8000)){throw 'archive-type'}
        if($entry.Length -lt 1 -or $entry.Length -gt $PreparationDownloadBytes){throw 'archive-entry-limit'}
        $total+=$entry.Length;if($total -gt $PreparationExpandedBytes){throw 'archive-total-limit'}
        if($name.ToLowerInvariant() -in @('procdump64.exe','eula.txt')){$plan+=,$entry}
    }
    if(-not $seen.Contains('procdump64.exe') -or -not $seen.Contains('eula.txt') -or $plan.Count -ne 2){throw 'archive-required'}
    return ,$plan
}
function Get-PrivateProcDump([string]$Directory){
    if(Test-Path -LiteralPath $Directory){throw 'tool-root-exists'}
    $null=New-Item -ItemType Directory -Path $Directory
    $zip=Join-Path $Directory 'procdump.zip';$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler);$cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($PreparationToolSeconds));$response=$null;$input=$null;$output=$null
    try {
        $response=$client.GetAsync('https://download.sysinternals.com/files/Procdump.zip',[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
        if([int]$response.StatusCode -ne 200 -or ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $PreparationDownloadBytes)){throw 'download-status-or-limit'}
        $input=$response.Content.ReadAsStreamAsync($cancel.Token).GetAwaiter().GetResult();$output=[IO.FileStream]::new($zip,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $buffer=[byte[]]::new(65536);$total=[long]0
        while($true){$count=$input.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult();if($count -eq 0){break};$total+=$count;if($total -gt $PreparationDownloadBytes){throw 'download-limit'};$output.Write($buffer,0,$count)}
        if($total -eq 0){throw 'download-empty'};$output.Flush($true)
    } finally {if($null -ne $output){$output.Dispose()};if($null -ne $input){$input.Dispose()};if($null -ne $response){$response.Dispose()};$cancel.Dispose();$client.Dispose();$handler.Dispose()}
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $plan=Get-PreparationArchivePlan $archive # Validate ALL members before extracting either allowed file.
        foreach($entry in $plan){
            $target=Join-Path $Directory $entry.FullName.ToLowerInvariant();$source=$entry.Open();$dest=$null
            try {$dest=[IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$count=0L;$buffer=[byte[]]::new(65536)
                while(($read=$source.Read($buffer,0,$buffer.Length)) -gt 0){$count+=$read;if($count -gt $entry.Length -or $count -gt $PreparationDownloadBytes){throw 'expansion-limit'};$dest.Write($buffer,0,$read)}
                if($count -ne $entry.Length){throw 'expansion-length'};$dest.Flush($true)
            } finally {if($null -ne $dest){$dest.Dispose()};$source.Dispose()}
        }
    } finally {$archive.Dispose()}
    return @{Path=(Join-Path $Directory 'procdump64.exe');ArchiveSha256=(Sha $zip)}
}
function Get-NoTargetArguments([string]$Role){
    switch -CaseSensitive ($Role){'procdump'{return ,@('-accepteula','-?')};'cdb'{return ,@('-version')};default{throw 'no-target-role'}}
}
function Test-ReviewedProcDumpContract($Profile,$Observation){
    # A moving signed ZIP is not an approved capture-tool contract. Exact values need parent review.
    if($Profile.reviewed -isnot [bool] -or -not $Profile.reviewed){return $false}
    if((@($Profile.Keys|Sort-Object) -join '|') -cne 'documentationCommit|helpExitCode|helpSha256|reviewed|schema|sha256|version' -or $Profile.documentationCommit -cne 'd46b5bfa4b1151a3dbcf568152cb56a4a9f7c032' -or ($Profile.schema -isnot [int] -and $Profile.schema -isnot [long])){return $false}
    if($Profile.schema -ne 1 -or $Profile.version -isnot [string] -or $Profile.version -cnotmatch '\A[0-9]{1,10}(\.[0-9]{1,10}){3}\z' -or $Profile.sha256 -isnot [string] -or $Profile.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $Profile.helpSha256 -isnot [string] -or $Profile.helpSha256 -cnotmatch '\A[a-f0-9]{64}\z'){return $false}
    $help=$Observation['helpProjection']
    if($help -isnot [Collections.IDictionary] -or $help['status'] -cne 'recognized' -or $help['grammarId'] -cne 'procdump-help-12.02-d46b5bfa' -or $help['versionAgreement'] -isnot [bool] -or -not $help['versionAgreement'] -or $help['behaviorVerified'] -isnot [bool] -or $help['behaviorVerified']){return $false}
    if($help['helpMajor'] -ne 12 -or $help['helpMinor'] -ne 2 -or $Observation.version -cnotmatch '\A12\.2\.[0-9]+\.[0-9]+\z' -or $help['inputSha256'] -cne $Observation.helpSha256){return $false}
    $required=@('acceptEula','cancelGraceful','captureTimeout','dumpCount','fullMemory','helpNoTarget','nativeDebugger','unhandledException')
    if($help['capabilities'] -isnot [Collections.IDictionary] -or (@($help['capabilities'].Keys|Sort-Object) -join '|') -cne ($required -join '|')){return $false}
    foreach($key in $required){if($help['capabilities'][$key] -isnot [bool] -or -not $help['capabilities'][$key]){return $false}}
    return $Profile.version -ceq $Observation.version -and $Profile.sha256 -ceq $Observation.sha256 -and
        $Profile.helpSha256 -ceq $Observation.helpSha256 -and ($Profile.helpExitCode -is [int] -or $Profile.helpExitCode -is [long]) -and $Profile.helpExitCode -ge [int]::MinValue -and $Profile.helpExitCode -le [int]::MaxValue -and $Profile.helpExitCode -eq $Observation.nativeExitCode
}
function Save-PreparationToolFailure($Tool,[hashtable]$Outcomes,[AllowNull()][Nullable[int]]$FirstExit){
    # Called only while this exact tool owns the current Run/Receive result.
    $result=$FirstExit
    if($null -ne $Tool -and $Outcomes.probe -is [int]){
        if($Outcomes.probe -ne 0 -and $null -eq $result){$result=$Outcomes.probe}
        try {$Tool.nativeExitCode=$Outcomes.probe} catch { } # Metadata failure cannot replace the known native outcome.
    }
    return $result
}

function New-UnobservedProcDumpHelp {
    $capabilities=@{};$clauses=@{}
    foreach($key in @('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')){$capabilities[$key]=$false;$clauses[$key]='unobserved'}
    return @{schema=1;status='not-observed';reason='none';grammarId=$null;scope='documented-help-forms';behaviorVerified=$false;inputSha256=$null;inputBytes=$null;encoding='unknown';helpMajor=$null;helpMinor=$null;versionAgreement=$null;capabilities=$capabilities;clauses=$clauses}
}
