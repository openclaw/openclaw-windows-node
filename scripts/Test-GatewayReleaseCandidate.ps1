[CmdletBinding()]
param(
    [string]$Version,
    [string]$SummaryPath,
    [switch]$AllowEmbeddedPolicyEvidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$sigstoreCliPackage = "@sigstore/cli@0.8.0"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $tags = Invoke-RestMethod -Uri "https://registry.npmjs.org/-/package/openclaw/dist-tags" -TimeoutSec 30
    $Version = [string]$tags.latest
}

$summary = [ordered]@{
    version = $Version
    eligible = $false
    protocolGeneration = $null
    securityFloor = $null
    atOrAboveSecurityFloor = $false
    npmIntegrity = $null
    npmIntegrityVerified = $false
    npmSignatureCount = 0
    npmSignatureVerified = $false
    npmProvenance = $false
    npmProvenanceSourceCommit = $null
    tagCommit = $null
    npmProvenanceTagBound = $false
    embeddedPolicyException = $false
    packageBuildVersion = $null
    packageBuildCommit = $null
    packageBuildMatchesTag = $false
    githubStableRelease = $false
    stableReleaseManifest = $false
    githubVerifiedTag = $false
    extendedStableTag = $false
    checkedAtUtc = [DateTime]::UtcNow.ToString("O")
    failures = @()
}

function Add-Failure([string]$Message) {
    $script:summary.failures += $Message
}

function Compare-GatewayReleaseVersion([string]$Left, [string]$Right) {
    $pattern = '^(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d+)(?:-(?<correction>\d+))?$'
    $leftMatch = [regex]::Match($Left, $pattern)
    $rightMatch = [regex]::Match($Right, $pattern)
    if (-not $leftMatch.Success -or -not $rightMatch.Success) {
        throw "Cannot compare invalid Gateway release versions '$Left' and '$Right'."
    }

    foreach ($group in @('year', 'month', 'patch', 'correction')) {
        $leftValue = if ($leftMatch.Groups[$group].Success) {
            [int]$leftMatch.Groups[$group].Value
        } else {
            0
        }
        $rightValue = if ($rightMatch.Groups[$group].Success) {
            [int]$rightMatch.Groups[$group].Value
        } else {
            0
        }
        if ($leftValue -ne $rightValue) {
            return $leftValue.CompareTo($rightValue)
        }
    }

    return 0
}

