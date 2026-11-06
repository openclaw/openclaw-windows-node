[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "store-flight-$([guid]::NewGuid().ToString('N'))"
$flightId = '33333333-3333-3333-3333-333333333333'
$applicationPath = 'https://manage.devcenter.microsoft.com/v1.0/my/applications/TESTPRODUCT'
$flightPath = "$applicationPath/flights/$flightId"
function Copy-Fixture($Value) { $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json }
function Reset-Fixture {
    $global:StoreFlightTest_calls = [Collections.Generic.List[string]]::new()
    $global:StoreFlightTest_pending = $null
    $global:StoreFlightTest_scenario = ''
    $global:StoreFlightTest_published = [pscustomobject]@{
        Id = 'published'; FlightId = $flightId; FlightPackages = @()
        TargetPublishMode = 'Immediate'; NotesForCertification = 'Fixture instructions'
        PackageDeliveryOptions = [pscustomobject]@{
            PackageRollout = [pscustomobject]@{ IsPackageRollout = $false; PackageRolloutPercentage = 0 }
        }
        FileUploadUrl = 'https://example.invalid/upload'
    }
}
$http = {
    param($Method, $Uri, $Headers, $Body, $ContentType)
    $applicationPath = 'https://manage.devcenter.microsoft.com/v1.0/my/applications/TESTPRODUCT'
    $flightPath = "$applicationPath/flights/33333333-3333-3333-3333-333333333333"
    $global:StoreFlightTest_calls.Add("$Method $Uri")
    if ($Uri -like 'https://login.microsoftonline.com/*') {
        return [pscustomobject]@{ access_token = 'fixture'; expires_in = 3600 }
    }
    if ($Method -eq 'Get' -and $Uri -ceq $applicationPath) {
        return [pscustomobject]@{
            Id = 'TESTPRODUCT'; PackageIdentityName = 'Test.Product'; PublisherName = 'CN=Test'
            # A production draft must neither block nor become a flight target.
            PendingApplicationSubmission = [pscustomobject]@{ Id = 'unrelated-production-draft' }
        }
    }
    if ($Method -eq 'Get' -and $Uri -ceq $flightPath) {
        return [pscustomobject]@{
            FlightId = if ($global:StoreFlightTest_scenario -eq 'wrong-flight') { 'wrong' } else { $flightId }
            GroupIds = if ($global:StoreFlightTest_scenario -eq 'no-group') { @() } else { @('testers') }
            PendingFlightSubmission = if ($global:StoreFlightTest_pending) { [pscustomobject]@{ Id = $global:StoreFlightTest_pending } } else { $null }
            LastPublishedFlightSubmission = if ($global:StoreFlightTest_scenario -eq 'first-flight') { $null } else { [pscustomobject]@{ Id = 'published' } }
        }
    }
    if ($Method -eq 'Get' -and $Uri -ceq "$flightPath/submissions/published") { return Copy-Fixture $global:StoreFlightTest_published }
    if ($Method -eq 'Post' -and $Uri -ceq "$flightPath/submissions") {
        $global:StoreFlightTest_draft = Copy-Fixture $global:StoreFlightTest_published
        $global:StoreFlightTest_draft.Id = 'owned'
        $global:StoreFlightTest_pending = 'owned'
        return Copy-Fixture $global:StoreFlightTest_draft
    }
    if ($Method -eq 'Get' -and $Uri -ceq "$flightPath/submissions/owned") { return Copy-Fixture $global:StoreFlightTest_draft }
    if ($Method -eq 'Put' -and $Uri -ceq "$flightPath/submissions/owned") {
        $global:StoreFlightTest_draft = $Body | ConvertFrom-Json
        if ($global:StoreFlightTest_draft.PSObject.Properties.Name -contains 'ApplicationPackages' -or
            @($global:StoreFlightTest_draft.FlightPackages).Count -ne 1 -or
            $global:StoreFlightTest_draft.FlightPackages[0].FileName -cne 'fixture.msixbundle') {
            throw 'Flight upload used the wrong package contract.'
        }
        return Copy-Fixture $global:StoreFlightTest_draft
    }
    if ($Method -eq 'Post' -and $Uri -ceq "$flightPath/submissions/owned/Commit") {
        if ($global:StoreFlightTest_scenario -eq 'lost-commit') { throw 'Lost commit response.' }
        $global:StoreFlightTest_pending = $null
        return [pscustomobject]@{ Status = 'CommitStarted' }
    }
    if ($Method -eq 'Delete' -and $Uri -ceq "$flightPath/submissions/owned") { $global:StoreFlightTest_pending = $null; return }
    throw "Unexpected request: $Method $Uri"
}
$upload = {
    param($Uri, $Path, $TimeoutSeconds)
    if ($Uri -cne 'https://example.invalid/upload' -or -not (Test-Path $Path)) { throw 'Invalid upload.' }
    switch ($global:StoreFlightTest_scenario) {
        'metadata-drift' { $global:StoreFlightTest_draft.NotesForCertification = 'changed' }
        'wrong-submission-flight' { $global:StoreFlightTest_draft.FlightId = 'wrong' }
        'replaced-draft' { $global:StoreFlightTest_pending = 'foreign' }
        'package-drift' { $global:StoreFlightTest_draft.FlightPackages[0].FileName = 'changed.msixbundle' }
    }
}
function Invoke-Fixture([string]$Channel = 'development', [string]$Id = $flightId) {
    $parameters = @{
        BundlePath = Join-Path $root 'fixture.msixbundle'
        ApplicationId = 'TESTPRODUCT'; PackageIdentityName = 'Test.Product'; Publisher = 'CN=Test'
        Channel = $Channel; FlightId = $Id
        TenantId = '11111111-1111-1111-1111-111111111111'
        ClientId = '22222222-2222-2222-2222-222222222222'
        ClientAssertionFile = Join-Path $root 'assertion'
        EvidencePath = Join-Path $root 'evidence.json'
        HttpInvoker = $http; UploadInvoker = $upload
    }
    & (Join-Path $PSScriptRoot 'Submit-MicrosoftStore.ps1') @parameters
}
function Assert-Rejected([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected rejection: $Pattern"
}
try {
    New-Item (Join-Path $root 'bundle/AppxMetadata') -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'bundle/AppxMetadata/AppxBundleManifest.xml'),
        '<Bundle><Identity Name="Test.Product" Publisher="CN=Test" /></Bundle>')
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root 'bundle'), (Join-Path $root 'fixture.msixbundle'))
    [IO.File]::WriteAllText((Join-Path $root 'assertion'), 'fixture-assertion')
    foreach ($case in @('', 'first-flight', 'Development', 'DEVELOPMENT')) {
        Reset-Fixture
        $global:StoreFlightTest_scenario = $case
        Invoke-Fixture -Channel $(if ($case -in @('Development', 'DEVELOPMENT')) { $case } else { 'development' })
        $evidence = Get-Content (Join-Path $root 'evidence.json') -Raw | ConvertFrom-Json
        if ($evidence.channel -cne 'development' -or $evidence.flightId -cne $flightId -or
            $evidence.commitStatus -cne 'CommitStarted' -or $global:StoreFlightTest_pending) { throw 'Missing flight commit receipt.' }
        if ($global:StoreFlightTest_calls -match '/applications/TESTPRODUCT/submissions') { throw 'Flight touched production submissions.' }
    }
    foreach ($case in @(
        @{ Name = 'wrong-flight'; Error = 'different flight' },
        @{ Name = 'no-group'; Error = 'tester group' },
        @{ Name = 'metadata-drift'; Error = 'preserve published product metadata' },
        @{ Name = 'wrong-submission-flight'; Error = 'different flight' },
        @{ Name = 'package-drift'; Error = 'mutation state changed' },
        @{ Name = 'replaced-draft'; Error = 'no longer the automation-owned' },
        @{ Name = 'lost-commit'; Error = 'Lost commit response' }
    )) {
        Reset-Fixture
        $global:StoreFlightTest_scenario = $case.Name
        Assert-Rejected { Invoke-Fixture } $case.Error
        if ($case.Name -eq 'lost-commit' -and ($global:StoreFlightTest_pending -cne 'owned' -or $global:StoreFlightTest_calls -match '^Delete ')) {
            throw 'Ambiguous flight commit was deleted.'
        }
        if ($case.Name -ne 'lost-commit' -and $global:StoreFlightTest_calls -match '/Commit$') { throw 'Invalid flight reached commit.' }
    }
    Reset-Fixture
    $global:StoreFlightTest_pending = 'foreign'
    Assert-Rejected { Invoke-Fixture } 'already has a pending'
    if ($global:StoreFlightTest_pending -cne 'foreign' -or $global:StoreFlightTest_calls -match '^Post .*flights|^Delete ') { throw 'Foreign draft changed.' }
    Reset-Fixture
    Assert-Rejected { Invoke-Fixture -Id '' } 'requires the configured flight'
    Assert-Rejected { Invoke-Fixture -Channel production } 'must not specify a flight'
    if ($global:StoreFlightTest_calls.Count) { throw 'Invalid destination reached authentication.' }
    foreach ($channel in @('Development', 'DEVELOPMENT')) {
        Assert-Rejected { Invoke-Fixture -Channel $channel -Id '' } 'requires the configured flight'
    }
    foreach ($channel in @('Production', 'PRODUCTION')) {
        Assert-Rejected { Invoke-Fixture -Channel $channel } 'must not specify a flight'
    }
    if ($global:StoreFlightTest_calls.Count) { throw 'Case-variant destination reached authentication.' }
    Write-Host 'Store flight submission: 18 scenarios passed.'
} finally {
    Get-Variable -Scope Global -Name StoreFlightTest_* | Remove-Variable -Scope Global
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
