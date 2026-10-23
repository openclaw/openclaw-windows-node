[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Manifest,
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $CapsuleRoot,
    [Parameter(Mandatory)][string] $Harness,
    [Parameter(Mandatory)][string] $Profile,
    [Parameter(Mandatory)][string] $Wpr,
    [Parameter(Mandatory)][string] $ActorSid,
    [Parameter(Mandatory)][int] $ActorSession,
    [Parameter(Mandatory)][string] $CallerPath,
    [Parameter(Mandatory)][string] $Destination,
    [Parameter(Mandatory)][string] $ProofRoot,
    [Parameter(Mandatory)][string] $DotnetVersion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
$profileReceiptPath = "$Profile.receipt.json"
$q = Get-Content $profileReceiptPath -Raw | ConvertFrom-Json
if ($m.pr -ne 1791 -or $m.sdkVersion -cne '1.0.0' -or $m.leaseId -cne $LeaseId -or
    $m.disposableComputerName -cne $env:COMPUTERNAME -or
    $q.computer -cne $env:COMPUTERNAME -or $q.traceStarted -or
    (Get-FileHash $Profile).Hash -ine $q.profileSha256 -or $ActorSession -le 0 -or
    (Test-Path $Destination) -or (Test-Path (Join-Path $ProofRoot 'qualification'))) {
    throw 'Exact fresh guest, unrecorded installed profile and immutable new configuration required.'
}
$private = New-Item -ItemType Directory (Join-Path $ProofRoot 'qualification')
$advanced = & $Wpr -help advanced 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Installed WPR advanced help failed.' }
$advanced | Set-Content (Join-Path $private 'wpr-help.txt')
if (($advanced -join "`n") -notmatch 'wpr <command> -instancename <instance name>' -or
    ($advanced -join "`n") -notmatch 'Can be applied to any commands that manipulates the logging session') {
    throw 'Installed global named-instance grammar is not qualified.'
}
foreach ($verb in @('start','stop','status','cancel')) {
    & $Wpr -help $verb *> (Join-Path $private "wpr-$verb-help.txt")
    if ($LASTEXITCODE -ne 0) { throw "Installed $verb help failed." }
}
& $Wpr -profiledetails ($Profile + '!OpenClaw1791.Light') *> (Join-Path $private 'profiledetails.txt')
if ($LASTEXITCODE -ne 0) { throw 'Nonrecording profile qualification failed. No trace permitted.' }
$absent = & $Wpr -status -instancename $q.instanceName 2>&1
$absentCode = $LASTEXITCODE
$absentText = ($absent -join "`n").Trim()
$absent | Set-Content (Join-Path $private 'named-before-status.txt')
if ($absentText -notmatch 'not recording') { throw 'Unique named instance is not demonstrably absent.' }
$observer = Join-Path $CapsuleRoot 'observer-final\QuietLaneCaptureObserver.dll'
$paths = @($Manifest,$Harness,$m.dotnet,$m.powerShell,$m.jobSource,$m.scopeApprovalReceipt,
    $Profile,$profileReceiptPath,$Wpr,$observer)
$paths += @($m.stages.script) + @($m.supportScripts.path) + @($m.artifacts.path)
$paths += @($m.inputs.nativeRuntime,$m.inputs.nativeProbe,$m.inputs.nativeInitFailure)
$paths += @(Get-ChildItem -LiteralPath $CapsuleRoot -Recurse -File | ForEach-Object FullName)
$paths += @(Get-ChildItem -LiteralPath (Split-Path $Harness) -File | ForEach-Object FullName)
$paths += @(Get-ChildItem -LiteralPath $private -File | ForEach-Object FullName)
$paths += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*Startup*.ps1' | ForEach-Object FullName)
$paths = @($paths | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)
$c = [ordered]@{
    computer = $env:COMPUTERNAME; leaseId = $LeaseId; sourceHead = $m.expectedHead; sdkVersion = '1.0.0'
    actorSid = $ActorSid; actorSession = $ActorSession
    manifest = [IO.Path]::GetFullPath($Manifest); capsuleRoot = [IO.Path]::GetFullPath($CapsuleRoot)
    observerSource = [IO.Path]::GetFullPath($observer); harness = [IO.Path]::GetFullPath($Harness)
    dotnet = $m.dotnet; dotnetVersion = $DotnetVersion; powerShell = $m.powerShell
    workingDirectory = $m.repository; callerPath = $CallerPath; runUrl = $null
    wpr = [IO.Path]::GetFullPath($Wpr); profile = [IO.Path]::GetFullPath($Profile)
    instanceName = $q.instanceName; installedProfileQualified = $true
    deadlineSeconds = 900; toolDeadlineSeconds = 30
    absentStatusExitCode = $absentCode; absentStatusPattern = [Regex]::Escape($absentText)
    countedInvalidShowsAuthorized = $true
    dryRunReceipt = Join-Path $ProofRoot 'DryRunReceipt.json'
    traceReservation = Join-Path $ProofRoot 'recording-reserved'
    traceStartsFile = Join-Path $ProofRoot 'trace-starts.json'
    requiredPaths = $paths
    files = @(foreach ($path in $paths) { @{ path = $path; sha256 = (Get-FileHash -LiteralPath $path).Hash } })
}
$c | ConvertTo-Json -Depth 35 | Set-Content -LiteralPath $Destination -Encoding utf8NoBOM
[pscustomobject]@{ completed = $true; traceStartsCounter = 0; configSha256 = (Get-FileHash $Destination).Hash }
