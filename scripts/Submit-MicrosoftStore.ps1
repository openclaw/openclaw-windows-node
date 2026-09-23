[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$ApplicationId,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][string]$SellerId,
    [Parameter(Mandatory)][guid]$ClientId,
    [Parameter(Mandatory)][string]$ClientAssertionFile,
    [string]$PolicyPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'store-submission.json'),
    [string]$EvidencePath,
    [string]$MSStoreCommand = 'msstore'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NonEmptyValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must not be empty."
    }
}

function Invoke-MSStore {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation,
        [switch]$CaptureOutput
    )

    $output = @(& $MSStoreCommand @Arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Microsoft Store CLI $Operation failed with exit code $exitCode."
    }
    if ($CaptureOutput) {
        return ($output -join "`n")
    }
    foreach ($line in $output) {
        Write-Output $line
    }
}

function ConvertFrom-MSStoreJson {
    param(
        [Parameter(Mandatory)][string]$Json,
        [Parameter(Mandatory)][string]$Operation
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "Microsoft Store CLI $Operation returned no JSON."
    }
    try {
        return $Json | ConvertFrom-Json
    }
    catch {
        throw "Microsoft Store CLI $Operation returned invalid JSON: $($_.Exception.Message)"
    }
}

function ConvertTo-CanonicalValue {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) {
        return $Value
    }
    if ($Value -is [Collections.IDictionary]) {
        $dictionary = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) {
            $dictionary[[string]$key] = ConvertTo-CanonicalValue $Value[$key]
        }
        return $dictionary
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = @($Value | ForEach-Object { ConvertTo-CanonicalValue $_ })
        return ,$items
    }

    $properties = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
        $properties[$property.Name] = ConvertTo-CanonicalValue $property.Value
    }
    return $properties
}

function Get-SubmissionMetadataHash {
    param([Parameter(Mandatory)][psobject]$Submission)

    $metadata = [ordered]@{}
    foreach ($name in @(
        'ApplicationCategory'
        'Pricing'
        'Visibility'
        'TargetPublishMode'
        'TargetPublishDate'
        'Listings'
        'HardwarePreferences'
        'AutomaticBackupEnabled'
        'CanInstallOnRemovableMedia'
        'IsGameDvrEnabled'
        'GamingOptions'
        'HasExternalInAppProducts'
        'MeetAccessibilityGuidelines'
        'NotesForCertification'
        'EnterpriseLicensing'
        'AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies'
        'AllowTargetFutureDeviceFamilies'
        'FriendlyName'
        'Trailers'
    )) {
        $property = $Submission.PSObject.Properties[$name]
        if ($null -eq $property) {
            throw "Store submission JSON is missing preserved field '$name'."
        }
        $metadata[$name] = ConvertTo-CanonicalValue $property.Value
    }

    $json = $metadata | ConvertTo-Json -Depth 100 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

Assert-NonEmptyValue -Name 'ApplicationId' -Value $ApplicationId
Assert-NonEmptyValue -Name 'SellerId' -Value $SellerId
Assert-NonEmptyValue -Name 'MSStoreCommand' -Value $MSStoreCommand

if (-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)) {
    throw "MSIX bundle does not exist: $BundlePath"
}
$resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path
if ([IO.Path]::GetExtension($resolvedBundle) -cne '.msixbundle') {
    throw "Store submission requires one .msixbundle: $resolvedBundle"
}
if ((Get-Item -LiteralPath $resolvedBundle).Length -eq 0) {
    throw "MSIX bundle is empty: $resolvedBundle"
}

if (-not (Test-Path -LiteralPath $ClientAssertionFile -PathType Leaf)) {
    throw "OIDC client assertion file does not exist: $ClientAssertionFile"
}
$resolvedAssertion = (Resolve-Path -LiteralPath $ClientAssertionFile).Path
if ([string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($resolvedAssertion))) {
    throw 'OIDC client assertion file is empty.'
}

if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw "Store submission policy does not exist: $PolicyPath"
}
try {
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
}
catch {
    throw "Unable to parse Store submission policy: $($_.Exception.Message)"
}