function Get-TarballSha512([string]$Uri) {
    $tempPath = Join-Path ([IO.Path]::GetTempPath()) "openclaw-candidate-$([Guid]::NewGuid().ToString('N')).tgz"
    $stream = $null
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $tempPath -TimeoutSec 120
        $stream = [IO.File]::OpenRead($tempPath)
        $sha512 = [Security.Cryptography.SHA512]::Create()
        try {
            $hash = $sha512.ComputeHash($stream)
        }
        finally {
            $sha512.Dispose()
        }

        $stream.Position = 0
        $buildInfo = $null
        $gzip = [IO.Compression.GZipStream]::new(
            $stream,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
        try {
            while ($entry = $reader.GetNextEntry()) {
                if ($entry.Name -notin @(
                        "package/dist/build-info.json",
                        "./package/dist/build-info.json")) {
                    continue
                }

                if ($null -eq $entry.DataStream) {
                    break
                }

                $textReader = [IO.StreamReader]::new(
                    $entry.DataStream,
                    [Text.Encoding]::UTF8,
                    $true,
                    1024,
                    $true)
                try {
                    $buildInfo = $textReader.ReadToEnd() | ConvertFrom-Json
                }
                finally {
                    $textReader.Dispose()
                }
                break
            }
        }
        finally {
            $reader.Dispose()
            $gzip.Dispose()
        }

        return [pscustomobject]@{
            Base64 = [Convert]::ToBase64String($hash)
            Hex = [Convert]::ToHexString($hash).ToLowerInvariant()
            BuildVersion = [string]$buildInfo.version
            BuildCommit = [string]$buildInfo.commit
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-NpmRegistrySignatures($Metadata) {
    $signatures = @($Metadata.dist.signatures)
    if ($signatures.Count -eq 0) {
        return $false
    }

    $keys = @((Invoke-RestMethod `
        -Uri "https://registry.npmjs.org/-/npm/v1/keys" `
        -TimeoutSec 30).keys)
    $content = [Text.Encoding]::UTF8.GetBytes(
        "$($Metadata.name)@$($Metadata.version):$($Metadata.dist.integrity)")
    foreach ($signature in $signatures) {
        $key = @($keys | Where-Object {
            [string]$_.keyid -eq [string]$signature.keyid
        }) | Select-Object -First 1
        if ($null -eq $key) {
            return $false
        }

        $ecdsa = [Security.Cryptography.ECDsa]::Create()
        try {
            $bytesRead = 0
            $ecdsa.ImportSubjectPublicKeyInfo(
                [Convert]::FromBase64String([string]$key.key),
                [ref]$bytesRead)
            $verified = $ecdsa.VerifyData(
                $content,
                [Convert]::FromBase64String([string]$signature.sig),
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
            if (-not $verified) {
                return $false
            }
        }
        finally {
            $ecdsa.Dispose()
        }
    }

    return $true
}

function Test-NpmProvenance($Metadata, [string]$ExpectedSha512Hex) {
    $attestationsMetadata = $Metadata.dist.PSObject.Properties["attestations"]
    if ($null -eq $attestationsMetadata -or
        [string]$attestationsMetadata.Value.provenance.predicateType -ne "https://slsa.dev/provenance/v1" -or
        [string]::IsNullOrWhiteSpace([string]$attestationsMetadata.Value.url)) {
        return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
    }

    $document = Invoke-RestMethod -Uri ([string]$attestationsMetadata.Value.url) -TimeoutSec 30
    $provenance = @($document.attestations | Where-Object {
        [string]$_.predicateType -eq "https://slsa.dev/provenance/v1"
    }) | Select-Object -First 1
    if ($null -eq $provenance) {
        return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
    }

    $bundle = $provenance.bundle
    $envelope = $bundle.dsseEnvelope
    if ([string]$envelope.payloadType -ne "application/vnd.in-toto+json") {
        return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
    }

    $bundlePath = Join-Path ([IO.Path]::GetTempPath()) "openclaw-provenance-$([Guid]::NewGuid().ToString('N')).sigstore.json"
    $stderrPath = "$bundlePath.stderr"
    try {
        $bundle | ConvertTo-Json -Depth 100 -Compress |
            Set-Content -LiteralPath $bundlePath -Encoding utf8
        $identityPattern =
            "^https://github[.]com/openclaw/openclaw/[.]github/workflows/openclaw-npm-release[.]yml@(refs/heads/main|refs/tags/v$([regex]::Escape($Version)))$"
        $verificationOutput = @(
            & npx --yes $sigstoreCliPackage verify $bundlePath `
                --certificate-identity-uri $identityPattern `
                --certificate-issuer "https://token.actions.githubusercontent.com" `
                --json 2>$stderrPath
        )
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
        }

        $verification = ($verificationOutput -join [Environment]::NewLine) |
            ConvertFrom-Json
        if (-not [bool]$verification.verified) {
            return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
        }
    }
    finally {
        Remove-Item -LiteralPath $bundlePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }

    $payloadBytes = [Convert]::FromBase64String([string]$envelope.payload)
    $payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
    if ([string]$payload.predicateType -ne "https://slsa.dev/provenance/v1") {
        return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
    }

    $subject = @($payload.subject | Where-Object {
        [string]$_.name -eq "pkg:npm/openclaw@$Version" -and
        [string]$_.digest.sha512 -eq $ExpectedSha512Hex
    }) | Select-Object -First 1
    if ($null -eq $subject) {
        return [pscustomobject]@{ Verified = $false; SourceCommit = $null }
    }

    $source = @($payload.predicate.buildDefinition.resolvedDependencies | Where-Object {
        [string]$_.uri -match '^git\+https://github[.]com/openclaw/openclaw@' -and
        [string]$_.digest.gitCommit -match '^[0-9a-fA-F]{40}$'
    }) | Select-Object -First 1
    return [pscustomobject]@{
        Verified = $null -ne $source
        SourceCommit = if ($null -eq $source) { $null } else { [string]$source.digest.gitCommit }
    }
}

try {
    $policyPath = Join-Path $PSScriptRoot '..\src\OpenClaw.SetupEngine\GatewayReleasePolicy.cs'
    $policy = Get-Content -LiteralPath $policyPath -Raw
    if ($Version -notmatch '^\d{4}\.\d{1,2}\.\d+(?:-\d+)?$') {
        Add-Failure "Version is not an exact stable release. Prerelease labels are ineligible."
    }
    else {
        $floorMatch = [regex]::Match(
            $policy,
            'SecurityFloor\s*=\s*"(?<version>[^"]+)"')
        if (-not $floorMatch.Success) {
            Add-Failure "The embedded Gateway security floor could not be read."
        }
        else {
            $summary.securityFloor = $floorMatch.Groups['version'].Value
            $summary.atOrAboveSecurityFloor =
                (Compare-GatewayReleaseVersion $Version $summary.securityFloor) -ge 0
            if (-not $summary.atOrAboveSecurityFloor) {
                Add-Failure "Gateway $Version is below the embedded security floor $($summary.securityFloor)."
            }
        }
    }

    $metadata = Invoke-RestMethod -Uri "https://registry.npmjs.org/openclaw/$Version" -TimeoutSec 30
    $summary.npmIntegrity = [string]$metadata.dist.integrity
    $summary.npmSignatureCount = @($metadata.dist.signatures).Count

    if ([string]::IsNullOrWhiteSpace($summary.npmIntegrity) -or
        -not $summary.npmIntegrity.StartsWith("sha512-", [StringComparison]::Ordinal)) {
        Add-Failure "npm metadata does not contain SHA-512 package integrity."
    }
    else {
        try {
            $tarballHash = Get-TarballSha512 ([string]$metadata.dist.tarball)
            $summary.npmIntegrityVerified =
                "sha512-$($tarballHash.Base64)" -eq $summary.npmIntegrity
            $summary.packageBuildVersion = $tarballHash.BuildVersion
            $summary.packageBuildCommit = $tarballHash.BuildCommit
            if (-not $summary.npmIntegrityVerified) {
                Add-Failure "Downloaded npm tarball does not match the published SHA-512 integrity."
            }
            if ($summary.packageBuildVersion -ne $Version) {
                Add-Failure "The integrity-verified package build metadata does not match Gateway $Version."
            }
            if ($summary.packageBuildCommit -notmatch '^[0-9a-fA-F]{40}$') {
                Add-Failure "The integrity-verified package does not contain an exact source commit."
            }
        }
        catch {
            Add-Failure "npm tarball integrity verification failed: $($_.Exception.Message)"
        }
    }

    try {
        $summary.npmSignatureVerified = Test-NpmRegistrySignatures $metadata
        if (-not $summary.npmSignatureVerified) {
            Add-Failure "npm registry signatures did not verify cryptographically."
        }
    }
    catch {
        Add-Failure "npm registry signature verification failed: $($_.Exception.Message)"
    }

    if ($summary.npmIntegrityVerified) {
        try {
            $provenance = Test-NpmProvenance $metadata $tarballHash.Hex
            $summary.npmProvenance = [bool]$provenance.Verified
            $summary.npmProvenanceSourceCommit = [string]$provenance.SourceCommit
            if (-not $summary.npmProvenance) {
                Add-Failure "npm SLSA provenance did not verify against the package digest and OpenClaw release identity."
            }
        }
        catch {
            Add-Failure "npm SLSA provenance verification failed: $($_.Exception.Message)"
        }
    }
    else {
        Add-Failure "npm SLSA provenance was not checked because package integrity failed."
    }

    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "openclaw-windows-node-candidate-validator"
    }
    if ($env:GITHUB_TOKEN) {
        $headers.Authorization = "Bearer $($env:GITHUB_TOKEN)"
    }
    try {
        $release = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/openclaw/openclaw/releases/tags/v$Version" `
            -Headers $headers `
            -TimeoutSec 30
        $summary.githubStableRelease = -not [bool]$release.draft -and -not [bool]$release.prerelease
    }
    catch {
        $summary.githubStableRelease = $false
    }

    if ($summary.githubStableRelease) {
        try {
            $manifest = Invoke-RestMethod `
                -Uri "https://github.com/openclaw/openclaw/releases/download/v$Version/openclaw-$Version-release-manifest.json" `
                -TimeoutSec 30
            $summary.stableReleaseManifest =
                [string]$manifest.releaseProfile -eq "stable" -and
                [string]$manifest.runReleaseSoak -eq "true" -and
                [bool]$manifest.controls.stableSoakRequired
        }
        catch {
            $summary.stableReleaseManifest = $false
        }
    }

    try {
        $tagRef = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/openclaw/openclaw/git/ref/tags/v$Version" `
            -Headers $headers `
            -TimeoutSec 30
        if ([string]$tagRef.object.type -eq "tag") {
            $tag = Invoke-RestMethod `
                -Uri "https://api.github.com/repos/openclaw/openclaw/git/tags/$($tagRef.object.sha)" `
                -Headers $headers `
                -TimeoutSec 30
            $summary.githubVerifiedTag = [bool]$tag.verification.verified
            $summary.extendedStableTag =
                $summary.githubVerifiedTag -and
                [string]$tag.message -match '(?i)\bextended-stable release\b'
            if ([string]$tag.object.type -eq "commit") {
                $summary.tagCommit = [string]$tag.object.sha
            }
        }
        elseif ([string]$tagRef.object.type -eq "commit") {
            $summary.tagCommit = [string]$tagRef.object.sha
        }
    }
    catch {
        $summary.githubVerifiedTag = $false
        $summary.extendedStableTag = $false
    }

    $summary.packageBuildMatchesTag =
        $summary.packageBuildCommit -match '^[0-9a-fA-F]{40}$' -and
        $summary.tagCommit -match '^[0-9a-fA-F]{40}$' -and
        [string]::Equals(
            $summary.packageBuildCommit,
            $summary.tagCommit,
            [StringComparison]::OrdinalIgnoreCase)
    if (-not $summary.packageBuildMatchesTag) {
        Add-Failure "The integrity-verified package build commit does not match the exact release tag commit."
    }

    $summary.npmProvenanceTagBound =
        $summary.npmProvenance -and
        $summary.npmProvenanceSourceCommit -match '^[0-9a-fA-F]{40}$' -and
        [string]::Equals(
            $summary.npmProvenanceSourceCommit,
            $summary.tagCommit,
            [StringComparison]::OrdinalIgnoreCase)
    if ($summary.npmProvenance -and -not $summary.npmProvenanceTagBound) {
        $recommendedMatch = [regex]::Match(
            $policy,
            'RecommendedVersion\s*=\s*"(?<version>[^"]+)"')
        $fallbackMatch = [regex]::Match(
            $policy,
            'FallbackVersion\s*=>\s*"(?<version>[^"]+)"')
        $isEmbeddedValidatedSelection =
            (($recommendedMatch.Success -and
              $recommendedMatch.Groups['version'].Value -eq $Version) -or
             ($fallbackMatch.Success -and
              $fallbackMatch.Groups['version'].Value -eq $Version)) -and
            $policy.Contains($summary.npmIntegrity, [StringComparison]::Ordinal)
        if ($AllowEmbeddedPolicyEvidence -and
            $isEmbeddedValidatedSelection -and
            $summary.packageBuildMatchesTag) {
            $summary.embeddedPolicyException = $true
        }
        else {
            Add-Failure "npm provenance source commit does not match the exact release tag commit."
        }
    }

    $distTags = Invoke-RestMethod `
        -Uri "https://registry.npmjs.org/-/package/openclaw/dist-tags" `
        -TimeoutSec 30
    $isCurrentExtendedStable =
        [string]$distTags.'extended-stable' -eq $Version
    $hasStableUpstreamAttestation =
        ($summary.githubStableRelease -and $summary.stableReleaseManifest) -or
        ($summary.extendedStableTag -and $isCurrentExtendedStable)
    if (-not $hasStableUpstreamAttestation) {
        Add-Failure "Stable upstream attestation is missing. Require a stable GitHub release manifest or a verified signed extended-stable tag."
    }

    if ($summary.packageBuildMatchesTag) {
        $protocolSource = Invoke-RestMethod `
            -Uri "https://raw.githubusercontent.com/openclaw/openclaw/$($summary.packageBuildCommit)/packages/gateway-protocol/src/version.ts" `
            -TimeoutSec 30
        $protocolMatch = [regex]::Match(
            [string]$protocolSource,
            'PROTOCOL_VERSION\s*=\s*(\d+)')
        if ($protocolMatch.Success) {
            $summary.protocolGeneration = [int]$protocolMatch.Groups[1].Value
        }
    }
    if ($summary.protocolGeneration -ne 4) {
        Add-Failure "The exact package/tag commit does not declare protocol generation 4."
    }

    $summary.eligible = $summary.failures.Count -eq 0
}
finally {
    if ($SummaryPath) {
        $parent = Split-Path -Parent $SummaryPath
        if ($parent) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SummaryPath -Encoding utf8
    }
}

$summary | ConvertTo-Json -Depth 8
if (-not $summary.eligible) {
    throw "Gateway $Version is not eligible: $($summary.failures -join ' ')"
}
