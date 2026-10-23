#requires -Version 7
<#
.SYNOPSIS
Exercises the real SetupEngine build's signed-resource boundary without signing or changing trust.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\OpenClaw.SetupEngine\OpenClaw.SetupEngine.csproj'
$verify = Join-Path $PSScriptRoot 'Test-CleanupHostSignature.ps1'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-cleanup-build-test-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporaryDirectory
$originalSigningRequirement = $env:RequireSignedCleanupHost
Remove-Item Env:RequireSignedCleanupHost -ErrorAction SilentlyContinue

function Invoke-TestBuild {
    param([string]$Name, [string[]]$Properties, [string]$ExpectedError)
    $output = Join-Path $temporaryDirectory $Name
    $log = Join-Path $temporaryDirectory "$Name.log"
    & dotnet build $project -c Release --no-restore "-p:OutDir=$output\" @Properties *> $log
    $code = $LASTEXITCODE
    if ($ExpectedError) {
        if ($code -eq 0 -or !(Get-Content -LiteralPath $log -Raw).Contains($ExpectedError)) {
            Get-Content -LiteralPath $log
            throw "Expected build rejection was not observed: $Name."
        }
        Write-Host "PASS: $Name rejected."
    }
    else {
        if ($code -ne 0) { Get-Content -LiteralPath $log; throw "Build failed: $Name." }
        return (Join-Path $output 'OpenClaw.SetupEngine.dll')
    }
}

try {
    # Use an existing OS-signed fixture. Never create a certificate or modify trust.
    $signedFixture = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    if ((Get-AuthenticodeSignature -LiteralPath $signedFixture).Status -ne 'Valid') {
        throw 'The Windows PowerShell signed fixture must have a valid signature.'
    }
    $unsignedAssembly = Invoke-TestBuild -Name unsigned-dev -Properties @(
        '-p:RequireSignedCleanupHost=false', '-p:OpenClawSignedCleanupHostPath=')
    & $verify -AssemblyPath $unsignedAssembly
    Write-Host 'PASS: unsigned development build embeds a helper.'

    Invoke-TestBuild -Name missing-signing-input -Properties @(
        '-p:OpenClawSignedCleanupHostPath=') `
        -ExpectedError 'Signed releases require OpenClawSignedCleanupHostPath'

    # The deployed development image is the actual unsigned production helper.
    $unsignedHost = Join-Path $temporaryDirectory 'unsigned-host.exe'
    $assemblyStream = [IO.File]::OpenRead($unsignedAssembly)
    $pe = [Reflection.PortableExecutable.PEReader]::new($assemblyStream)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($handle in $metadata.ManifestResources) {
            $resource = $metadata.GetManifestResource($handle)
            if ($metadata.GetString($resource.Name) -cne 'OpenClaw.SetupEngine.GatewayCleanupHost.exe') { continue }
            $block = $pe.GetSectionData($pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
            $reader = $block.GetReader([int]$resource.Offset, $block.Length - [int]$resource.Offset)
            [IO.File]::WriteAllBytes($unsignedHost, $reader.ReadBytes($reader.ReadInt32()))
            break
        }
    }
    finally { $pe.Dispose(); $assemblyStream.Dispose() }
    Invoke-TestBuild -Name unsigned-signing-input -Properties @(
        '-p:RequireSignedCleanupHost=true', "-p:OpenClawSignedCleanupHostPath=$unsignedHost") `
        -ExpectedError 'not validly Authenticode-signed'

    $signedAssembly = Invoke-TestBuild -Name signed-input -Properties @(
        '-p:RequireSignedCleanupHost=true', "-p:OpenClawSignedCleanupHostPath=$signedFixture")
    & $verify -AssemblyPath $signedAssembly -HostPath $signedFixture -RequireSigned
    Write-Host 'PASS: actual SetupEngine resource matches the signed input and retains its signature.'

    # Exit status is part of the verifier's observable contract, not a source marker.
    & pwsh -NoProfile -File $verify -AssemblyPath $signedAssembly -HostPath $unsignedHost *> (Join-Path $temporaryDirectory 'mismatch.log')
    if ($LASTEXITCODE -eq 0) { throw 'Verifier accepted a stale/different embedded helper.' }
    Write-Host 'PASS: mismatched embedded helper rejected.'
}
finally {
    [IO.Directory]::Delete($temporaryDirectory, $true)
    if ($null -eq $originalSigningRequirement) { Remove-Item Env:RequireSignedCleanupHost -ErrorAction SilentlyContinue }
    else { $env:RequireSignedCleanupHost = $originalSigningRequirement }
}
Write-Host 'Cleanup signing regression: 5 cases passed.'
exit 0
