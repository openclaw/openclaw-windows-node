<#
.SYNOPSIS
    Tests release signing policy without signing or changing installed applications.

.PARAMETER PublishedFixtures
    Also verify immutable v2026.9.4 release assets and task-owned tampered copies
    with Windows Authenticode. Downloads about 550 MB into temporary storage.

.PARAMETER UpdateProofHost
    Optional dedicated C# proof host DLL. Verifies the same ZIP downloads and
    exercises Updatum only in a disposable child app, with relaunch disabled.
#>
[CmdletBinding()]
param([switch]$PublishedFixtures, [string]$UpdateProofHost)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($UpdateProofHost -and -not $PublishedFixtures) { throw "Update proof requires published fixtures." }
$validator = Join-Path $PSScriptRoot "Test-ReleaseExecutableSignatures.ps1"
$expectedSubject = "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US"
$ownedNames = @(
    "OpenClaw.Tray.WinUI.exe", "OpenClaw.Tray.WinUI.dll", "OpenClaw.Chat.dll",
    "OpenClaw.Connection.dll", "OpenClaw.SetupEngine.UI.dll", "OpenClaw.SetupEngine.dll",
    "OpenClaw.Shared.dll", "OpenClawTray.FunctionalUI.dll"
)
$installerNames = @("OpenClawCompanion-Setup-x64.exe", "OpenClawCompanion-Setup-arm64.exe")
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-signature-tests-$([Guid]::NewGuid().ToString('N'))"
$script:passed = 0

function New-Signature {
    param(
        [string]$Status = "Valid",
        [string]$Subject = $expectedSubject,
        [bool]$Timestamp = $true
    )
    [pscustomobject]@{
        Status = $Status
        SignerCertificate = if ($Subject) { [pscustomobject]@{ Subject = $Subject } } else { $null }
        TimeStamperCertificate = if ($Timestamp) { [pscustomobject]@{ Subject = "Test timestamp authority" } } else { $null }
    }
}

function Assert-Policy {
    param(
        [string]$Name,
        [hashtable]$Arguments,
        [hashtable]$FakeSignatures,
        [string]$ExpectedError
    )
    # Shadow the cmdlet only inside this offline case. Published cases below
    # call the real Windows cmdlet through the unchanged verifier entry point.
    if ($null -ne $FakeSignatures) {
        function Get-AuthenticodeSignature {
            param([string]$LiteralPath)
            $key = [IO.Path]::GetFileName($LiteralPath)
            if (-not $FakeSignatures.ContainsKey($key)) { throw "No signature fixture for $key." }
            $FakeSignatures[$key]
        }
    }
    $failure = $null
    try {
        & $validator @Arguments | Out-Null
    } catch {
        $failure = $_.Exception.Message
    }
    if ($ExpectedError) {
        if ($null -eq $failure -or -not $failure.Contains($ExpectedError)) {
            throw "Case '$Name': expected '$ExpectedError', received '$failure'."
        }
    } elseif ($null -ne $failure) {
        throw "Case '$Name': unexpected rejection: $failure"
    }
    $script:passed++
    Write-Host "Passed: $Name"
}

function Invoke-OfflineCase {
    param(
        [string]$Name,
        [switch]$Installers,
        [string]$ChangedFile,
        [object]$Signature,
        [string]$MissingFile,
        [string]$ExtraFile,
        [switch]$UnsignedPayload,
        [string]$ExpectedError
    )
    $path = Join-Path $fixtureRoot "case-$script:passed"
    $names = if ($Installers) { $installerNames } else { $ownedNames + "tools\mxc\x64\wxc-exec.exe" }
    if ($ExtraFile) { $names += $ExtraFile }
    $signatures = @{}
    foreach ($name in $names) {
        if ($name -eq $MissingFile) { continue }
        $file = Join-Path $path $name
        [IO.Directory]::CreateDirectory((Split-Path -Parent $file)) | Out-Null
        [IO.File]::WriteAllText($file, "offline signature fixture")
        $leaf = [IO.Path]::GetFileName($file)
        $signatures[$leaf] = if ($name -eq $ChangedFile) { $Signature } elseif ($name -like "tools\*") {
            New-Signature -Status NotSigned -Subject "" -Timestamp $false
        } else { New-Signature }
    }
    $arguments = if ($Installers) { @{ InstallerPath = $path } } else {
        @{ PayloadPath = $path; RequireSignedOpenClaw = -not $UnsignedPayload }
    }
    Assert-Policy -Name $Name -Arguments $arguments -FakeSignatures $signatures -ExpectedError $ExpectedError
}

