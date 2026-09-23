[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Submit-MicrosoftStore.ps1'
$oidcUriScriptPath = Join-Path $PSScriptRoot 'New-GitHubOidcRequestUri.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-store-submission-$([guid]::NewGuid().ToString('N'))"
$tenantId = [guid]'11111111-1111-1111-1111-111111111111'
$clientId = [guid]'22222222-2222-2222-2222-222222222222'
$applicationId = '9NTESTOPENCLAW'
$apiBase = 'https://manage.devcenter.microsoft.com'

function Assert-Fails {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$MessagePattern)
    try { & $Action }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw "Expected failure matching '$MessagePattern'; received: $($_.Exception.Message)"
        }
        $global:LASTEXITCODE = 0
        return
    }
    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

function Copy-Object([object]$Value) {
    return $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json
}

function New-Submission([string]$Id) {
    [pscustomobject]@{
        Id = $Id
        ApplicationCategory = [pscustomobject]@{ category = 'Productivity' }
        Pricing = [pscustomobject]@{ priceId = 'Free'; trialPeriod = 'NoFreeTrial' }
        Visibility = 'Public'
        TargetPublishMode = 'Immediate'
        TargetPublishDate = $null
        Listings = [pscustomobject]@{
            'en-us' = [pscustomobject]@{
                title = 'OpenClaw'
                description = 'Personal AI assistant'
            }
        }
        HardwarePreferences = @('Touch')
        AutomaticBackupEnabled = $true
        CanInstallOnRemovableMedia = $false
        IsGameDvrEnabled = $false
        GamingOptions = @()
        HasExternalInAppProducts = $false
        MeetAccessibilityGuidelines = $true
        NotesForCertification = 'OpenClaw Windows Hub.'
        EnterpriseLicensing = 'Online'
        AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies = $true
        AllowTargetFutureDeviceFamilies = [pscustomobject]@{ Desktop = $true }
        FriendlyName = 'OpenClaw'
        Trailers = @()
        FileUploadUrl = 'https://storage.example.invalid/upload?sas=redacted'
        ApplicationPackages = @(
            [pscustomobject]@{ FileName = 'old-x64.msix'; FileStatus = 'Published' },
            [pscustomobject]@{ FileName = 'old-arm64.msix'; FileStatus = 'Published' }
        )
        PackageDeliveryOptions = [pscustomobject]@{
            PackageRollout = [pscustomobject]@{
                IsPackageRollout = $false
                PackageRolloutPercentage = 0
            }
        }
    }
}

function Reset-Fixture {
    $global:OpenClawStoreTest_calls = [Collections.Generic.List[string]]::new()
    $global:OpenClawStoreTest_published = New-Submission 'published-1'
    $global:OpenClawStoreTest_draft = $null
    $global:OpenClawStoreTest_pendingId = $null
    $global:OpenClawStoreTest_tokenLifetime = 3600
    $global:OpenClawStoreTest_commitStatus = 'CommitStarted'
    $global:OpenClawStoreTest_commitThrows = $false
    $global:OpenClawStoreTest_replaceAfterUpload = $false
    $global:OpenClawStoreTest_driftAfterUpload = $false
    $global:OpenClawStoreTest_mutatePackagesAfterUpload = $false
    $global:OpenClawStoreTest_uploadHash = $null
    $global:OpenClawStoreTest_uploadedEntries = @()
}

