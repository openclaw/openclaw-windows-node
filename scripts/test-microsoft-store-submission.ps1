[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Submit-MicrosoftStore.ps1'
$oidcUriScriptPath = Join-Path $PSScriptRoot 'New-GitHubOidcRequestUri.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "openclaw-store-submission-$([guid]::NewGuid().ToString('N'))"
)
$tenantId = [guid]'11111111-1111-1111-1111-111111111111'
$clientId = [guid]'22222222-2222-2222-2222-222222222222'
$sellerId = '12345678'
$applicationId = '9NTESTOPENCLAW'

function Assert-Fails {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        $global:LASTEXITCODE = 0
        return
    }
    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

function Invoke-Submission {
    param(
        [string]$PolicyPath = (Join-Path $testRoot 'policy.json'),
        [string]$BundlePath = (Join-Path $testRoot 'OpenClaw.msixbundle'),
        [string]$EvidencePath = (Join-Path $testRoot 'evidence.json')
    )

    & $scriptPath `
        -BundlePath $BundlePath `
        -ApplicationId $applicationId `
        -TenantId $tenantId `
        -SellerId $sellerId `
        -ClientId $clientId `
        -ClientAssertionFile (Join-Path $testRoot 'assertion.jwt') `
        -PolicyPath $PolicyPath `
        -EvidencePath $EvidencePath `
        -MSStoreCommand $script:fakeMSStore
}

New-Item -Path $testRoot -ItemType Directory | Out-Null
try {
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'OpenClaw.msixbundle'),
        'bundle-fixture')
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'assertion.jwt'),
        'header.payload.signature')
    [ordered]@{
        schemaVersion = 1
        environment = 'microsoft-store'
        oidcAudience = 'api://AzureADTokenExchange'
        msstoreCliVersion = 'v0.4.3'
        commitSubmission = $true
        pendingSubmissionPolicy = 'reject'
        packageRolloutPercentage = 100
        uploadTimeoutSeconds = 1800
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $testRoot 'policy.json') -Encoding utf8

    $script:fakeMSStore = Join-Path $testRoot 'fake-msstore.ps1'
    $fakeCommand = @'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

($Arguments -join ' ') | Add-Content -LiteralPath $env:FAKE_MSSTORE_LOG
if ($Arguments[0] -eq $env:FAKE_MSSTORE_FAIL_COMMAND) {
    exit 23
}
if (($Arguments[0..1] -join ' ') -eq 'apps get') {
    Write-Output $env:FAKE_MSSTORE_APPLICATION_JSON
}
if (($Arguments[0..1] -join ' ') -eq 'submission get') {
    $count = if (Test-Path -LiteralPath $env:FAKE_MSSTORE_GET_COUNT) {
        [int](Get-Content -LiteralPath $env:FAKE_MSSTORE_GET_COUNT -Raw)
    }
    else {
        0
    }
    ($count + 1) | Set-Content -LiteralPath $env:FAKE_MSSTORE_GET_COUNT
    if ($count -eq 0) {
        Write-Output $env:FAKE_MSSTORE_PUBLISHED_JSON
    }
    else {
        Write-Output $env:FAKE_MSSTORE_DRAFT_JSON
    }
}
exit 0
'@
    [IO.File]::WriteAllText($script:fakeMSStore, $fakeCommand)

    $preservedMetadata = [ordered]@{
        ApplicationCategory = @{ category = 'Productivity' }
        Pricing = @{ priceId = 'Free'; trialPeriod = 'NoFreeTrial' }
        Visibility = 'Public'
        TargetPublishMode = 'Immediate'
        TargetPublishDate = $null
        Listings = [ordered]@{
            'en-us' = [ordered]@{
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
        AllowTargetFutureDeviceFamilies = @{ Desktop = $true }
        FriendlyName = 'OpenClaw'
        Trailers = @()
    }
    $publishedSubmission = [ordered]@{ Id = 'published-1' } + $preservedMetadata
    $draftSubmission = [ordered]@{ Id = 'draft-2' } + $preservedMetadata
    $draftSubmission['Listings'] = [ordered]@{
        'en-us' = [ordered]@{
            description = 'Personal AI assistant'
            title = 'OpenClaw'
        }
    }
    $env:FAKE_MSSTORE_APPLICATION_JSON = [ordered]@{
        Id = $applicationId
        PendingApplicationSubmission = $null
        LastPublishedApplicationSubmission = @{ Id = 'published-1' }
    } | ConvertTo-Json -Depth 100 -Compress
    $env:FAKE_MSSTORE_PUBLISHED_JSON = $publishedSubmission |
        ConvertTo-Json -Depth 100 -Compress
    $env:FAKE_MSSTORE_DRAFT_JSON = $draftSubmission |
        ConvertTo-Json -Depth 100 -Compress

    $logPath = Join-Path $testRoot 'msstore.log'
    $env:FAKE_MSSTORE_LOG = $logPath
    $env:FAKE_MSSTORE_GET_COUNT = Join-Path $testRoot 'get-count.txt'
    $env:FAKE_MSSTORE_FAIL_COMMAND = ''
    $env:MSSTORE_CLIENT_ASSERTION = 'previous-assertion'
    $env:MSSTORE_CLIENT_ASSERTION_FILE = 'previous-file'

    Invoke-Submission

    $calls = @(Get-Content -LiteralPath $logPath)
    if ($calls.Count -ne 6) {
        throw "Expected six MSStore CLI calls; received $($calls.Count)."
    }
    if ($calls[0] -cne (
        "reconfigure --tenantId $tenantId --sellerId $sellerId " +
        "--clientId $clientId --clientAssertion")) {
        throw "Unexpected reconfigure call: $($calls[0])"
    }
    $resolvedBundle = (Resolve-Path -LiteralPath (
        Join-Path $testRoot 'OpenClaw.msixbundle')).Path
    if ($calls[1] -cne "apps get $applicationId" -or
        $calls[2] -cne "submission get $applicationId") {
        throw 'Store publication did not snapshot the existing product state.'
    }
    if ($calls[3] -cne (
        "publish $resolvedBundle --appId $applicationId " +
        '--packageRolloutPercentage 100 --uploadTimeout 1800 --noCommit')) {
        throw "Unexpected publish call: $($calls[3])"
    }
    if ($calls[4] -cne "submission get $applicationId" -or
        $calls[5] -cne "submission publish $applicationId") {
        throw 'Store publication did not verify and commit the draft.'
    }
    if ($calls -match 'header\.payload|assertion\.jwt') {
        throw 'The Store CLI command line exposed the OIDC assertion.'
    }
    if ($env:MSSTORE_CLIENT_ASSERTION -cne 'previous-assertion' -or
        $env:MSSTORE_CLIENT_ASSERTION_FILE -cne 'previous-file') {
        throw 'Store submission did not restore the assertion environment.'
    }

    $evidence = Get-Content -LiteralPath (Join-Path $testRoot 'evidence.json') -Raw |
        ConvertFrom-Json
    $expectedHash = (Get-FileHash -LiteralPath $resolvedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$evidence.bundleSha256 -cne $expectedHash -or
        [string]$evidence.applicationId -cne $applicationId -or
        [string]$evidence.msstoreCliVersion -cne 'v0.4.3' -or
        [string]$evidence.publishedMetadataSha256 -cne
            [string]$evidence.draftMetadataSha256) {
        throw 'Store submission evidence did not bind the submitted bundle.'
    }

    Clear-Content -LiteralPath $logPath
    Remove-Item -LiteralPath $env:FAKE_MSSTORE_GET_COUNT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $testRoot 'evidence.json') -Force
    $env:FAKE_MSSTORE_FAIL_COMMAND = 'publish'
    Assert-Fails -MessagePattern 'publication failed with exit code 23' -Action {
        Invoke-Submission
    }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'evidence.json')) {
        throw 'A failed Store submission must not write success evidence.'
    }

    Clear-Content -LiteralPath $logPath
    Remove-Item -LiteralPath $env:FAKE_MSSTORE_GET_COUNT -ErrorAction SilentlyContinue
    $env:FAKE_MSSTORE_FAIL_COMMAND = ''
    $changedDraft = [ordered]@{ Id = 'draft-3' } + $preservedMetadata
    $changedDraft.Listings = @{
        'en-us' = @{ description = 'Unexpected replacement' }
    }
    $env:FAKE_MSSTORE_DRAFT_JSON = $changedDraft |
        ConvertTo-Json -Depth 100 -Compress
    Assert-Fails -MessagePattern 'did not preserve published product metadata' -Action {
        Invoke-Submission
    }
    $calls = @(Get-Content -LiteralPath $logPath)
    if ($calls -contains "submission publish $applicationId") {
        throw 'Metadata drift must block committing the Store draft.'
    }

    $env:FAKE_MSSTORE_APPLICATION_JSON = [ordered]@{
        Id = $applicationId
        PendingApplicationSubmission = @{ Id = 'pending-1' }
        LastPublishedApplicationSubmission = @{ Id = 'published-1' }
    } | ConvertTo-Json -Depth 100 -Compress
    Assert-Fails -MessagePattern 'already has a pending submission' -Action {
        Invoke-Submission
    }

    $invalidPolicyPath = Join-Path $testRoot 'invalid-policy.json'
    [ordered]@{
        schemaVersion = 1
        environment = 'microsoft-store'
        oidcAudience = 'api://AzureADTokenExchange'
        msstoreCliVersion = 'latest'
        commitSubmission = $true
        pendingSubmissionPolicy = 'reject'
        packageRolloutPercentage = 100
        uploadTimeoutSeconds = 1800
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $invalidPolicyPath -Encoding utf8
    Assert-Fails -MessagePattern 'pin an exact' -Action {
        Invoke-Submission -PolicyPath $invalidPolicyPath
    }

    $wrongPackagePath = Join-Path $testRoot 'OpenClaw-x64.msix'
    [IO.File]::WriteAllText($wrongPackagePath, 'standalone-fixture')
    Assert-Fails -MessagePattern 'requires one .msixbundle' -Action {
        Invoke-Submission -BundlePath $wrongPackagePath
    }

    $uriWithoutQuery = & $oidcUriScriptPath `
        -RequestUri 'https://token.actions.githubusercontent.com/oidc' `
        -Audience 'api://AzureADTokenExchange'
    if ($uriWithoutQuery -cne (
            'https://token.actions.githubusercontent.com/oidc?' +
            'audience=api%3A%2F%2FAzureADTokenExchange')) {
        throw "Unexpected OIDC URI without a query: $uriWithoutQuery"
    }
    $uriWithQuery = & $oidcUriScriptPath `
        -RequestUri 'https://token.actions.githubusercontent.com/oidc?api-version=1' `
        -Audience 'api://AzureADTokenExchange'
    if ($uriWithQuery -cne (
            'https://token.actions.githubusercontent.com/oidc?api-version=1&' +
            'audience=api%3A%2F%2FAzureADTokenExchange')) {
        throw "Unexpected OIDC URI with a query: $uriWithQuery"
    }
    if ($uriWithQuery.Contains([char]7)) {
        throw 'The OIDC URI contains PowerShell alert escape U+0007.'
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_LOG -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_FAIL_COMMAND -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_GET_COUNT -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_APPLICATION_JSON -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_PUBLISHED_JSON -ErrorAction SilentlyContinue
    Remove-Item Env:FAKE_MSSTORE_DRAFT_JSON -ErrorAction SilentlyContinue
    Remove-Item Env:MSSTORE_CLIENT_ASSERTION -ErrorAction SilentlyContinue
    Remove-Item Env:MSSTORE_CLIENT_ASSERTION_FILE -ErrorAction SilentlyContinue
}

$global:LASTEXITCODE = 0
Write-Host 'Microsoft Store submission tests passed.'
