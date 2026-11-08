#Requires -Version 7.2
<#
.SYNOPSIS
    Validates the product MXC runner against owned synthetic user folders only.
.DESCRIPTION
    Does not start a tray, Gateway, WSL, listener, service or elevated process.
    Requires an explicit artifact directory outside the checkout. Real appdata
    metadata is compared without reading credential contents. Native skips fail.
    The existing artifact root owns runs.jsonl unless -RegistryPath is supplied.
    -PreflightOnly validates plumbing without creating a run or claiming native proof.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$SourceManifest,
    [string]$ResultsDirectory = 'TestResults\MxcDotNetUserFolders',
    [string]$RegistryPath,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Assert-OrdinaryPath([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try {
            $attributes = [IO.File]::GetAttributes($current)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Proof paths must not traverse reparse points: $current"
            }
            if ($current -ne $Path -and -not ($attributes -band [IO.FileAttributes]::Directory)) {
                throw "Proof path ancestor is not a directory: $current"
            }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
    }
}
function Assert-WritableDirectory([string]$Path) {
    if (-not [IO.Directory]::Exists($Path)) { throw "Preflight requires an existing directory: $Path" }
    Assert-OrdinaryPath $Path
    # A delete-on-close probe checks actual write access without retaining a run artifact.
    $probe = Join-Path $Path ('.mxc-preflight-' + [guid]::NewGuid().ToString('N'))
    $handle = [IO.FileStream]::new($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    $handle.Dispose()
}
function Set-ProofEnvironmentValue([string]$Name, $Value) {
    # PowerShell can coerce a null hashtable value to an empty string for the .NET overload.
    # Remove the entry explicitly so an originally absent variable stays absent.
    if ($null -eq $Value) { Remove-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value) }
}
if (-not [IO.Path]::IsPathFullyQualified($ArtifactDirectory)) {
    throw 'ArtifactDirectory must be an absolute, caller-owned directory.'
}
$artifact = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ArtifactDirectory))
if ($artifact.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
    $artifact.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native scratch must be outside the source checkout.'
}
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) { throw 'ResultsDirectory must not be empty.' }
$results = [IO.Path]::GetFullPath($ResultsDirectory, $repo)
$source = (Resolve-Path -LiteralPath $SourceManifest).Path
Assert-OrdinaryPath $source
if (-not [IO.File]::Exists($source)) { throw 'SourceManifest must be a JSON file.' }
$sourceText = Get-Content -LiteralPath $source -Raw
$sourceData = ConvertFrom-Json -InputObject $sourceText -ErrorAction Stop
if ($null -eq $sourceData -or $sourceText.TrimStart()[0] -notin @('{', '[') -or
    ($sourceData -is [array] -and $sourceData.Count -eq 0) -or
    ($sourceData -is [pscustomobject] -and @($sourceData.PSObject.Properties).Count -eq 0)) {
    throw 'SourceManifest must contain a nonempty JSON source manifest.'
}
$sourceHash = (Get-FileHash -LiteralPath $source).Hash
$registry = if ([string]::IsNullOrWhiteSpace($RegistryPath)) { Join-Path $artifact 'runs.jsonl' }
    elseif ([IO.Path]::IsPathFullyQualified($RegistryPath)) { [IO.Path]::GetFullPath($RegistryPath) }
    else { throw 'RegistryPath must be absolute when supplied.' }
if ($registry.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $registry.Equals($results, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Directory]::Exists($registry)) { throw 'RegistryPath must name a separate JSONL file.' }
Assert-OrdinaryPath $registry
Assert-OrdinaryPath $results
if ([IO.File]::Exists($results)) { throw 'ResultsDirectory must not be a file.' }
$resultParent = $results
while (-not [IO.Directory]::Exists($resultParent)) {
    $resultParent = [IO.Path]::GetDirectoryName($resultParent)
    if (-not $resultParent) { throw 'ResultsDirectory has no existing parent.' }
}
Assert-WritableDirectory $artifact
Assert-WritableDirectory ([IO.Path]::GetDirectoryName($registry))
Assert-WritableDirectory $resultParent
if ([IO.File]::Exists($registry)) {
    $handle = [IO.File]::Open($registry, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $handle.Dispose()
}
if ($PreflightOnly) {
    return [pscustomobject]@{ ArtifactDirectory=$artifact; ResultsDirectory=$results;
        SourceManifest=$source; SourceManifestSha256=$sourceHash; RegistryPath=$registry; NativeProofRun=$false }
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$runId = 'product-mxc-' + [guid]::NewGuid().ToString('N')
$run = Join-Path $artifact ('p-' + $runId.Substring($runId.Length - 12))
New-Item -ItemType Directory -Path $run | Out-Null
New-Item -ItemType Directory -Path $results -Force | Out-Null

function Save-Json($Value, [string]$Name) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run $Name) -Encoding utf8
}
function Read-RealMetadata {
    foreach ($root in @((Join-Path $env:APPDATA 'OpenClawTray'), (Join-Path $env:LOCALAPPDATA 'OpenClawTray'))) {
        [pscustomobject]@{Root=$root;RelativePath='.';Exists=(Test-Path $root);Length=$null;Modified=$null}
        if (Test-Path $root) {
            Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
                [pscustomobject]@{Root=$root;RelativePath=[IO.Path]::GetRelativePath($root,$_.FullName);Exists=$true;
                    Length=$_.Length;Modified=$_.LastWriteTimeUtc.ToString('o')}
            }
        }
    }
}
$before = @(Read-RealMetadata)
Save-Json $before 'real-appdata-before.json'
$unowned = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match 'OpenClaw|wsl|node' -and $_.ProcessId -ne $PID
} | Select-Object ProcessId,Name,ExecutablePath,CreationDate)
Save-Json $unowned 'unowned-processes-before.json'
Save-Json @(Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess) 'unowned-listeners-before.json'
Save-Json @(Get-ScheduledTask | Where-Object TaskName -match 'OpenClaw' | Select-Object TaskName,TaskPath,State) 'unowned-tasks-before.json'
$wsl = @(& wsl.exe --list --quiet 2>&1)
Save-Json $wsl 'unowned-wsl-before.json'

