<#
.SYNOPSIS
    Offline promotion policy, retry, artifact binding, and workflow contract tests.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'scripts\AlphaPromotion.ps1')
$sourceSha = 'a' * 40
$pipelineSha = 'b' * 40
$previousSha = 'c' * 40
$tagSha = 'd' * 40
$alpha = 'v2026.9.5-alpha.93'
$state = @{}
$cases = 0
$publicationToken = 'test-publication-token'
$previousPublicationToken = $env:STABLE_RELEASE_TOKEN
$env:STABLE_RELEASE_TOKEN = $publicationToken
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-promotion-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

function Assert-Equal($Actual, $Expected) {
    if ($Actual -cne $Expected) { throw "Expected '$Expected', received '$Actual'." }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($Message, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected '$Message', received '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected failure: $Message"
}
function Reset-Fixture {
    $state.Clear()
    $env:STABLE_RELEASE_TOKEN = $publicationToken
    $state.alphaSha = $sourceSha
    $state.published = $true
    $state.currentTag = 'v2026.9.4'
    $state.tag = $null
    $state.annotation = $null
    $state.release = $null
    $state.extraReleases = @()
    $state.writes = 0
    $state.approval = $true
    $state.allowedBranch = 'main'
    $state.ancestry = 'ahead'
    $state.runSha = $sourceSha
    $state.jobConclusion = 'success'
    $state.runAttempt = 1
    $state.failUpload = $false
    $state.collision = $false
    $state.uploadsBeforeFailure = 0
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    Reset-Fixture
    try { & $Action | Out-Null }
    catch { throw "Case '$Name' failed: $($_.Exception.Message)" }
    $script:cases++
}
function Candidate {
    Get-AlphaPromotion -AlphaTag $alpha -PipelineSha $pipelineSha
}

# No HTTP or real GitHub mutations are allowed beyond this seam.
function Invoke-PromotionApi {
    param([string]$Path, [string]$Method = 'GET', [object]$Body, [string]$Token, [switch]$AllowNotFound)
    if ($Method -ne 'GET') { $state.writes++ }
    switch -Regex ($Path) {
        '^releases/tags/v2026\.9\.5-alpha\.93$' {
            return [pscustomobject]@{ tag_name = $alpha; draft = $false; prerelease = $true; published_at = $(if ($state.published) { '2026-10-02' } else { $null }) }
        }
        '^releases/latest$' {
            return [pscustomobject]@{ tag_name = $state.currentTag; draft = $false; prerelease = $false; published_at = '2026-09-15' }
        }
        '^git/ref/tags/' {
            $name = $Path.Substring('git/ref/tags/'.Length)
            if ($name -ceq $alpha) { $sha = $state.alphaSha; $type = 'commit' }
            elseif ($name -ceq $state.currentTag) { $sha = $previousSha; $type = 'commit' }
            elseif ($name -ceq 'v2026.9.5' -and $state.tag) { $sha = $tagSha; $type = 'tag' }
            elseif ($AllowNotFound) { return $null }
            else { throw 'Unknown required tag.' }
            return [pscustomobject]@{ ref = "refs/tags/$name"; object = [pscustomobject]@{ sha = $sha; type = $type } }
        }
        '^git/tags/' {
            return [pscustomobject]@{ message = $state.annotation; object = [pscustomobject]@{ sha = $state.tag; type = 'commit' } }
        }
        '^git/ref/heads/main$' { return [pscustomobject]@{ object = [pscustomobject]@{ sha = $pipelineSha } } }
        '^compare/' { return [pscustomobject]@{ status = $state.ancestry } }
        '^actions/workflows/ci.yml$' { return [pscustomobject]@{ id = 7 } }
        '^actions/workflows/ci.yml/runs\?' {
            return [pscustomobject]@{ workflow_runs = @([pscustomobject]@{
                id = 100; run_attempt = $state.runAttempt; head_sha = $state.runSha; head_branch = $alpha
                workflow_id = 7; event = 'workflow_dispatch'; status = 'completed'; conclusion = 'success'
            }) }
        }
        '^actions/runs/100/attempts/\d+/jobs\?' {
            $names = @('CI Gate', 'release', 'Core and CLI tests', 'Tray, setup, and integration tests',
                'UI, functional, and accessibility tests', 'Setup and connect E2E', 'Revocation recovery E2E',
                'Network recovery E2E', 'x64 release publish smoke', 'ARM64 release publish',
                'MSIX artifacts (x64)', 'MSIX artifacts (arm64)', 'Multi-architecture Store MSIX bundle')
            return [pscustomobject]@{ jobs = @($names | ForEach-Object { [pscustomobject]@{ name = $_; conclusion = $state.jobConclusion } }) }
        }
        '^environments/stable-release$' {
            return [pscustomobject]@{
                can_admins_bypass = $false
                deployment_branch_policy = [pscustomobject]@{ custom_branch_policies = $true }
                protection_rules = @([pscustomobject]@{
                    type = 'required_reviewers'; reviewers = @('maintainer'); prevent_self_review = $state.approval
                })
            }
        }
        '^environments/stable-release/deployment-branch-policies$' {
            return [pscustomobject]@{
                total_count = 1
                branch_policies = @([pscustomobject]@{ name = $state.allowedBranch; type = 'branch' })
            }
        }
        '^releases/tags/v2026\.9\.5$' {
            if ($state.release -and -not $state.release.draft) { return $state.release }
            if ($AllowNotFound) { return $null }
            throw 'The by-tag endpoint cannot find an unpublished draft.'
        }
        '^releases\?per_page=100&page=(\d+)$' {
            $releases = @($state.release | Where-Object { $null -ne $_ }) + $state.extraReleases
            # Invoke-RestMethod emits a JSON array as one pipeline object.
            return ,@($releases | Select-Object -Skip (([int]$Matches[1] - 1) * 100) -First 100)
        }
        '^releases/500$' {
            if ($Method -ceq 'PATCH') {
                Assert-Equal $Token $publicationToken
                Assert-Equal $Body.draft $false
                Assert-Equal $Body.prerelease $false
                Assert-Equal $Body.make_latest 'true'
                $state.release.draft = $false
                $state.release.published_at = '2026-10-06'
                $state.currentTag = 'v2026.9.5'
            }
            return $state.release
        }
        '^git/tags$' {
            Assert-Equal $Token ''
            Assert-Equal $Method 'POST'
            $state.annotation = $Body.message
            return [pscustomobject]@{ sha = $tagSha }
        }
        '^git/refs$' {
            Assert-Equal $Token ''
            Assert-Equal $Method 'POST'
            if ($state.tag -or $state.collision) { throw 'Ref creation collision.' }
            $state.tag = $sourceSha
            return [pscustomobject]@{ ref = $Body.ref }
        }
        '^releases/generate-notes$' {
            Assert-Equal $Body.previous_tag_name 'v2026.9.4'
            Assert-Equal $Body.target_commitish $sourceSha
            Assert-Equal $Body.tag_name 'v2026.9.5'
            return [pscustomobject]@{ body = 'Only candidate changes.' }
        }
        '^releases$' {
            Assert-Equal $Token $publicationToken
            Assert-Equal $Body.draft $true
            Assert-Equal $Body.make_latest 'false'
            $state.release = [pscustomobject]@{
                id = 500; tag_name = $Body.tag_name; body = $Body.body; draft = $true; prerelease = $false
                published_at = $null; assets = @()
            }
            return $state.release
        }
        default { throw "Unexpected API call: $Method $Path" }
    }
}

function gh {
    if ($args[0] -cne 'release' -or $args[2] -cne 'v2026.9.5') { throw 'Unexpected gh call.' }
    $state.writes++
    if ($args[1] -ceq 'upload') {
        if ($state.failUpload -and $state.release.assets.Count -ge $state.uploadsBeforeFailure) {
            $global:LASTEXITCODE = 1
            return
        }
        $file = Get-Item -LiteralPath $args[3]
        $state.release.assets += [pscustomobject]@{
            name = $file.Name; digest = "sha256:$((Get-FileHash $file.FullName).Hash.ToLowerInvariant())"
            size = $file.Length; state = 'uploaded'
        }
    } else { throw 'Unexpected gh mutation.' }
    $global:LASTEXITCODE = 0
}

function New-ArtifactFixture([object]$Record) {
    $directory = Join-Path $temporaryRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($name in (Get-PromotionAssetNames $Record.version)) {
        Set-Content -LiteralPath (Join-Path $directory $name) -Value "Synthetic $name"
    }
    $allocation = [pscustomobject][ordered]@{
        schemaVersion = 1; sourceVersion = '2026.9.5'; sourceCommit = $sourceSha
        sourceRef = 'refs/tags/v2026.9.5'; repository = 'openclaw/openclaw-windows-node'
        baseVersion = '2026.9.5'; packageBaseVersion = '2026.9.510'; storePackageVersion = '2026.9.510.0'
        packagingRevision = 10; allocation = 'reserved'; reservationRef = 'refs/tags/msix-package/2026.9.5/510'
    }
    $manifest = New-PromotionArtifactManifest $Record $directory $allocation '200' '1'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'promotion.json')
    return [pscustomobject]@{ directory = $directory; manifest = $manifest }
}

