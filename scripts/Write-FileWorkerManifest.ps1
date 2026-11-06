param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$ProductVersion,
    [switch]$RequireSignedWorker
)
$ErrorActionPreference = 'Stop'

function Assert-NoExternalVcRuntimeImport([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid worker PE image: $([IO.Path]::GetFileName($Path))" }
        $reader.BaseStream.Position = 0x3c
        $pe = $reader.ReadUInt32()
        $reader.BaseStream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid worker PE signature.' }
        $null = $reader.ReadUInt16()
        $sectionCount = $reader.ReadUInt16()
        $reader.BaseStream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24
        $reader.BaseStream.Position = $optional
        $magic = $reader.ReadUInt16()
        $directory = switch ($magic) {
            0x20b { $optional + 112 }
            0x10b { $optional + 96 }
            default { throw 'Unsupported worker PE image.' }
        }
        $sections = @()
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $reader.BaseStream.Position = $optional + $optionalSize + 40 * $i + 8
            $virtualSize = $reader.ReadUInt32()
            $virtualAddress = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $raw = $reader.ReadUInt32()
            $sections += [pscustomobject]@{ Rva = $virtualAddress; Size = [Math]::Max($virtualSize, $rawSize); Raw = $raw }
        }
        $offsetOf = {
            param([uint32]$Rva)
            foreach ($section in $sections) {
                if ($Rva -ge $section.Rva -and [long]$Rva -lt [long]$section.Rva + $section.Size) {
                    return [long]$section.Raw + $Rva - $section.Rva
                }
            }
            throw 'Invalid worker import address.'
        }
        foreach ($table in @(@{ Index = 1; Stride = 20; Name = 12 }, @{ Index = 13; Stride = 32; Name = 4 })) {
            $reader.BaseStream.Position = $directory + $table.Index * 8
            $rva = $reader.ReadUInt32()
            if ($rva -eq 0) { continue }
            $descriptor = & $offsetOf $rva
            while ($true) {
                $reader.BaseStream.Position = $descriptor
                $record = $reader.ReadBytes($table.Stride)
                if ($record.Length -ne $table.Stride) { throw 'Truncated worker import record.' }
                if (-not ($record | Where-Object { $_ -ne 0 })) { break }
                if ($table.Index -eq 13 -and [BitConverter]::ToUInt32($record, 0) -ne 1) {
                    throw 'Unsupported worker delayed-import address mode.'
                }
                $reader.BaseStream.Position = & $offsetOf ([BitConverter]::ToUInt32($record, $table.Name))
                $name = [Text.StringBuilder]::new()
                for ($i = 0; $i -lt 260; $i++) {
                    $value = $reader.ReadByte()
                    if ($value -eq 0) { break }
                    $null = $name.Append([char]$value)
                }
                if ($i -eq 260) { throw 'Oversized worker import name.' }
                if ($name.ToString() -match '^(vcruntime|msvcp|msvcr)[0-9_].*\.dll$') {
                    throw "The fixed worker unexpectedly depends on an external VC runtime: $name. Restore the native-runtime packaging requirement before adding this dependency."
                }
                $descriptor += $table.Stride
            }
        }
    } finally { $reader.Dispose() }
}

$directory = [IO.Path]::GetFullPath($PayloadDirectory)
if ($RequireSignedWorker) {
    foreach ($name in @('OpenClaw.FileWorker.exe', 'OpenClaw.FileWorker.dll')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $directory $name)
        if ($signature.Status -ne 'Valid' -or
            -not [string]::Equals($signature.SignerCertificate.Subject,
                'CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixed worker must be validly signed by the OpenClaw release signer before manifest generation: $name."
        }
    }
}
if (Get-ChildItem -LiteralPath $directory -Directory) {
    throw 'The fixed file worker must be a flat self-contained payload.'
}
$files = [ordered]@{}
Get-ChildItem -LiteralPath $directory -File |
    Where-Object Name -ne 'payload-manifest.json' |
    Sort-Object Name |
    ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse files are not valid worker payload.' }
        # MSIX drops PDBs. Normalize the shared shipping directory before hashing or copying it.
        # Build-output symbols remain available outside this publish directory.
        if ($_.Extension -eq '.pdb') {
            Remove-Item -LiteralPath $_.FullName
            return
        }
        if ($_.Extension -in @('.dll', '.exe')) { Assert-NoExternalVcRuntimeImport $_.FullName }
        $stream = [IO.File]::OpenRead($_.FullName)
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $files[$_.Name] = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        } finally {
            $hasher.Dispose()
            $stream.Dispose()
        }
    }
foreach ($required in @('OpenClaw.FileWorker.exe', 'coreclr.dll', 'OpenClaw.FileWorker.runtimeconfig.json')) {
    if (-not $files.Contains($required)) { throw "Self-contained worker is missing $required." }
}
$manifest = [ordered]@{ version = 1; rid = $RuntimeIdentifier; productVersion = $ProductVersion; files = $files }
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $directory 'payload-manifest.json'), $json, [Text.UTF8Encoding]::new($false))