$httpInvoker = {
    param($Method, $Uri, $Headers, $Body, $ContentType)
    $safeUri = $Uri -replace 'sas=[^&]+', 'sas=redacted'
    $global:OpenClawStoreTest_calls.Add("$Method $safeUri")
    if ($Uri -like 'https://login.microsoftonline.com/*') {
        if ($Body.client_assertion -cne 'header.payload.signature' -or
            $Body.scope -cne 'https://manage.devcenter.microsoft.com/.default') {
            throw 'Unexpected OAuth client assertion request.'
        }
        return [pscustomobject]@{ access_token = 'access-token-secret'; expires_in = $global:OpenClawStoreTest_tokenLifetime }
    }
    if ([string]$Headers.Authorization -cne 'Bearer access-token-secret' -or
        [string]$Headers.TenantId -cne $tenantId.ToString()) {
        throw 'Store API request did not carry the expected authorization boundary.'
    }

    $applicationPath = "$apiBase/v1.0/my/applications/$applicationId"
    if ($Method -eq 'Get' -and $Uri -ceq $applicationPath) {
        return [pscustomobject]@{
            Id = $applicationId
            PendingApplicationSubmission = if ($null -eq $global:OpenClawStoreTest_pendingId) { $null } else { [pscustomobject]@{ Id = $global:OpenClawStoreTest_pendingId } }
            LastPublishedApplicationSubmission = [pscustomobject]@{ Id = 'published-1' }
        }
    }
    if ($Method -eq 'Get' -and $Uri -ceq "$applicationPath/submissions/published-1") {
        return Copy-Object $global:OpenClawStoreTest_published
    }
    if ($Method -eq 'Post' -and $Uri -ceq "$applicationPath/submissions?isMinimalResponse=true") {
        if ($null -ne $global:OpenClawStoreTest_pendingId) { throw 'Store refused to create a second draft.' }
        $global:OpenClawStoreTest_draft = Copy-Object $global:OpenClawStoreTest_published
        $global:OpenClawStoreTest_draft.Id = 'draft-2'
        $global:OpenClawStoreTest_draft.FriendlyName = 'Generated draft 2'
        $global:OpenClawStoreTest_pendingId = 'draft-2'
        return Copy-Object $global:OpenClawStoreTest_draft
    }
    if ($Method -eq 'Get' -and $Uri -ceq "$applicationPath/submissions/draft-2") {
        if ($null -eq $global:OpenClawStoreTest_draft) { throw 'Draft does not exist.' }
        return Copy-Object $global:OpenClawStoreTest_draft
    }
    if ($Method -eq 'Put' -and $Uri -ceq "$applicationPath/submissions/draft-2") {
        if ($global:OpenClawStoreTest_pendingId -cne 'draft-2') { throw 'Draft ownership changed before update.' }
        $request = $Body | ConvertFrom-Json
        $pendingUpload = @($request.ApplicationPackages | Where-Object { $_.FileStatus -ceq 'PendingUpload' })
        if ($pendingUpload.Count -ne 1 -or
            [string]$pendingUpload[0].minimumDirectXVersion -cne 'None' -or
            [string]$pendingUpload[0].minimumSystemRam -cne 'None') {
            throw 'Pending package omitted required compatibility fields.'
        }
        $global:OpenClawStoreTest_draft = $request
        return Copy-Object $global:OpenClawStoreTest_draft
    }
    if ($Method -eq 'Post' -and $Uri -ceq "$applicationPath/submissions/draft-2/Commit") {
        if ($global:OpenClawStoreTest_pendingId -cne 'draft-2') { throw 'Draft ownership changed before commit.' }
        if ($global:OpenClawStoreTest_commitThrows) { throw 'Commit response was lost.' }
        if ($global:OpenClawStoreTest_commitStatus -ceq 'CommitStarted') {
            $global:OpenClawStoreTest_pendingId = $null
        }
        return [pscustomobject]@{ Status = $global:OpenClawStoreTest_commitStatus }
    }
    if ($Method -eq 'Delete' -and $Uri -ceq "$applicationPath/submissions/draft-2") {
        if ($global:OpenClawStoreTest_pendingId -ceq 'draft-2') { $global:OpenClawStoreTest_pendingId = $null }
        $global:OpenClawStoreTest_draft = $null
        return $null
    }
    throw "Unexpected Store request: $Method $Uri"
}