try {
    Test-Case 'preview derives exact stable target without writes' {
        $record = Candidate
        Assert-Equal $record.stableTag 'v2026.9.5'
        Assert-Equal $record.sourceSha $sourceSha
        Assert-Equal $state.writes 0
    }
    Test-Case 'reject noncanonical and injected candidates' {
        foreach ($tag in @('main', 'v2026.9.5', 'v2026.9.5-beta.1', 'v2026.09.5-alpha.1', 'v2026.9.5-alpha.01', "v2026.9.5-alpha.1`n")) {
            Assert-Throws { Get-AlphaPromotion $tag $pipelineSha } 'canonical'
        }
    }
    Test-Case 'unpublished alpha' { $state.published = $false; Assert-Throws { Candidate } 'published alpha' }
    Test-Case 'older and equal stable bases' {
        foreach ($tag in @('v2026.9.5', 'v2026.9.5-1', 'v2026.10.1')) {
            $state.currentTag = $tag
            Assert-Throws { Candidate } 'advance'
        }
    }
    Test-Case 'candidate outside approved ancestry' { $state.ancestry = 'diverged'; Assert-Throws { Candidate } 'ancestry' }
    Test-Case 'wrong source CI' { $state.runSha = $pipelineSha; Assert-Throws { Candidate } 'No successful' }
    Test-Case 'skipped required lane is not proof' { $state.jobConclusion = 'skipped'; Assert-Throws { Candidate } 'required job' }
    Test-Case 'moved alpha rejected after preview' {
        $record = Candidate
        $state.alphaSha = $pipelineSha
        Assert-Throws { New-AlphaPromotionTag $record } 'moved'
        Assert-Equal $state.writes 0
    }
    Test-Case 'missing approval blocks before ref creation' {
        $record = Candidate
        $state.approval = $false
        Assert-Throws { New-AlphaPromotionTag $record } 'required reviewers'
        Assert-Equal $state.writes 0
    }
    Test-Case 'environment must restrict deployment to exact main' {
        $record = Candidate
        $state.allowedBranch = '*'
        Assert-Throws { New-AlphaPromotionTag $record } 'exact main'
        Assert-Equal $state.writes 0
    }
    Test-Case 'reserve is immutable and repeatable' {
        $record = Candidate
        New-AlphaPromotionTag $record | Out-Null
        Assert-Equal $state.writes 2
        New-AlphaPromotionTag $record | Out-Null
        Assert-Equal $state.writes 2
        Assert-Equal (Candidate).sourceSha $sourceSha
    }
    Test-Case 'conflicting reservation' {
        $state.tag = $pipelineSha; $state.annotation = '{}'
        Assert-Throws { Candidate } 'different promotion provenance'
    }
    Test-Case 'concurrent ref collision cannot overwrite another tag' {
        $record = Candidate
        $state.collision = $true
        Assert-Throws { New-AlphaPromotionTag $record } 'collision'
        Assert-Equal $state.tag $null
    }
    Test-Case 'an unrelated draft is rejected before reservation' {
        $state.release = [pscustomobject]@{
            tag_name = 'v2026.9.5'; draft = $true; published_at = $null; body = 'Unrelated release'
        }
        Assert-Throws { Candidate } 'unrelated draft'
        Assert-Equal $state.writes 0
    }
    Test-Case 'duplicate target drafts across pages are rejected before reservation' {
        $state.release = [pscustomobject]@{
            tag_name = 'v2026.9.5'; draft = $true; published_at = $null; body = 'Unrelated release'
        }
        $state.extraReleases = @(1..99 | ForEach-Object { [pscustomobject]@{ tag_name = "v2026.8.$_" } }) +
            @($state.release)
        Assert-Throws { Candidate } 'Multiple releases'
        Assert-Equal $state.writes 0
    }
    Test-Case 'published release is immutable' {
        $state.release = [pscustomobject]@{ tag_name = 'v2026.9.5'; draft = $false; published_at = '2026-10-06' }
        Assert-Throws { Candidate } 'already published'
    }
    Test-Case 'new pipeline cannot silently reuse the old reservation' {
        $record = Candidate
        New-AlphaPromotionTag $record | Out-Null
        Assert-Throws { Get-AlphaPromotion $alpha ('e' * 40) } 'different promotion provenance'
    }
    Test-Case 'artifact manifest roundtrip and altered bytes' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $manifest = Get-Content (Join-Path $fixture.directory 'promotion.json') -Raw | ConvertFrom-Json
        Assert-PromotionArtifactManifest $manifest $record $fixture.directory '200' '1'
        Add-Content (Join-Path $fixture.directory 'OpenClawCompanion-Setup-x64.exe') 'tamper'
        Assert-Throws { Assert-PromotionArtifactManifest $manifest $record $fixture.directory '200' '1' } 'approved manifest'
    }
    Test-Case 'wrong run and extra files' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        Assert-Throws { Assert-PromotionArtifactManifest $fixture.manifest $record $fixture.directory '201' '1' } 'approved manifest'
        Assert-Throws { Assert-PromotionArtifactManifest $fixture.manifest $record $fixture.directory '200' '2' } 'approved manifest'
        Set-Content (Join-Path $fixture.directory 'unexpected.exe') 'extra'
        Assert-Throws { Assert-PromotionArtifactManifest $fixture.manifest $record $fixture.directory '200' '1' } 'Unexpected files'
    }
    Test-Case 'approved publication creates the tag and preserves exact prepared bytes' {
        $record = Candidate
        $fixture = New-ArtifactFixture $record
        Publish-AlphaPromotion $record $fixture.directory '200' '1'
        Assert-Equal $state.release.draft $false
        Assert-Equal $state.tag $sourceSha
        Assert-Equal $state.release.assets.Count 7
        Assert-Equal $state.release.body.Contains('not Microsoft Store-signed') $true
        Assert-Equal $state.release.body.Contains('actions/runs/200') $true
        Assert-Equal $state.currentTag 'v2026.9.5'
    }
    Test-Case 'partial upload retries the same private draft' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $state.failUpload = $true
        $state.uploadsBeforeFailure = 3
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'Could not upload'
        Assert-Equal $state.release.draft $true
        Assert-Equal $state.release.assets.Count 3
        $state.failUpload = $false
        Publish-AlphaPromotion $record $fixture.directory '200' '1'
        Assert-Equal $state.release.draft $false
    }
    Test-Case 'changed draft bytes cannot be overwritten' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $state.failUpload = $true; $state.uploadsBeforeFailure = 3
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'Could not upload'
        $state.release.assets[0].digest = 'sha256:' + ('0' * 64)
        $state.failUpload = $false
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'different bytes'
        Assert-Equal $state.release.draft $true
    }
    Test-Case 'rebuilt outputs do not silently adopt the old draft' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $state.failUpload = $true
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'Could not upload'
        $newManifest = New-PromotionArtifactManifest $record $fixture.directory $fixture.manifest.msixVersionAllocation '200' '2'
        $newManifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $fixture.directory 'promotion.json')
        $state.failUpload = $false
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '2' } 'exact prepared artifacts'
    }
    Test-Case 'missing publication credential fails before tag or release I/O' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $env:STABLE_RELEASE_TOKEN = ''
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'distinct STABLE_RELEASE_TOKEN'
        Assert-Equal $state.tag $null
        Assert-Equal $state.writes 0
    }
    Test-Case 'invalid publication credential fails before tag or release I/O' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' -ReleaseToken "synthetic`n" } 'distinct STABLE_RELEASE_TOKEN'
        Assert-Equal $state.tag $null
        Assert-Equal $state.writes 0
    }
    Test-Case 'workflow token cannot substitute for publication credential' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $savedToken = $env:GH_TOKEN
        try {
            $env:GH_TOKEN = 'synthetic-workflow-token'
            Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' -ReleaseToken $env:GH_TOKEN } 'distinct STABLE_RELEASE_TOKEN'
            Assert-Equal $state.tag $null
            Assert-Equal $state.writes 0
        } finally { $env:GH_TOKEN = $savedToken }
    }
    Test-Case 'cancelled preparation leaves stable target available' {
        $record = Get-AlphaPromotion -AlphaTag $alpha -PipelineSha $pipelineSha -RequireApproval
        $fixture = New-ArtifactFixture $record
        Assert-Equal $state.tag $null
        Assert-Equal $state.writes 0
        Assert-Equal (Candidate).stableTag $record.stableTag
    }
    Test-Case 'altered artifact blocks publication before tag creation' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        Add-Content (Join-Path $fixture.directory 'OpenClawCompanion-Setup-x64.exe') 'tamper'
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'approved manifest'
        Assert-Equal $state.tag $null
        Assert-Equal $state.writes 0
    }
    Test-Case 'lost approval protection blocks publication before tag creation' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $state.approval = $false
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'required reviewers'
        Assert-Equal $state.tag $null
        Assert-Equal $state.writes 0
    }
    Test-Case 'another stable publication invalidates the pending approval' {
        $record = Candidate; $fixture = New-ArtifactFixture $record
        $state.currentTag = 'v2026.10.1'
        Assert-Throws { Publish-AlphaPromotion $record $fixture.directory '200' '1' } 'advance'
    }

    $workflow = Get-Content (Join-Path $RepoRoot '.github\workflows\ci.yml') -Raw
    $entry = Get-Content (Join-Path $RepoRoot '.github\workflows\promote-alpha-release.yml') -Raw
    foreach ($name in @('metadata', 'core-tests', 'tray-tests', 'ui-tests', 'setup-e2e', 'revocation-e2e',
        'network-e2e', 'build-x64', 'build-arm64', 'build-msix')) {
        $job = [regex]::Match($workflow, "(?ms)^  ${name}:\r?`n.*?(?=^  [a-z][a-z-]+:|\z)").Value
        if (-not $job.Contains('ref: ${{ inputs.promotion_source_sha || github.sha }}') -or
            -not $job.Contains('persist-credentials: false') -or
            -not ($job.Contains('&promotion-version-tag') -or $job.Contains('*promotion-version-tag'))) { throw "Source job $name is not SHA-bound." }
    }
    foreach ($text in @('name: stable-release', 'artifact-ids: ${{ needs.release.outputs.promotion_artifact_id }}',
        "if: inputs.promotion_alpha == ''", 'disableNormalization: ${{ inputs.promotion_alpha != '''' }}',
        'group: openclaw-windows-node-release', 'candidate-source\installer.iss',
        'GitVersion_NoNormalizeEnabled: ${{ inputs.promotion_alpha != '''' && ''true'' || ''false'' }}',
        'GitVersion_NoFetchEnabled: ${{ inputs.promotion_alpha != '''' && ''true'' || ''false'' }}')) {
        if (-not $workflow.Contains($text)) { throw "Missing promotion contract: $text" }
    }
    foreach ($name in @('prepare-release-assets', 'release')) {
        $job = [regex]::Match($workflow, "(?ms)^  ${name}:\r?`n.*?(?=^  [a-z][a-z-]+:|\z)").Value
        if (-not $job.Contains("(startsWith(github.ref, 'refs/tags/v') || inputs.promotion_alpha != '')")) {
            throw "Release job $name must accept main-based promotions."
        }
        if ($name -eq 'release' -and -not $job.Contains('promotion_artifact_id: ${{ steps.promotion_artifact.outputs.artifact-id }}')) {
            throw 'Promotion publication must consume the artifact ID from the release staging job.'
        }
    }
    if ($entry.Contains('New-AlphaPromotionTag') -or $entry.Contains('Reserve-AlphaPromotion') -or
        $workflow.Contains('-RequireReservation')) { throw 'Preparation must not require or create a public stable tag.' }
    $reservation = [regex]::Match($workflow, '(?ms)^  reserve-msix-version:.*?(?=^  core-tests:)').Value
    foreach ($text in @('Get-AlphaPromotion', '-RequireApproval', '$record.version -cne $env:SOURCE_VERSION',
        '$promotionArguments.PromotionAlphaTag = $record.alphaTag', 'Resolve-MsixPackageVersion.ps1 @promotionArguments')) {
        if (-not $reservation.Contains($text)) { throw "MSIX promotion reservation must retain policy validation: $text" }
    }
    $publication = [regex]::Match($workflow, '(?ms)^  publish-promotion:.*').Value
    if (-not $publication.Contains('name: stable-release') -or
        -not $publication.Contains('Publish-AlphaPromotion') -or
        -not $publication.Contains('STABLE_RELEASE_TOKEN: ${{ secrets.STABLE_RELEASE_TOKEN }}')) { throw 'Publication must remain approval-gated.' }
    $expectedAssets = @('OpenClawCompanion-Setup-x64.exe', 'OpenClawCompanion-Setup-arm64.exe',
        'OpenClawTray-2026.9.5-win-x64.zip', 'OpenClawTray-2026.9.5-win-arm64.zip',
        'OpenClaw-Dev-x64.zip', 'OpenClaw-Dev-arm64.zip') | Sort-Object
    if (@(Compare-Object $expectedAssets @(Get-PromotionAssetNames '2026.9.5' | Sort-Object)).Count -ne 0) {
        throw 'Promotions must preserve the canonical public asset set. Store packages stay workflow-only.'
    }
    foreach ($text in @('default: false', '$env:GITHUB_REF -cne ''refs/heads/main''', 'uses: ./.github/workflows/ci.yml',
        'GH_TOKEN: ${{ github.token }}', 'if: inputs.prepare', 'persist-credentials: false')) {
        if (-not $entry.Contains($text)) { throw "Missing entry workflow contract: $text" }
    }
    Write-Host "Passed $cases alpha promotion cases and workflow source/publication contracts."
} finally {
    $env:STABLE_RELEASE_TOKEN = $previousPublicationToken
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

# Expected gh failures must not become the Actions wrapper's exit status.
$global:LASTEXITCODE = 0