$environment = @{
    OPENCLAW_REPO_ROOT=$repo; OPENCLAW_TRAY_DATA_DIR=(Join-Path $run 'data')
    OPENCLAW_TRAY_APPDATA_DIR=(Join-Path $run 'roaming'); OPENCLAW_TRAY_LOCALAPPDATA_DIR=(Join-Path $run 'local')
    OPENCLAW_RUN_MXC_NATIVE_PROOF='1'; OPENCLAW_MXC_PROOF_ROOT=$run
    OPENCLAW_MXC_SOURCE_MANIFEST=$source; OPENCLAW_MXC_LAB_REGISTRY=$registry
}
$savedEnvironment = @{}
foreach ($entry in Get-ChildItem Env: | Where-Object Name -like 'OPENCLAW_RUN_*') {
    $environment[$entry.Name] = $null
}
$environment.OPENCLAW_RUN_MXC_NATIVE_PROOF = '1'
foreach ($key in $environment.Keys) { $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key) }
$manifest = [ordered]@{
    RunId=$runId;State='running';Worktree=$repo;Head=(& git -C $repo rev-parse HEAD);
    SourceManifestSha256=$sourceHash;
    DataRoots=@($environment.OPENCLAW_TRAY_DATA_DIR,$environment.OPENCLAW_TRAY_APPDATA_DIR,$environment.OPENCLAW_TRAY_LOCALAPPDATA_DIR);
    Processes=@();Ports=@();Lane='product runner, synthetic profiles only'
}
Save-Json $manifest 'ownership.json'
$manifestPath = Join-Path $run 'ownership.json'
$passed = $false
$registered = $false
$hostProcess = $null
try {
    ([ordered]@{runId=$runId;state='running';manifest=$manifestPath;timestamp=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json -Compress) | Add-Content -LiteralPath $registry
    $registered = $true
    foreach ($key in $environment.Keys) { Set-ProofEnvironmentValue $key $environment[$key] }
    foreach ($path in $manifest.DataRoots) { New-Item -ItemType Directory -Path $path | Out-Null }
    $project = Join-Path $repo 'tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj'
    $log = Join-Path $run 'native-console.log'
    $errorLog = Join-Path $run 'native-error.log'
    $trxName = "native-userfolders-$runId.trx"
    $arguments = @('test', "`"$project`"", '--no-build', '--no-restore',
        '--filter', '"FullyQualifiedName~MxcCommandRunnerIntegrationTests"',
        '--logger', "`"trx;LogFileName=$trxName`"", '--results-directory', "`"$results`"",
        '--logger', '"console;verbosity=normal"')
    $hostProcess = Start-Process -FilePath $dotnet -ArgumentList $arguments -PassThru `
        -WorkingDirectory $repo -RedirectStandardOutput $log -RedirectStandardError $errorLog
    $manifest.Processes = @(@{Pid=$hostProcess.Id;StartTimeUtc=$hostProcess.StartTime.ToUniversalTime().ToString('o');
        Executable=$dotnet;CommandLine=('dotnet ' + ($arguments -join ' '));RunId=$runId})
    Save-Json $manifest 'ownership.json'
    $hostProcess.WaitForExit()
    if ($hostProcess.ExitCode -ne 0) { throw "Product native tests failed. See $log and $errorLog." }
    [xml]$trx = Get-Content (Join-Path $results $trxName) -Raw
    $tests = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($tests.Count -lt 2 -or @($tests | Where-Object {$_.GetAttribute('outcome') -ne 'Passed'}).Count -gt 0) {
        throw 'Native proof must execute at least two passing tests, with no skips.'
    }
    $passed = $true
} finally {
    try {
        # A failure saving ownership after Start-Process must not orphan the test host.
        if ($null -ne $hostProcess) {
            $hostProcess.WaitForExit()
            $hostProcess.Dispose()
        }
        $after = @(Read-RealMetadata)
        Save-Json $after 'real-appdata-after.json'
        $unchanged = ($before | ConvertTo-Json -Compress) -ceq ($after | ConvertTo-Json -Compress)
        $manifest.State = if ($passed -and $unchanged) {'stopped/fixtures-cleaned'} else {'failed/evidence-retained'}
        Save-Json $manifest 'ownership.json'
        Save-Json @{Passed=$passed;RealAppdataUnchanged=$unchanged;RunId=$runId;Evidence=$run} 'summary.json'
        if ($registered) {
            ([ordered]@{runId=$runId;state=$manifest.State;manifest=$manifestPath;timestamp=[DateTime]::UtcNow.ToString('o')} |
                ConvertTo-Json -Compress) | Add-Content -LiteralPath $registry
        }
        if (-not $unchanged) { throw 'Real appdata metadata changed. Isolation breach; evidence retained.' }
    } finally {
        foreach ($key in $savedEnvironment.Keys) {
            Set-ProofEnvironmentValue $key $savedEnvironment[$key]
        }
    }
}
Write-Host "Product native user-folder proof passed. Evidence: $run"