$uploadInvoker = {
    param($Uri, $Path, $TimeoutSeconds)
    if ($Uri -cne 'https://storage.example.invalid/upload?sas=redacted' -or $TimeoutSeconds -ne 1800) {
        throw 'Unexpected Store upload request.'
    }
    $global:OpenClawStoreTest_uploadHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try { $global:OpenClawStoreTest_uploadedEntries = @($archive.Entries.FullName) }
    finally { $archive.Dispose() }
    if ($global:OpenClawStoreTest_driftAfterUpload) {
        $global:OpenClawStoreTest_draft.Listings.'en-us'.description = 'Unexpected replacement'
    }
    if ($global:OpenClawStoreTest_mutatePackagesAfterUpload) {
        $global:OpenClawStoreTest_draft.ApplicationPackages += [pscustomobject]@{
            FileName = 'competing.msix'
            FileStatus = 'PendingUpload'
        }
    }
    if ($global:OpenClawStoreTest_replaceAfterUpload) { $global:OpenClawStoreTest_pendingId = 'competing-draft' }
}

function Invoke-Submission {
    param(
        [string]$PolicyPath = (Join-Path $testRoot 'policy.json'),
        [string]$BundlePath = (Join-Path $testRoot 'OpenClaw.msixbundle'),
        [string]$EvidencePath = (Join-Path $testRoot 'evidence.json')
    )
    & $scriptPath -BundlePath $BundlePath -ApplicationId $applicationId `
        -TenantId $tenantId -ClientId $clientId `
        -ClientAssertionFile (Join-Path $testRoot 'assertion.jwt') `
        -PolicyPath $PolicyPath -EvidencePath $EvidencePath `
        -HttpInvoker $httpInvoker -UploadInvoker $uploadInvoker
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $testRoot 'OpenClaw.msixbundle'), 'bundle-fixture')
    [IO.File]::WriteAllText((Join-Path $testRoot 'assertion.jwt'), 'header.payload.signature')
    [ordered]@{
        schemaVersion = 1
        environment = 'microsoft-store'
        oidcAudience = 'api://AzureADTokenExchange'
        apiBaseUri = $apiBase
        oauthScope = 'https://manage.devcenter.microsoft.com/.default'
        commitSubmission = $true
        submissionWriterPolicy = 'exclusive-github-environment'
        pendingSubmissionPolicy = 'reject'
        failedDraftPolicy = 'delete-owned-before-commit'
        packageRolloutPercentage = 100
        uploadTimeoutSeconds = 1800
        minimumAccessTokenLifetimeSeconds = 2400
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'policy.json') -Encoding utf8

    Reset-Fixture
    Invoke-Submission
    if ($null -ne $global:OpenClawStoreTest_pendingId -or $global:OpenClawStoreTest_uploadedEntries.Count -ne 1 -or
        $global:OpenClawStoreTest_uploadedEntries[0] -cne 'OpenClaw.msixbundle') {
        throw 'Successful publication did not upload exactly one bundle and commit its owned draft.'
    }
    if (@($global:OpenClawStoreTest_calls | Where-Object { $_ -match '/submissions/draft-2/Commit$' }).Count -ne 1 -or
        @($global:OpenClawStoreTest_calls | Where-Object { $_ -match 'submission publish|msstore' }).Count -ne 0) {
        throw 'Publication was not bound to the verified submission ID.'
    }
    $evidence = Get-Content -LiteralPath (Join-Path $testRoot 'evidence.json') -Raw | ConvertFrom-Json
    if ([string]$evidence.draftSubmissionId -cne 'draft-2' -or
        [string]$evidence.commitStatus -cne 'CommitStarted' -or
        [string]$evidence.publishedMetadataSha256 -cne [string]$evidence.draftMetadataSha256 -or
        [string]$evidence.preparedMutationSha256 -cne [string]$evidence.verifiedMutationSha256) {
        throw 'Store evidence did not bind the verified and committed draft.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_pendingId = 'human-draft'
    Assert-Fails -MessagePattern 'already has a pending submission' -Action { Invoke-Submission }
    if ($global:OpenClawStoreTest_pendingId -cne 'human-draft' -or $global:OpenClawStoreTest_calls -match 'isMinimalResponse') {
        throw 'Pre-existing draft rejection mutated Partner Center.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_replaceAfterUpload = $true
    Assert-Fails -MessagePattern 'no longer the automation-owned draft' -Action { Invoke-Submission }
    if ($global:OpenClawStoreTest_pendingId -cne 'competing-draft' -or $global:OpenClawStoreTest_calls -match '/Commit$') {
        throw 'A replaced draft was deleted or committed.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_driftAfterUpload = $true
    Assert-Fails -MessagePattern 'did not preserve published product metadata' -Action { Invoke-Submission }
    if ($null -ne $global:OpenClawStoreTest_pendingId -or $global:OpenClawStoreTest_calls -match '/Commit$') {
        throw 'Metadata drift did not delete only the automation-owned draft.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_mutatePackagesAfterUpload = $true
    Assert-Fails -MessagePattern 'package mutation state changed' -Action { Invoke-Submission }
    if ($null -ne $global:OpenClawStoreTest_pendingId -or $global:OpenClawStoreTest_calls -match '/Commit$') {
        throw 'Package drift did not delete only the automation-owned draft.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_commitStatus = 'CommitFailed'
    Assert-Fails -MessagePattern 'did not accept the submission commit' -Action { Invoke-Submission }
    if ($null -ne $global:OpenClawStoreTest_pendingId -or
        @($global:OpenClawStoreTest_calls | Where-Object { $_ -match '/Commit$' }).Count -ne 1) {
        throw 'Rejected commit status did not fail and clean up the owned draft.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_commitStatus = 'PendingCommit'
    Assert-Fails -MessagePattern 'did not accept the submission commit' -Action { Invoke-Submission }
    if ($global:OpenClawStoreTest_pendingId -cne 'draft-2' -or
        $global:OpenClawStoreTest_calls -match 'Delete .*/submissions/draft-2$') {
        throw 'Ambiguous commit status deleted the submission with an unknown outcome.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_commitThrows = $true
    Assert-Fails -MessagePattern 'Commit response was lost' -Action { Invoke-Submission }
    if ($global:OpenClawStoreTest_pendingId -cne 'draft-2' -or
        $global:OpenClawStoreTest_calls -match 'Delete .*/submissions/draft-2$') {
        throw 'Ambiguous commit transport failure deleted the submission with an unknown outcome.'
    }

    Reset-Fixture
    $global:OpenClawStoreTest_tokenLifetime = 2000
    Assert-Fails -MessagePattern 'too short-lived' -Action { Invoke-Submission }
    if ($global:OpenClawStoreTest_calls.Count -ne 1) { throw 'Short-lived authorization reached the Store API.' }

    $wrongPackagePath = Join-Path $testRoot 'OpenClaw-x64.msix'
    [IO.File]::WriteAllText($wrongPackagePath, 'standalone-fixture')
    Assert-Fails -MessagePattern 'requires one .msixbundle' -Action {
        Invoke-Submission -BundlePath $wrongPackagePath
    }

    $uriWithoutQuery = & $oidcUriScriptPath `
        -RequestUri 'https://token.actions.githubusercontent.com/oidc' `
        -Audience 'api://AzureADTokenExchange'
    $uriWithQuery = & $oidcUriScriptPath `
        -RequestUri 'https://token.actions.githubusercontent.com/oidc?api-version=1' `
        -Audience 'api://AzureADTokenExchange'
    if ($uriWithoutQuery -cne 'https://token.actions.githubusercontent.com/oidc?audience=api%3A%2F%2FAzureADTokenExchange' -or
        $uriWithQuery -cne 'https://token.actions.githubusercontent.com/oidc?api-version=1&audience=api%3A%2F%2FAzureADTokenExchange' -or
        $uriWithQuery.Contains([char]7)) {
        throw 'OIDC request URI construction is invalid.'
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Get-Variable -Scope Global -Name 'OpenClawStoreTest_*' -ErrorAction SilentlyContinue |
        Remove-Variable -Scope Global -ErrorAction SilentlyContinue
}

$global:LASTEXITCODE = 0
Write-Host 'Microsoft Store submission tests passed.'
