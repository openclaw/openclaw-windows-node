[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$ApplicationId,
    [ValidateSet('production', 'development')][string]$Channel = 'production',
    [string]$FlightId,
    [Parameter(Mandatory)][string]$PackageIdentityName,
    [Parameter(Mandatory)][string]$Publisher,
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
$Channel = $Channel.ToLowerInvariant()
$isFlight = $Channel -ceq 'development'
$parsedFlightId = [guid]::Empty
if ($isFlight -and -not [guid]::TryParse($FlightId, [ref]$parsedFlightId)) {
    throw 'Development submission requires the configured flight ID.'
}
if (-not $isFlight -and -not [string]::IsNullOrWhiteSpace($FlightId)) {
    throw 'Production submission must not specify a flight ID.'
}
$packagesProperty = if ($isFlight) { 'FlightPackages' } else { 'ApplicationPackages' }
$pendingProperty = if ($isFlight) { 'PendingFlightSubmission' } else { 'PendingApplicationSubmission' }
$publishedProperty = if ($isFlight) { 'LastPublishedFlightSubmission' } else { 'LastPublishedApplicationSubmission' }

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
    $metadataNames = if ($isFlight) { @('TargetPublishMode', 'TargetPublishDate', 'NotesForCertification') } else { @(
        'ApplicationCategory', 'Pricing', 'Visibility', 'TargetPublishMode',
        'TargetPublishDate', 'Listings', 'HardwarePreferences',
        'AutomaticBackupEnabled', 'CanInstallOnRemovableMedia', 'IsGameDvrEnabled',
        'GamingOptions', 'HasExternalInAppProducts', 'MeetAccessibilityGuidelines',
        'NotesForCertification', 'EnterpriseLicensing',
        'AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies',
        'AllowTargetFutureDeviceFamilies', 'Trailers'
    ) }
    foreach ($name in $metadataNames) {
        # Conditional metadata is omitted by valid Store responses. Preserve it
        # when applicable, without requiring an Immediate submission's date.
        if ($name -eq 'TargetPublishDate' -and $Submission.TargetPublishMode -ne 'SpecificDate') { continue }
        $property = $Submission.PSObject.Properties[$name]
        if ($name -in @('ApplicationCategory', 'Pricing', 'Visibility', 'TargetPublishMode', 'Listings', 'TargetPublishDate')) {
            $metadata[$name] = ConvertTo-CanonicalValue (Get-RequiredProperty $Submission $name)
        } else {
            $metadata[$name] = if ($null -eq $property) { $null } else { ConvertTo-CanonicalValue $property.Value }
        }
    }
    $json = $metadata | ConvertTo-Json -Depth 100 -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-SubmissionMutationHash {
    param([Parameter(Mandatory)][psobject]$Submission)
    # Partner Center owns package IDs, architecture, version and upload status.
    # Compare only our requested files/actions and editable delivery settings.
    $packages = @(foreach ($package in @(Get-RequiredProperty $Submission $packagesProperty)) {
        $status = [string](Get-RequiredProperty $package 'FileStatus')
        if ($status -notin @('PendingUpload', 'Uploaded', 'PendingDelete')) { throw 'Unexpected Store package status.' }
        [ordered]@{
            FileName = [string](Get-RequiredProperty $package 'FileName')
            Action = if ($status -eq 'PendingDelete') { 'delete' } else { 'upload' }
            MinimumDirectXVersion = if ($null -ne $package.PSObject.Properties['minimumDirectXVersion']) { $package.PSObject.Properties['minimumDirectXVersion'].Value } else { $null }
            MinimumSystemRam = if ($null -ne $package.PSObject.Properties['minimumSystemRam']) { $package.PSObject.Properties['minimumSystemRam'].Value } else { $null }
        }
    })
    $delivery = ConvertTo-CanonicalValue (Get-RequiredProperty $Submission 'PackageDeliveryOptions')
    if ($null -ne $delivery -and $null -ne $delivery['PackageRollout']) {
        $delivery['PackageRollout'].Remove('PackageRolloutStatus')
        $delivery['PackageRollout'].Remove('FallbackSubmissionId')
    }
    $mutationState = [ordered]@{
        ApplicationPackages = @($packages | Sort-Object { $_.FileName })
        PackageDeliveryOptions = $delivery
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
$bundleArchive = [IO.Compression.ZipFile]::OpenRead($resolvedBundle)
try {
    $manifestEntry = $bundleArchive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
    if ($null -eq $manifestEntry) { throw 'Bundle manifest is missing.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ([string]$manifest.Bundle.Identity.Name -cne $PackageIdentityName -or
        [string]$manifest.Bundle.Identity.Publisher -cne $Publisher) {
        throw 'Bundle does not match the approved Store package identity.'
    }
} finally { $bundleArchive.Dispose() }

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
$applicationPath = "/v1.0/my/applications/$encodedApplicationId"
$targetPath = if ($isFlight) { "$applicationPath/flights/$([Uri]::EscapeDataString($FlightId))" } else { $applicationPath }
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
    $target = Invoke-StoreApi -Method Get -Path $targetPath
    if ($isFlight -and [string](Get-RequiredProperty $target 'FlightId') -cne $FlightId) {
        throw 'Store returned a different flight.'
    }
    $pending = Get-RequiredProperty $target $pendingProperty
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
    if ([string](Get-RequiredProperty $application 'PackageIdentityName') -cne $PackageIdentityName -or
        [string](Get-RequiredProperty $application 'PublisherName') -cne $Publisher) {
        throw 'Microsoft Store product does not match the approved package identity.'
    }
    $target = $application
    if ($isFlight) {
        $target = Invoke-StoreApi -Method Get -Path $targetPath
        if ([string](Get-RequiredProperty $target 'FlightId') -cne $FlightId) {
            throw 'Store returned a different flight.'
        }
        if (@(Get-RequiredProperty $target 'GroupIds').Count -eq 0) {
            throw 'The development flight must have a tester group.'
        }
    }
    $pendingSubmission = $target.PSObject.Properties[$pendingProperty]
    if ($null -ne $pendingSubmission -and $null -ne $pendingSubmission.Value) {
        throw 'Partner Center already has a pending submission; automation will not replace it.'
    }
    $publishedInfo = $target.PSObject.Properties[$publishedProperty]
    $publishedId = ''
    $publishedMetadataHash = ''
    if ($null -ne $publishedInfo -and $null -ne $publishedInfo.Value) {
        $publishedId = [string](Get-RequiredProperty $publishedInfo.Value 'Id')
        Assert-NonEmptyValue -Name 'Published submission ID' -Value $publishedId
        $encodedPublishedId = [Uri]::EscapeDataString($publishedId)
        $published = Invoke-StoreApi -Method Get -Path "$targetPath/submissions/$encodedPublishedId"
        $publishedMetadataHash = Get-SubmissionMetadataHash $published
    } elseif (-not $isFlight) {
        throw 'The Partner Center product must have a published submission.'
    }

    $draft = Invoke-StoreApi -Method Post `
        -Path $(if ($isFlight) { "$targetPath/submissions" } else { "$targetPath/submissions?isMinimalResponse=true" })
    $draftId = [string](Get-RequiredProperty $draft 'Id')
    Assert-NonEmptyValue -Name 'Draft submission ID' -Value $draftId
    $encodedDraftId = [Uri]::EscapeDataString($draftId)
    Assert-OwnedDraft -SubmissionId $draftId
    $draft = Invoke-StoreApi -Method Get `
        -Path "$targetPath/submissions/$encodedDraftId"
    if ([string](Get-RequiredProperty $draft 'Id') -cne $draftId) {
        throw 'Store returned a different draft than the automation created.'
    }
    $uploadUri = [string](Get-RequiredProperty $draft 'FileUploadUrl')
    Assert-NonEmptyValue -Name 'Draft upload URL' -Value $uploadUri

    if ($isFlight -and [string](Get-RequiredProperty $draft 'FlightId') -cne $FlightId) {
        throw 'Store returned a submission for a different flight.'
    }
    # A first flight has no published baseline; preserve its freshly created settings.
    if (-not $publishedMetadataHash) {
        $published = $draft | ConvertTo-Json -Depth 100 | ConvertFrom-Json
        $publishedMetadataHash = Get-SubmissionMetadataHash $published
    }
    $packages = @(Get-RequiredProperty $draft $packagesProperty)
    foreach ($package in $packages) { $package.FileStatus = 'PendingDelete' }
    $packages += [pscustomobject]@{
        FileName = [IO.Path]::GetFileName($resolvedBundle)
        FileStatus = 'PendingUpload'
        minimumDirectXVersion = 'None'
        minimumSystemRam = 'None'
    }
    $draft.$packagesProperty = $packages
    $deliveryOptions = Get-RequiredProperty $draft 'PackageDeliveryOptions'
    if ($null -ne $deliveryOptions) {
        $packageRollout = Get-RequiredProperty $deliveryOptions 'PackageRollout'
        if ($null -ne $packageRollout) {
            # Even a 100% gradual rollout needs manual finalization before the next submission.
            $packageRollout.IsPackageRollout = $rollout -lt 100
            $packageRollout.PackageRolloutPercentage = $rollout
        }
    }

    $draftMutationHash = Get-SubmissionMutationHash $draft
    $draft = Invoke-StoreApi -Method Put `
        -Path "$targetPath/submissions/$encodedDraftId" `
        -Body $draft
    if ([string](Get-RequiredProperty $draft 'Id') -cne $draftId) {
        throw 'Store updated a different draft than the automation owns.'
    }
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
        -Path "$targetPath/submissions/$encodedDraftId"
    if ([string](Get-RequiredProperty $verifiedDraft 'Id') -cne $draftId) {
        throw 'Store verification returned an unowned draft.'
    }
    if ($isFlight -and [string](Get-RequiredProperty $verifiedDraft 'FlightId') -cne $FlightId) {
        throw 'Store verification returned a different flight.'
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
        -Path "$targetPath/submissions/$encodedDraftId/Commit"
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
            channel = $Channel
            flightId = $FlightId
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
                -Path "$targetPath/submissions/$encodedDraftId" |
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
