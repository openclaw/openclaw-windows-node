#requires -Version 7
<#
.SYNOPSIS
Verifies the cleanup helper before embedding and checks the actual embedded bytes.
#>
[CmdletBinding()]
param(
    [string]$HostPath,
    [string]$AssemblyPath,
    [switch]$RequireSigned,
    [string]$SignerSubject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$HostPath -and !$AssemblyPath) { throw 'Specify HostPath or AssemblyPath.' }
$temporaryDirectory = $null
try {
    $path = $HostPath
    if ($AssemblyPath) {
        $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-cleanup-signature-" + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $temporaryDirectory
        $path = Join-Path $temporaryDirectory 'OpenClaw.GatewayCleanupHost.exe'
        $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $AssemblyPath).Path)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $found = $false
            foreach ($handle in $metadata.ManifestResources) {
                $resource = $metadata.GetManifestResource($handle)
                if ($metadata.GetString($resource.Name) -cne 'OpenClaw.SetupEngine.GatewayCleanupHost.exe') { continue }
                if (!$resource.Implementation.IsNil) { throw 'Cleanup helper must be embedded, not linked.' }
                $block = $pe.GetSectionData($pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
                $reader = $block.GetReader([int]$resource.Offset, $block.Length - [int]$resource.Offset)
                $bytes = $reader.ReadBytes($reader.ReadInt32())
                [IO.File]::WriteAllBytes($path, $bytes)
                $found = $true
                break
            }
            if (!$found) { throw 'SetupEngine is missing its embedded cleanup helper.' }
        }
        finally { $pe.Dispose(); $stream.Dispose() }
        if ($HostPath -and (Get-FileHash -LiteralPath $HostPath).Hash -cne (Get-FileHash -LiteralPath $path).Hash) {
            throw 'Embedded cleanup helper differs from the signing input.'
        }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($RequireSigned -and $signature.Status -ne 'Valid') {
        throw "Cleanup helper is not validly Authenticode-signed: $($signature.Status)."
    }
    if ($RequireSigned -and $SignerSubject -and
        ![string]::Equals($signature.SignerCertificate.Subject, $SignerSubject, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup helper is not signed by the expected release signer.'
    }
    Write-Host "Cleanup helper verified: signature=$($signature.Status); sha256=$((Get-FileHash -LiteralPath $path).Hash); embedded=$([bool]$AssemblyPath)"
}
finally {
    if ($temporaryDirectory) { [IO.Directory]::Delete($temporaryDirectory, $true) }
}
exit 0