function Assert-TamperedFileRejected {
    param([string]$Path, [hashtable]$Arguments, [string]$Name)
    $backup = "$Path.original"
    Copy-Item -LiteralPath $Path -Destination $backup
    try {
        # The DOS stub is covered by the Authenticode digest. Do not append
        # bytes: some PE signature layouts exclude trailing certificate data.
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $stream.Position = 64
            $value = $stream.ReadByte()
            if ($value -lt 0) { throw "Signature fixture is not a complete PE file." }
            $stream.Position = 64
            $stream.WriteByte([byte]($value -bxor 1))
        } finally { $stream.Dispose() }
        Assert-Policy -Name $Name -Arguments $Arguments -ExpectedError "not validly signed"
    } finally {
        Move-Item -LiteralPath $backup -Destination $Path -Force
    }
}

try {
    foreach ($installers in @($false, $true)) {
        $mode = if ($installers) { "installers" } else { "payload" }
        $file = if ($installers) { $installerNames[0] } else { $ownedNames[0] }
        Invoke-OfflineCase -Name "$mode accepts trusted timestamped signatures" -Installers:$installers
        foreach ($status in @("NotSigned", "HashMismatch", "NotTrusted", "UnknownError")) {
            Invoke-OfflineCase -Name "$mode rejects $status" -Installers:$installers -ChangedFile $file `
                -Signature (New-Signature -Status $status) -ExpectedError "not validly signed"
        }
        Invoke-OfflineCase -Name "$mode rejects another trusted publisher" -Installers:$installers -ChangedFile $file `
            -Signature (New-Signature -Subject "CN=Other Publisher, O=Example") -ExpectedError "expected OpenClaw signer"
        Invoke-OfflineCase -Name "$mode rejects missing signer" -Installers:$installers -ChangedFile $file `
            -Signature (New-Signature -Subject "") -ExpectedError "expected OpenClaw signer"
        Invoke-OfflineCase -Name "$mode rejects missing timestamp" -Installers:$installers -ChangedFile $file `
            -Signature (New-Signature -Timestamp $false) -ExpectedError "no Authenticode timestamp"
        Invoke-OfflineCase -Name "$mode accepts publisher subject casing" -Installers:$installers -ChangedFile $file `
            -Signature (New-Signature -Subject $expectedSubject.ToUpperInvariant())
        Invoke-OfflineCase -Name "$mode rejects missing $file" -Installers:$installers -MissingFile $file `
            -ExpectedError "Missing OpenClaw binary"
        Invoke-OfflineCase -Name "$mode rejects unknown executable" -Installers:$installers -ExtraFile "unexpected.exe" `
            -ExpectedError "Unknown executable"
    }
    Invoke-OfflineCase -Name "installers require ARM64 output too" -Installers -MissingFile $installerNames[1] `
        -ExpectedError "Missing OpenClaw binary"
    foreach ($file in $ownedNames[1..($ownedNames.Length - 1)]) {
        Invoke-OfflineCase -Name "payload requires $file" -MissingFile $file -ExpectedError "Missing OpenClaw binary"
    }
    Invoke-OfflineCase -Name "payload allows unsigned development outputs" -ChangedFile $ownedNames[0] -UnsignedPayload `
        -Signature (New-Signature -Status NotSigned -Subject "" -Timestamp $false)
    Invoke-OfflineCase -Name "payload rejects OpenClaw-signed third party" -ChangedFile "tools\mxc\x64\wxc-exec.exe" `
        -Signature (New-Signature) -ExpectedError "Third-party binary appears to be signed by OpenClaw"
    Invoke-OfflineCase -Name "payload preserves third-party timestamp policy" -ChangedFile "tools\mxc\x64\wxc-exec.exe" `
        -Signature (New-Signature -Subject "CN=Microsoft Windows, O=Microsoft Corporation, C=US" -Timestamp $false)
    Invoke-OfflineCase -Name "payload requires MXC executable" -MissingFile "tools\mxc\x64\wxc-exec.exe" `
        -ExpectedError "Missing tools\mxc"
    Invoke-OfflineCase -Name "payload rejects unknown owned DLL" -ExtraFile "OpenClaw.Unexpected.dll" `
        -ExpectedError "Unknown OpenClaw binary"

    if ($PublishedFixtures) {
        if (-not $IsWindows) { throw "Published signature proof requires Windows." }
        # These are existing immutable release inputs, never candidate builds.
        # Pin bytes so asset replacement cannot silently change proof coverage.
        $assets = [ordered]@{
            "OpenClawCompanion-Setup-x64.exe" = "5e9d2d248383a5ab22ca6ffb31c1a142a23b23d273bda774877fe4fd2ac6a3ce"
            "OpenClawCompanion-Setup-arm64.exe" = "0901c7212c2fe2a14e84dd81834923b7f43f34f441ed3653d45a2af18449ae39"
            "OpenClawTray-2026.9.4-win-x64.zip" = "719b88f8d8f7de6a0fb2293565ae56e30693c96763047f33f9e3cfcae6f2b600"
            "OpenClawTray-2026.9.4-win-arm64.zip" = "d0972138d6e2a36e4646414cb1e89bf9ad2532434832887bdc7cc35317415522"
        }
        $installerRoot = Join-Path $fixtureRoot "published-installers"
        [IO.Directory]::CreateDirectory($installerRoot) | Out-Null
        foreach ($name in $assets.Keys) {
            $destination = if ($name.EndsWith(".exe")) { Join-Path $installerRoot $name } else { Join-Path $fixtureRoot $name }
            Invoke-WebRequest -Uri "https://github.com/openclaw/openclaw-windows-node/releases/download/v2026.9.4/$name" -OutFile $destination
            $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($hash -ne $assets[$name]) { throw "Published fixture SHA-256 mismatch: $name" }
            Write-Host "Verified fixture: $name SHA256=$hash"
            if ($name.EndsWith(".zip")) {
                $payload = Join-Path $fixtureRoot ([IO.Path]::GetFileNameWithoutExtension($name))
                Expand-Archive -LiteralPath $destination -DestinationPath $payload
                $arguments = @{ PayloadPath = $payload; RequireSignedOpenClaw = $true }
                Assert-Policy -Name "$name actual Windows signature policy" -Arguments $arguments
                Assert-TamperedFileRejected -Path (Join-Path $payload $ownedNames[0]) -Arguments $arguments `
                    -Name "$name rejects tampered apphost"
                if ($UpdateProofHost) {
                    dotnet $UpdateProofHost verify $destination "sha256:$hash"
                    if ($LASTEXITCODE -ne 0) { throw "Native update package proof failed: $name" }
                }
            }
        }
        $arguments = @{ InstallerPath = $installerRoot }
        Assert-Policy -Name "both published installers pass actual Windows signature policy" -Arguments $arguments
        foreach ($name in $installerNames) {
            Assert-TamperedFileRejected -Path (Join-Path $installerRoot $name) -Arguments $arguments `
                -Name "$name rejects tampered installer"
        }
        $otherPublisher = Join-Path $env:SystemRoot "System32\whoami.exe"
        $otherSignature = Get-AuthenticodeSignature -LiteralPath $otherPublisher
        if ($otherSignature.Status -ne "Valid") { throw "Windows wrong-publisher fixture is not trusted." }
        Copy-Item -LiteralPath $otherPublisher -Destination (Join-Path $installerRoot $installerNames[0]) -Force
        Assert-Policy -Name "rejects real trusted Windows binary as an OpenClaw installer" -Arguments $arguments `
            -ExpectedError "expected OpenClaw signer"
    }
    Write-Host "Release signature regressions passed: $script:passed cases." -ForegroundColor Green
    $global:LASTEXITCODE = 0
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
