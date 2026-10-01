[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$ApplicationId,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][guid]$ClientId,
    [Parameter(Mandatory)][string]$ClientAssertionFile,
    [string]$PolicyPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'store-submission.json'),
    [string]$EvidencePath,
    [scriptblock]$HttpInvoker,
    [scriptblock]$UploadInvoker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NonEmptyValue {
    param([Parameter(Mandatory)][string]$Name, [AllowEmptyString()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "$Name must not be empty." }
}

function Get-RequiredProperty {
    param([Parameter(Mandatory)][object]$Object, [Parameter(Mandatory)][string]$Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "Store response is missing '$Name'." }
    return $property.Value
}

function ConvertTo-CanonicalValue {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
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
        'ApplicationCategory', 'Pricing', 'Visibility', 'TargetPublishMode',
        'TargetPublishDate', 'Listings', 'HardwarePreferences',
        'AutomaticBackupEnabled', 'CanInstallOnRemovableMedia', 'IsGameDvrEnabled',
        'GamingOptions', 'HasExternalInAppProducts', 'MeetAccessibilityGuidelines',
        'NotesForCertification', 'EnterpriseLicensing',
        'AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies',
        'AllowTargetFutureDeviceFamilies', 'Trailers'
    )) {
        $metadata[$name] = ConvertTo-CanonicalValue (Get-RequiredProperty $Submission $name)
    }
    $json = $metadata | ConvertTo-Json -Depth 100 -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-SubmissionMutationHash {
    param([Parameter(Mandatory)][psobject]$Submission)
    $mutationState = [ordered]@{
        ApplicationPackages = ConvertTo-CanonicalValue (
            Get-RequiredProperty $Submission 'ApplicationPackages')
        PackageDeliveryOptions = ConvertTo-CanonicalValue (
            Get-RequiredProperty $Submission 'PackageDeliveryOptions')
    }
    $json = $mutationState | ConvertTo-Json -Depth 100 -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

Assert-NonEmptyValue -Name 'ApplicationId' -Value $ApplicationId
if (-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)) {
    throw "MSIX bundle does not exist: $BundlePath"
}
$resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path
if ([IO.Path]::GetExtension($resolvedBundle) -cne '.msixbundle') {
    throw "Store submission requires one .msixbundle: $resolvedBundle"
}
if ((Get-Item -LiteralPath $resolvedBundle).Length -eq 0) { throw "MSIX bundle is empty: $resolvedBundle" }
if (-not (Test-Path -LiteralPath $ClientAssertionFile -PathType Leaf)) {
    throw "OIDC client assertion file does not exist: $ClientAssertionFile"
}
$assertion = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ClientAssertionFile).Path).Trim()
if ([string]::IsNullOrWhiteSpace($assertion)) { throw 'OIDC client assertion file is empty.' }
if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw "Store submission policy does not exist: $PolicyPath"
}
try { $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json }
catch { throw "Unable to parse Store submission policy: $($_.Exception.Message)" }

$requiredProperties = @(
    'schemaVersion', 'environment', 'oidcAudience', 'apiBaseUri', 'oauthScope',
    'commitSubmission', 'submissionWriterPolicy', 'pendingSubmissionPolicy', 'failedDraftPolicy',
    'packageRolloutPercentage', 'uploadTimeoutSeconds', 'minimumAccessTokenLifetimeSeconds'
)
foreach ($property in $requiredProperties) {
    if ($policy.PSObject.Properties.Name -notcontains $property) {
        throw "Store submission policy is missing '$property'."
    }
}
if ([int]$policy.schemaVersion -ne 1 -or [string]$policy.environment -cne 'microsoft-store' -or
    [string]$policy.oidcAudience -cne 'api://AzureADTokenExchange') {
    throw 'Store submission policy has an unsupported identity boundary.'
}
if ([string]$policy.apiBaseUri -cne 'https://manage.devcenter.microsoft.com' -or
    [string]$policy.oauthScope -cne 'https://manage.devcenter.microsoft.com/.default') {
    throw 'Store submission policy has an unsupported API boundary.'
}
if ([bool]$policy.commitSubmission -ne $true -or
    [string]$policy.submissionWriterPolicy -cne 'exclusive-github-environment' -or
    [string]$policy.pendingSubmissionPolicy -cne 'reject' -or
    [string]$policy.failedDraftPolicy -cne 'delete-owned-before-commit') {
    throw 'Store submission policy must commit safely and reject unowned drafts.'
}
$rollout = [float]$policy.packageRolloutPercentage
$uploadTimeout = [long]$policy.uploadTimeoutSeconds
$minimumLifetime = [long]$policy.minimumAccessTokenLifetimeSeconds
if ($rollout -lt 0 -or $rollout -gt 100) { throw 'Store package rollout percentage must be between 0 and 100.' }
if ($uploadTimeout -lt 100 -or $uploadTimeout -gt 100000 -or
    $minimumLifetime -lt ($uploadTimeout + 300)) {
    throw 'Store access-token lifetime must cover upload timeout plus five minutes.'
}