$requiredProperties = @(
    'schemaVersion'
    'environment'
    'oidcAudience'
    'msstoreCliVersion'
    'commitSubmission'
    'pendingSubmissionPolicy'
    'packageRolloutPercentage'
    'uploadTimeoutSeconds'
)
foreach ($property in $requiredProperties) {
    if ($policy.PSObject.Properties.Name -notcontains $property) {
        throw "Store submission policy is missing '$property'."
    }
}
if ([int]$policy.schemaVersion -ne 1) {
    throw "Unsupported Store submission policy schema: $($policy.schemaVersion)"
}
if ([string]$policy.environment -cne 'microsoft-store') {
    throw 'Store submission policy must use the microsoft-store environment.'
}
if ([string]$policy.oidcAudience -cne 'api://AzureADTokenExchange') {
    throw 'Store submission policy must use the Azure token-exchange audience.'
}
if ([string]$policy.msstoreCliVersion -notmatch '^v\d+\.\d+\.\d+$') {
    throw 'Store submission policy must pin an exact MSStore CLI version.'
}
if ([bool]$policy.commitSubmission -ne $true) {
    throw 'Store submission policy must commit the Partner Center update.'
}
if ([string]$policy.pendingSubmissionPolicy -cne 'reject') {
    throw 'Store submission policy must reject pending drafts.'
}
$rollout = [float]$policy.packageRolloutPercentage
if ($rollout -lt 0 -or $rollout -gt 100) {
    throw 'Store package rollout percentage must be between 0 and 100.'
}
$uploadTimeout = [long]$policy.uploadTimeoutSeconds
if ($uploadTimeout -lt 100 -or $uploadTimeout -gt 100000) {
    throw 'Store upload timeout must be between 100 and 100000 seconds.'
}

$previousAssertion = [Environment]::GetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION')
$previousAssertionFile = [Environment]::GetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION_FILE')
try {
    [Environment]::SetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION', $null)
    [Environment]::SetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION_FILE', $resolvedAssertion)

    Invoke-MSStore -Operation 'configuration' -Arguments @(
        'reconfigure'
        '--tenantId'
        $TenantId.ToString()
        '--sellerId'
        $SellerId
        '--clientId'
        $ClientId.ToString()
        '--clientAssertion'
    )

    $application = ConvertFrom-MSStoreJson `
        -Operation 'application preflight' `
        -Json (Invoke-MSStore `
            -Operation 'application preflight' `
            -CaptureOutput `
            -Arguments @('apps', 'get', $ApplicationId))
    if ([string]$application.Id -cne $ApplicationId) {
        throw 'Microsoft Store application preflight returned the wrong product.'
    }
    if ($null -ne $application.PendingApplicationSubmission) {
        throw (
            'Partner Center already has a pending submission. ' +
            'Finish or delete that draft before automated publication.'
        )
    }
    if ([string]::IsNullOrWhiteSpace([string]$application.LastPublishedApplicationSubmission.Id)) {
        throw 'The Partner Center product must have a published submission.'
    }

    $publishedSubmission = ConvertFrom-MSStoreJson `
        -Operation 'published submission snapshot' `
        -Json (Invoke-MSStore `
            -Operation 'published submission snapshot' `
            -CaptureOutput `
            -Arguments @('submission', 'get', $ApplicationId))
    $publishedMetadataHash = Get-SubmissionMetadataHash $publishedSubmission

    Invoke-MSStore -Operation 'publication' -Arguments @(
        'publish'
        $resolvedBundle
        '--appId'
        $ApplicationId
        '--packageRolloutPercentage'
        $rollout.ToString([Globalization.CultureInfo]::InvariantCulture)
        '--uploadTimeout'
        $uploadTimeout.ToString([Globalization.CultureInfo]::InvariantCulture)
        '--noCommit'
    )

    $draftSubmission = ConvertFrom-MSStoreJson `
        -Operation 'draft submission verification' `
        -Json (Invoke-MSStore `
            -Operation 'draft submission verification' `
            -CaptureOutput `
            -Arguments @('submission', 'get', $ApplicationId))
    $draftMetadataHash = Get-SubmissionMetadataHash $draftSubmission
    if ($draftMetadataHash -cne $publishedMetadataHash) {
        throw (
            'The Store draft did not preserve published product metadata. ' +
            'The draft was left uncommitted for inspection.'
        )
    }

    Invoke-MSStore -Operation 'submission commit' -Arguments @(
        'submission'
        'publish'
        $ApplicationId
    )

    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
        $evidenceDirectory = Split-Path $resolvedEvidence -Parent
        if (-not [string]::IsNullOrEmpty($evidenceDirectory)) {
            New-Item -Path $evidenceDirectory -ItemType Directory -Force | Out-Null
        }
        [ordered]@{
            schemaVersion = 1
            applicationId = $ApplicationId
            bundleFileName = [IO.Path]::GetFileName($resolvedBundle)
            bundleSha256 = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
            msstoreCliVersion = [string]$policy.msstoreCliVersion
            pendingSubmissionPolicy = [string]$policy.pendingSubmissionPolicy
            publishedSubmissionId = [string]$publishedSubmission.Id
            draftSubmissionId = [string]$draftSubmission.Id
            publishedMetadataSha256 = $publishedMetadataHash
            draftMetadataSha256 = $draftMetadataHash
            packageRolloutPercentage = $rollout
            submittedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } |
            ConvertTo-Json |
            Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    }
}
finally {
    [Environment]::SetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION', $previousAssertion)
    [Environment]::SetEnvironmentVariable('MSSTORE_CLIENT_ASSERTION_FILE', $previousAssertionFile)
}
