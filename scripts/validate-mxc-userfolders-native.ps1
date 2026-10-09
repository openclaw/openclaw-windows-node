<#
.SYNOPSIS
    Validates the product MXC runner against owned synthetic user folders only.
.DESCRIPTION
    Does not start a tray, Gateway, WSL, listener, service or elevated process.
    Requires an explicit artifact directory outside the checkout. Real appdata
    metadata is compared without reading credential contents. Native skips fail.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$SourceManifest,
    [string]$ResultsDirectory = 'TestResults\MxcDotNetUserFolders'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifact = [IO.Path]::GetFullPath($ArtifactDirectory)
if ($artifact.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native scratch must be outside the source checkout.'
}
$runId = 'product-mxc-' + [guid]::NewGuid().ToString('N')
$run = Join-Path $artifact ('p-' + $runId.Substring($runId.Length - 12))
New-Item -ItemType Directory -Path $run | Out-Null
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$registry = 'D:\second brain\work\openclaw\runtime-lab\runs.jsonl'

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

$env:OPENCLAW_REPO_ROOT = $repo
$env:OPENCLAW_TRAY_DATA_DIR = Join-Path $run 'data'
$env:OPENCLAW_TRAY_APPDATA_DIR = Join-Path $run 'roaming'
$env:OPENCLAW_TRAY_LOCALAPPDATA_DIR = Join-Path $run 'local'
$env:OPENCLAW_RUN_MXC_NATIVE_PROOF = '1'
$env:OPENCLAW_MXC_PROOF_ROOT = $run
$env:OPENCLAW_MXC_SOURCE_MANIFEST = (Resolve-Path $SourceManifest).Path
$env:OPENCLAW_MXC_LAB_REGISTRY = $registry
foreach ($path in @($env:OPENCLAW_TRAY_DATA_DIR,$env:OPENCLAW_TRAY_APPDATA_DIR,$env:OPENCLAW_TRAY_LOCALAPPDATA_DIR)) {
    New-Item -ItemType Directory -Path $path | Out-Null
}
$manifest = [ordered]@{
    RunId=$runId;State='running';Worktree=$repo;Head=(& git -C $repo rev-parse HEAD);
    SourceManifestSha256=(Get-FileHash $SourceManifest).Hash;
    DataRoots=@($env:OPENCLAW_TRAY_DATA_DIR,$env:OPENCLAW_TRAY_APPDATA_DIR,$env:OPENCLAW_TRAY_LOCALAPPDATA_DIR);
    Processes=@();Ports=@();Lane='product runner, synthetic profiles only'
}
Save-Json $manifest 'ownership.json'
$manifestPath = Join-Path $run 'ownership.json'
([ordered]@{runId=$runId;state='running';manifest=$manifestPath;timestamp=[DateTime]::UtcNow.ToString('o')} |
    ConvertTo-Json -Compress) | Add-Content -LiteralPath $registry
$passed = $false
try {
    $project = Join-Path $repo 'tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj'
    $log = Join-Path $run 'native-console.log'
    $errorLog = Join-Path $run 'native-error.log'
    $arguments = @('test', "`"$project`"", '--no-build', '--no-restore',
        '--filter', '"FullyQualifiedName~MxcCommandRunnerIntegrationTests"',
        '--logger', '"trx;LogFileName=native-userfolders.trx"', '--results-directory', "`"$results`"",
        '--logger', '"console;verbosity=normal"')
    $hostProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -PassThru `
        -WorkingDirectory $repo -RedirectStandardOutput $log -RedirectStandardError $errorLog
    $manifest.Processes = @(@{Pid=$hostProcess.Id;StartTimeUtc=$hostProcess.StartTime.ToUniversalTime().ToString('o');
        Executable=(Get-Command dotnet).Source;CommandLine=('dotnet ' + ($arguments -join ' '));RunId=$runId})
    Save-Json $manifest 'ownership.json'
    $hostProcess.WaitForExit()
    if ($hostProcess.ExitCode -ne 0) { throw "Product native tests failed. See $log and $errorLog." }
    [xml]$trx = Get-Content (Join-Path $results 'native-userfolders.trx') -Raw
    $tests = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($tests.Count -lt 2 -or @($tests | Where-Object {$_.GetAttribute('outcome') -ne 'Passed'}).Count -gt 0) {
        throw 'Native proof must execute at least two passing tests, with no skips.'
    }
    $passed = $true
} finally {
    $after = @(Read-RealMetadata)
    Save-Json $after 'real-appdata-after.json'
    $unchanged = ($before | ConvertTo-Json -Compress) -ceq ($after | ConvertTo-Json -Compress)
    $manifest.State = if ($passed -and $unchanged) {'stopped/fixtures-cleaned'} else {'failed/evidence-retained'}
    Save-Json $manifest 'ownership.json'
    Save-Json @{Passed=$passed;RealAppdataUnchanged=$unchanged;RunId=$runId;Evidence=$run} 'summary.json'
    ([ordered]@{runId=$runId;state=$manifest.State;manifest=$manifestPath;timestamp=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json -Compress) | Add-Content -LiteralPath $registry
    if (-not $unchanged) { throw 'Real appdata metadata changed. Isolation breach; evidence retained.' }
}
Write-Host "Product native user-folder proof passed. Evidence: $run"
