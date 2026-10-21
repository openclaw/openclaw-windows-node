<#
.SYNOPSIS
    Portable preflight/failure-path tests only. No native MXC tests or fixtures are launched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$OutputDirectory = 'TestResults\MxcReviewFixes\Plumbing'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script = Join-Path $repo 'scripts\validate-mxc-userfolders-native.ps1'
$root = Join-Path ([IO.Path]::GetFullPath($ArtifactDirectory)) ('plumbing-' + [guid]::NewGuid().ToString('N'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root,$output -Force | Out-Null
$source = Join-Path $root 'source.json'
Set-Content $source '{"Lane":"plumbing fixture, not native proof","Files":[{"Path":"synthetic","SHA256":"not-native-evidence"}]}'
$artifact = Join-Path $root 'owned artifacts'
New-Item -ItemType Directory -Path $artifact | Out-Null
$parameters = @{ArtifactDirectory=$artifact; SourceManifest=$source; ResultsDirectory=(Join-Path $root 'results')}
$checks = [Collections.Generic.List[string]]::new()
function Assert-Condition([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-PreflightFailure([string]$Case, [hashtable]$Overrides) {
    $argsForCase = $parameters.Clone()
    foreach ($key in $Overrides.Keys) { $argsForCase[$key] = $Overrides[$key] }
    $before = @(Get-ChildItem -LiteralPath $artifact -Recurse -Force | Select-Object -ExpandProperty FullName)
    $failed = $false
    try { & $script @argsForCase -PreflightOnly | Out-Null } catch { $failed = $true }
    Assert-Condition $failed "$Case did not fail preflight."
    $after = @(Get-ChildItem -LiteralPath $artifact -Recurse -Force | Select-Object -ExpandProperty FullName)
    Assert-Condition (($before -join '|') -eq ($after -join '|')) "$Case created run artifacts during failed preflight."
    $checks.Add($Case)
}
# All potentially live inventory and launch commands are shadowed in this plumbing-only harness.
function Get-CimInstance { @() }
function Get-NetTCPConnection { @() }
function Get-ScheduledTask { @() }
function wsl.exe { @() }
$plumbingState = @{LaunchCalls=0; FailRegistration=$false}
function Add-Content {
    param([Parameter(ValueFromPipeline)]$Value, [string]$LiteralPath)
    process {
        if ($plumbingState.FailRegistration) { throw 'PLUMBING_ONLY_REGISTRATION_FAILURE' }
        Microsoft.PowerShell.Management\Add-Content -LiteralPath $LiteralPath -Value $Value
    }
}
function Start-Process {
    $plumbingState.LaunchCalls++
    Assert-Condition ($env:OPENCLAW_RUN_MXC_NATIVE_PROOF -eq '1') 'Native flag missing at launch seam.'
    Assert-Condition (-not $env:OPENCLAW_RUN_E2E) 'Unrelated E2E flag leaked into native lane.'
    Assert-Condition (@($env:OPENCLAW_TRAY_DATA_DIR,$env:OPENCLAW_TRAY_APPDATA_DIR,$env:OPENCLAW_TRAY_LOCALAPPDATA_DIR |
        Select-Object -Unique).Count -eq 3) 'Data roots must remain distinct.'
    throw 'PLUMBING_ONLY_LAUNCH_FAILURE'
}
$saved = @{}
foreach ($key in @('OPENCLAW_REPO_ROOT','OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR',
    'OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_RUN_MXC_NATIVE_PROOF','OPENCLAW_RUN_E2E',
    'OPENCLAW_MXC_PROOF_ROOT','OPENCLAW_MXC_SOURCE_MANIFEST','OPENCLAW_MXC_LAB_REGISTRY')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $result = & $script @parameters -PreflightOnly
    Assert-Condition ($result.RegistryPath -eq (Join-Path $artifact 'runs.jsonl')) 'Default registry escaped the artifact root.'
    Assert-Condition (-not $result.NativeProofRun) 'Preflight claimed native proof.'
    Assert-Condition (-not (Test-Path $parameters.ResultsDirectory)) 'Preflight created result directory.'
    $checks.Add('default registry on caller-owned alternate root')
    $explicit = Join-Path $root 'explicit.jsonl'
    $result = & $script @parameters -RegistryPath $explicit -PreflightOnly
    Assert-Condition ($result.RegistryPath -eq $explicit) 'Explicit registry was not respected.'
    $checks.Add('explicit registry')
    Expect-PreflightFailure 'missing source' @{SourceManifest=(Join-Path $root 'missing.json')}
    $invalid = Join-Path $root 'invalid.json'
    Set-Content $invalid '{invalid'
    Expect-PreflightFailure 'malformed source' @{SourceManifest=$invalid}
    Set-Content $invalid 'null'
    Expect-PreflightFailure 'null source' @{SourceManifest=$invalid}
    Set-Content $invalid '{}'
    Expect-PreflightFailure 'empty source' @{SourceManifest=$invalid}
    Expect-PreflightFailure 'missing registry parent' @{RegistryPath=(Join-Path $root 'missing\runs.jsonl')}
    Expect-PreflightFailure 'registry is source' @{RegistryPath=$source}
    Expect-PreflightFailure 'registry is directory' @{RegistryPath=$root}
    Expect-PreflightFailure 'relative registry' @{RegistryPath='relative.jsonl'}
    Expect-PreflightFailure 'checkout scratch' @{ArtifactDirectory=$repo}
    Expect-PreflightFailure 'result is file' @{ResultsDirectory=$source}
    Expect-PreflightFailure 'registry is result directory' @{RegistryPath=$parameters.ResultsDirectory}
    Expect-PreflightFailure 'file ancestor in results' @{ResultsDirectory=($source + '\results')}
    $denied = Join-Path $root 'denied'
    New-Item -ItemType Directory -Path $denied | Out-Null
    $acl = Get-Acl -LiteralPath $denied
    try {
        $restricted = Get-Acl -LiteralPath $denied
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent().User,
            [Security.AccessControl.FileSystemRights]::WriteData, [Security.AccessControl.AccessControlType]::Deny)
        $restricted.AddAccessRule($rule)
        Set-Acl -LiteralPath $denied -AclObject $restricted
        Expect-PreflightFailure 'denied registry parent' @{RegistryPath=(Join-Path $denied 'runs.jsonl')}
    } finally { Set-Acl -LiteralPath $denied -AclObject $acl }
    Assert-Condition ($plumbingState.LaunchCalls -eq 0) 'Preflight launched a process.'
    $env:OPENCLAW_RUN_E2E = 'plumbing-sentinel'
    $expected = @{}
    foreach ($key in $saved.Keys) { $expected[$key] = [Environment]::GetEnvironmentVariable($key) }
    $failure = ''
    try { & $script @parameters | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-Condition ($failure -eq 'PLUMBING_ONLY_LAUNCH_FAILURE') "Unexpected launch-seam failure: $failure"
    Assert-Condition ($plumbingState.LaunchCalls -eq 1) 'Expected one intercepted launch and no real launch.'
    foreach ($key in $expected.Keys) {
        $actual = [Environment]::GetEnvironmentVariable($key)
        Assert-Condition ($actual -ceq $expected[$key]) "$key was not restored."
    }
    $checks.Add('environment restoration after intercepted launch failure')
    $registrations = @(Get-Content (Join-Path $artifact 'runs.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-Condition ($registrations.Count -eq 2 -and $registrations[-1].state -eq 'failed/evidence-retained') 'Failed run was not finalized.'
    $checks.Add('failure registration and finalization')
    $plumbingState.FailRegistration = $true
    $failure = ''
    try { & $script @parameters | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-Condition ($failure -eq 'PLUMBING_ONLY_REGISTRATION_FAILURE') "Unexpected registration failure: $failure"
    Assert-Condition ($plumbingState.LaunchCalls -eq 1) 'Registration failure started a native process.'
    foreach ($key in $expected.Keys) {
        Assert-Condition ([Environment]::GetEnvironmentVariable($key) -ceq $expected[$key]) "$key changed after registration failure."
    }
    $checks.Add('registration failure restores environment without launching')
    $text = Get-Content $script -Raw
    $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
    Assert-Condition ($ast.ScriptRequirements.RequiredPSVersion -eq [version]'7.2') 'Native proof requires PowerShell 7.2 before execution.'
    $checks.Add('PowerShell 7.2 prerequisite')
    Assert-Condition ($text -notmatch '\$registry\s*=\s*''[A-Z]:\\') 'A fixed absolute registry path remains.'
    Assert-Condition ($text.Contains('$tests.Count -lt 2') -and $text.Contains("GetAttribute('outcome') -ne 'Passed'")) 'Native zero-skip gate changed.'
    Assert-Condition ($text.Contains('"native-userfolders-$runId.trx"')) 'Native proof must not reuse a stale TRX from another run.'
    $checks.Add('native no-skips gate retained (source contract only)')
} finally {
    foreach ($key in $saved.Keys) {
        if ($null -eq $saved[$key]) { Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, [string]$saved[$key]) }
    }
    @{Lane='Plumbing only. No native proof was run.';Checks=$checks;InterceptedLaunches=$plumbingState.LaunchCalls;Root=$root} |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'summary.json')
}
Write-Host "Native proof plumbing passed $($checks.Count) checks. No native tests launched. Evidence: $output"