if ($null -eq $HttpInvoker) {
    $HttpInvoker = {
        param($Method, $Uri, $Headers, $Body, $ContentType)
        $parameters = @{ Method = $Method; Uri = $Uri; ErrorAction = 'Stop' }
        if ($null -ne $Headers) { $parameters.Headers = $Headers }
        if ($null -ne $Body) { $parameters.Body = $Body }
        if (-not [string]::IsNullOrWhiteSpace($ContentType)) { $parameters.ContentType = $ContentType }
        Invoke-RestMethod @parameters
    }
}
if ($null -eq $UploadInvoker) {
    $UploadInvoker = {
        param($Uri, $Path, $TimeoutSeconds)
        Invoke-WebRequest -Method Put -Uri $Uri -InFile $Path -TimeoutSec $TimeoutSeconds `
            -ContentType 'application/zip' -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
    }
}

$tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
$tokenResponse = & $HttpInvoker 'Post' $tokenEndpoint $null ([ordered]@{
    client_id = $ClientId.ToString()
    scope = [string]$policy.oauthScope
    client_assertion = $assertion
    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
    grant_type = 'client_credentials'
}) 'application/x-www-form-urlencoded'
$accessToken = [string](Get-RequiredProperty $tokenResponse 'access_token')
$expiresIn = [long](Get-RequiredProperty $tokenResponse 'expires_in')
if ([string]::IsNullOrWhiteSpace($accessToken) -or $expiresIn -lt $minimumLifetime) {
    throw 'Microsoft Store access token is missing or too short-lived for the upload.'
}
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$accessToken" }

$apiBase = [string]$policy.apiBaseUri
$encodedApplicationId = [Uri]::EscapeDataString($ApplicationId)
$apiHeaders = @{ Authorization = "Bearer $accessToken"; TenantId = $TenantId.ToString() }
function Invoke-StoreApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][object]$Body = $null
    )
    $json = if ($null -eq $Body) { $null } else { $Body | ConvertTo-Json -Depth 100 -Compress }
    return & $HttpInvoker $Method ($apiBase + $Path) $apiHeaders $json 'application/json'
}
function Assert-OwnedDraft {
    param([Parameter(Mandatory)][string]$SubmissionId)
    $application = Invoke-StoreApi -Method Get -Path "/v1.0/my/applications/$encodedApplicationId"
    $pending = Get-RequiredProperty $application 'PendingApplicationSubmission'
    if ($null -eq $pending -or [string](Get-RequiredProperty $pending 'Id') -cne $SubmissionId) {
        throw 'The Store pending submission is no longer the automation-owned draft.'
    }
}

$draftId = $null
$committed = $false
$draftDeletionAllowed = $true
$temporaryDirectory = $null
try {
    $application = Invoke-StoreApi -Method Get -Path "/v1.0/my/applications/$encodedApplicationId"
    if ([string](Get-RequiredProperty $application 'Id') -cne $ApplicationId) {
        throw 'Microsoft Store application preflight returned the wrong product.'
    }
    if ($null -ne (Get-RequiredProperty $application 'PendingApplicationSubmission')) {
        throw 'Partner Center already has a pending submission; automation will not replace it.'
    }
    $publishedInfo = Get-RequiredProperty $application 'LastPublishedApplicationSubmission'
    if ($null -eq $publishedInfo) { throw 'The Partner Center product must have a published submission.' }
    $publishedId = [string](Get-RequiredProperty $publishedInfo 'Id')
    Assert-NonEmptyValue -Name 'Published submission ID' -Value $publishedId
    $encodedPublishedId = [Uri]::EscapeDataString($publishedId)
    $published = Invoke-StoreApi -Method Get `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedPublishedId"
    $publishedMetadataHash = Get-SubmissionMetadataHash $published

    $draft = Invoke-StoreApi -Method Post `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions?isMinimalResponse=true"
    $draftId = [string](Get-RequiredProperty $draft 'Id')
    Assert-NonEmptyValue -Name 'Draft submission ID' -Value $draftId
    $encodedDraftId = [Uri]::EscapeDataString($draftId)
    Assert-OwnedDraft -SubmissionId $draftId
    $draft = Invoke-StoreApi -Method Get `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedDraftId"
    if ([string](Get-RequiredProperty $draft 'Id') -cne $draftId) {
        throw 'Store returned a different draft than the automation created.'
    }
    $uploadUri = [string](Get-RequiredProperty $draft 'FileUploadUrl')
    Assert-NonEmptyValue -Name 'Draft upload URL' -Value $uploadUri

    $packages = @(Get-RequiredProperty $draft 'ApplicationPackages')
    foreach ($package in $packages) { $package.FileStatus = 'PendingDelete' }
    $packages += [pscustomobject]@{
        FileName = [IO.Path]::GetFileName($resolvedBundle)
        FileStatus = 'PendingUpload'
        minimumDirectXVersion = 'None'
        minimumSystemRam = 'None'
    }
    $draft.ApplicationPackages = $packages
    $deliveryOptions = Get-RequiredProperty $draft 'PackageDeliveryOptions'
    if ($null -ne $deliveryOptions) {
        $packageRollout = Get-RequiredProperty $deliveryOptions 'PackageRollout'
        if ($null -ne $packageRollout) {
            $packageRollout.IsPackageRollout = $true
            $packageRollout.PackageRolloutPercentage = $rollout
        }
    }

    $draft = Invoke-StoreApi -Method Put `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedDraftId" `
        -Body $draft
    if ([string](Get-RequiredProperty $draft 'Id') -cne $draftId) {
        throw 'Store updated a different draft than the automation owns.'
    }
    $draftMutationHash = Get-SubmissionMutationHash $draft
    Assert-OwnedDraft -SubmissionId $draftId

    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) (
        "openclaw-store-upload-$([guid]::NewGuid().ToString('N'))")
    $uploadDirectory = Join-Path $temporaryDirectory 'payload'
    $uploadArchive = Join-Path $temporaryDirectory 'Upload.zip'
    New-Item -ItemType Directory -Path $uploadDirectory -Force | Out-Null
    Copy-Item -LiteralPath $resolvedBundle -Destination $uploadDirectory
    [IO.Compression.ZipFile]::CreateFromDirectory($uploadDirectory, $uploadArchive)
    & $UploadInvoker $uploadUri $uploadArchive $uploadTimeout

    Assert-OwnedDraft -SubmissionId $draftId
    $verifiedDraft = Invoke-StoreApi -Method Get `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedDraftId"
    if ([string](Get-RequiredProperty $verifiedDraft 'Id') -cne $draftId) {
        throw 'Store verification returned an unowned draft.'
    }
    $draftMetadataHash = Get-SubmissionMetadataHash $verifiedDraft
    if ($draftMetadataHash -cne $publishedMetadataHash) {
        throw 'The Store draft did not preserve published product metadata.'
    }
    $verifiedMutationHash = Get-SubmissionMutationHash $verifiedDraft
    if ($verifiedMutationHash -cne $draftMutationHash) {
        throw 'The Store draft package mutation state changed after upload.'
    }
    Assert-OwnedDraft -SubmissionId $draftId
    # Once the commit request leaves this process, a transport failure is
    # ambiguous: Partner Center may have accepted it even if no response
    # arrived. Never delete that submission unless Partner Center explicitly
    # reports a terminal rejection.
    $draftDeletionAllowed = $false
    $commit = Invoke-StoreApi -Method Post `
        -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedDraftId/Commit"
    $commitStatus = [string](Get-RequiredProperty $commit 'Status')
    if ($commitStatus -cne 'CommitStarted') {
        if ($commitStatus -cin @('CommitFailed', 'Canceled')) {
            $draftDeletionAllowed = $true
        }
        throw "Store did not accept the submission commit: $commitStatus"
    }
    $committed = $true

    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
        New-Item -Path (Split-Path $resolvedEvidence -Parent) -ItemType Directory -Force | Out-Null
        [ordered]@{
            schemaVersion = 1
            applicationId = $ApplicationId
            bundleFileName = [IO.Path]::GetFileName($resolvedBundle)
            bundleSha256 = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
            publishedSubmissionId = $publishedId
            draftSubmissionId = $draftId
            publishedMetadataSha256 = $publishedMetadataHash
            draftMetadataSha256 = $draftMetadataHash
            preparedMutationSha256 = $draftMutationHash
            verifiedMutationSha256 = $verifiedMutationHash
            commitStatus = $commitStatus
            packageRolloutPercentage = $rollout
            submittedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    }
}
catch {
    $failure = $_
    if ($draftDeletionAllowed -and -not $committed -and
        -not [string]::IsNullOrWhiteSpace($draftId)) {
        try {
            $encodedDraftId = [Uri]::EscapeDataString($draftId)
            Invoke-StoreApi -Method Delete `
                -Path "/v1.0/my/applications/$encodedApplicationId/submissions/$encodedDraftId" |
                Out-Null
        }
        catch {
            Write-Warning "Could not delete automation-owned draft '$draftId': $($_.Exception.Message)"
        }
    }
    throw $failure
}
finally {
    if ($null -ne $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    $assertion = $null
    $accessToken = $null
}
