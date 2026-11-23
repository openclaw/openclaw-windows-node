<#
.SYNOPSIS
    Run ./scripts/test-candidate-gitversion-environment.ps1 on Windows to prove
    candidate builds avoid shared GITHUB_ENV export without losing versions.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This regression requires native Windows file-sharing semantics.' }
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) "openclaw-candidate-version-$([guid]::NewGuid().ToString('N'))"
$names = @('GITHUB_ACTIONS', 'GITHUB_REF', 'GITHUB_REF_TYPE', 'GITHUB_SHA',
    'GITHUB_HEAD_REF', 'GITHUB_BASE_REF', 'GITHUB_ENV',
    'GitVersion_NoNormalizeEnabled', 'GitVersion_NoFetchEnabled')
$saved = @{}
foreach ($name in $names) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
function Invoke-FixtureGit {
    & git -C $fixture @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($args[0])" }
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $props = [xml](Get-Content (Join-Path $root 'src/Directory.Build.props') -Raw)
    $packageVersion = $props.SelectSingleNode('//PackageReference[@Include="GitVersion.MsBuild"]').Version
    $workflow = Get-Content (Join-Path $root '.github/workflows/release-candidate-e2e.yml') -Raw
    $builds = @([regex]::Matches($workflow, '(?m)^ +dotnet build [^\r\n]+'))
    if ($builds.Count -ne 2) { throw 'Expected the maintained harness and declared app build commands.' }
    $property = '/p:WriteVersionInfoToBuildLog=false'
    foreach ($build in $builds) {
        if (-not $build.Value.TrimEnd().EndsWith(" $property", [StringComparison]::Ordinal)) {
            throw "Candidate build must suppress only build-server export: $($build.Value.Trim())"
        }
    }
    Invoke-FixtureGit init --quiet --initial-branch=main
    Invoke-FixtureGit config user.name 'Candidate version fixture'
    Invoke-FixtureGit config user.email 'candidate@example.invalid'
    Invoke-FixtureGit config commit.gpgsign false
    Invoke-FixtureGit config tag.gpgsign false
    Copy-Item (Join-Path $root 'GitVersion.yml') $fixture
    $projectXml = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="GitVersion.MsBuild" Version="__VERSION__" PrivateAssets="all" /></ItemGroup>
  <Target Name="RecordVersionProperties" AfterTargets="Build">
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/version-properties.txt" Lines="$(GitVersion_SemVer);$(GitVersion_Sha);$(Version)" Overwrite="true" />
  </Target>
</Project>
'@
    $projectXml.Replace('__VERSION__', $packageVersion) | Set-Content (Join-Path $fixture 'VersionProbe.csproj')
    Invoke-FixtureGit add GitVersion.yml VersionProbe.csproj
    Invoke-FixtureGit commit --quiet -m 'Candidate version baseline'
    Invoke-FixtureGit tag v2026.11.2
    $sha = (Invoke-FixtureGit rev-parse HEAD).Trim()
    $project = Join-Path $fixture 'VersionProbe.csproj'
    & dotnet restore $project --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Pinned GitVersion.MsBuild restore failed.' }
    $env:GITHUB_ACTIONS = 'true'
    $env:GITHUB_REF = 'refs/tags/v2026.11.2'
    $env:GITHUB_REF_TYPE = 'tag'
    $env:GITHUB_SHA = $sha
    $env:GITHUB_ENV = Join-Path $fixture 'synthetic-github.env'
    $env:GitVersion_NoNormalizeEnabled = 'true'
    $env:GitVersion_NoFetchEnabled = 'true'
    $arguments = @($project, '--no-restore', '--no-incremental', '-nologo', '-verbosity:minimal',
        '-property:GitVersion_NoCacheEnabled=true')
    $baseline = & dotnet build @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Unlocked baseline failed: $baseline" }
    $export = Get-Content $env:GITHUB_ENV -Raw
    if ($export -notmatch '(?m)^GitVersion_SemVer=2026\.11\.2\r?$') {
        throw 'Unlocked baseline did not exercise the GitHub Actions exporter.'
    }
    Write-Host "BASELINE: GitVersion.MsBuild $packageVersion builds and exports SemVer=2026.11.2."
    # FileShare.Read permits readers but deterministically rejects AppendText's writer.
    $lock = [IO.File]::Open($env:GITHUB_ENV, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $red = & dotnet build @arguments 2>&1
        $redExit = $LASTEXITCODE
        $redText = $red -join [Environment]::NewLine
        if ($redExit -eq 0 -or $redText -notmatch 'WriteVersionInfoToBuildLog' -or
            $redText -notmatch 'GitVersion\.Agents\.GitHubActions\.WriteIntegration' -or
            $redText -notmatch 'being used by another process') {
            throw "Negative control missed the actual exporter sharing violation (exit $redExit): $redText"
        }
        Write-Host "RED: same build without property fails (exit $redExit) in the GitHub Actions exporter:"
        Write-Host $redText
        $green = & dotnet build @arguments $property 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Locked build with candidate workflow property failed: $green" }
        Write-Host ($green -join [Environment]::NewLine)
    } finally { $lock.Dispose() }
    $version = Get-Content (Join-Path $fixture 'obj/gitversion.json') -Raw | ConvertFrom-Json
    $properties = @(Get-Content (Join-Path $fixture 'version-properties.txt'))
    $assembly = Join-Path $fixture 'bin/Debug/net10.0/VersionProbe.dll'
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    $generated = @(Get-ChildItem (Join-Path $fixture 'obj') -Recurse -Filter '*GitVersionInformation*')
    if ($version.SemVer -cne '2026.11.2' -or $version.Sha -cne $sha -or
        ($properties -join '|') -cne "2026.11.2|$sha|2026.11.2" -or
        $metadata.ProductVersion.Split('+')[0] -cne '2026.11.2' -or
        $metadata.FileVersion -cne $version.AssemblySemFileVer -or $generated.Count -eq 0 -or
        (Get-Content $env:GITHUB_ENV -Raw) -cne $export) {
        throw 'Fixed build lost calculated properties, generated version information, assembly metadata, or wrote to GITHUB_ENV.'
    }
    Write-Host "GREEN: same locked path, $property, SemVer/Version/ProductVersion=2026.11.2; Sha=$sha; FileVersion=$($metadata.FileVersion); generated GitVersionInformation present; GITHUB_ENV unchanged."
    Write-Host 'Passed: 1 unlocked exporter baseline, 1 expected native sharing violation, 1 fixed build with version and assembly checks.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
