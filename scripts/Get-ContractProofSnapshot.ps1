[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$TestAssemblyPath,
    [Parameter(Mandatory)][string]$LvtPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Get-Payload([string]$Path, [string]$Role, [string]$ExpectedName, [switch]$RepositoryOwned) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ([IO.Path]::GetFileName($resolved) -cne $ExpectedName) { throw "Wrong $Role entry point." }
    if ($RepositoryOwned -and -not $resolved.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Role payload must be built in this worktree."
    }
    $root = Split-Path -Parent $resolved
    $directory = Get-Item -LiteralPath $root
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a $Role payload rooted at a reparse point." }
    $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
        throw "Refusing a $Role payload containing a reparse point."
    }
    @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Role = $Role
            Path = [IO.Path]::GetRelativePath($root, $_.FullName)
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}

$app = (Resolve-Path -LiteralPath $AppPath).Path
$identityPath = Join-Path (Split-Path -Parent $app) 'app-identity.txt'
if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf) -or
    (Get-Content -LiteralPath $identityPath -Raw).Trim() -cne 'release') {
    throw 'Contract proof requires a non-Dev app identity marker.'
}
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish source HEAD.' }
$paths = @(& git -C $repoRoot diff --name-only HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish tracked source changes.' }
$paths += @(& git -C $repoRoot ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish untracked source changes.' }
$dirty = @($paths | Sort-Object -Unique | ForEach-Object {
    $path = Join-Path $repoRoot $_
    [pscustomobject]@{
        Path = $_
        Sha256 = if (Test-Path -LiteralPath $path -PathType Leaf) {
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        } else { 'deleted' }
    }
})
$files = @(Get-Payload $app 'app' 'OpenClaw.Tray.WinUI.exe' -RepositoryOwned)
$files += @(Get-Payload $TestAssemblyPath 'tests' 'OpenClaw.Tray.UITests.dll' -RepositoryOwned)
$files += @(Get-Payload $LvtPath 'driver' 'lvt.exe')
$driver = & (Join-Path $PSScriptRoot 'Get-ContractLvtRelease.ps1')
if (($files | Where-Object { $_.Role -ceq 'driver' -and $_.Path -ceq 'lvt.exe' }).Sha256 -cne
    $driver.ExecutableSha256) {
    throw 'Driver payload does not contain the pinned lvt executable.'
}
[pscustomobject]@{
    Head = $head
    Dirty = $dirty
    Payloads = $files
    AppIdentity = 'release'
    Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    OsVersion = [Environment]::OSVersion.Version.ToString()
    LvtVersion = $driver.Version
}
