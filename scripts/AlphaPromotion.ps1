# Release control runs from the workflow revision, never from the candidate checkout.
Set-StrictMode -Version Latest

function Invoke-PromotionApi {
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateSet('GET', 'POST', 'PATCH')][string]$Method = 'GET',
        [object]$Body,
        [string]$Token = $env:GH_TOKEN,
        [switch]$AllowNotFound
    )

    $arguments = @{
        Uri = "https://api.github.com/repos/openclaw/openclaw-windows-node/$Path"
        Method = $Method
        Headers = @{
            Accept = 'application/vnd.github+json'
            Authorization = [string]::Concat('Bearer ', $Token)
            'X-GitHub-Api-Version' = '2022-11-28'
        }
        UserAgent = 'OpenClaw-AlphaPromotion'
        MaximumRedirection = 0
        TimeoutSec = 60
        ErrorAction = 'Stop'
    }
    if ($Method -ne 'GET') {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }
    try { return Invoke-RestMethod @arguments }
    catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        if ($AllowNotFound -and $status -eq 404) { return $null }
        throw "Promotion API $Method failed (HTTP $status)."
    }
}

function Get-PromotionTag {
    param([string]$Tag, [switch]$AllowNotFound)

    $ref = Invoke-PromotionApi -Path "git/ref/tags/$([Uri]::EscapeDataString($Tag))" -AllowNotFound:$AllowNotFound
    if ($null -eq $ref) { return $null }
    if ($ref.ref -cne "refs/tags/$Tag") { throw 'Tag lookup returned a different ref.' }
    $target = $ref.object
    $annotation = $null
    for ($depth = 0; $depth -le 8; $depth++) {
        if ($target.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Invalid tag object SHA.' }
        if ($target.type -ceq 'commit') {
            return [pscustomobject]@{ sha = $target.sha; annotation = $annotation }
        }
        if ($target.type -cne 'tag') { throw 'Release tags must resolve to commits.' }
        $object = Invoke-PromotionApi -Path "git/tags/$($target.sha)"
        if ($depth -eq 0) { $annotation = $object.message.Trim() }
        $target = $object.object
    }
    throw 'Release tag nesting exceeds the supported limit.'
}

function Assert-PromotionAncestry {
    param([string]$Base, [string]$Head)
    $comparison = Invoke-PromotionApi -Path "compare/$Base...$Head"
    if ($comparison.status -cnotin 'ahead', 'identical') {
        throw "Candidate ancestry check failed: $Base is not an ancestor of $Head."
    }
}

function Get-PromotionRelease {
    param([string]$Tag)

    # The by-tag endpoint is for published releases, not draft discovery.
    $found = @()
    for ($page = 1; $page -le 10; $page++) {
        $response = Invoke-PromotionApi -Path "releases?per_page=100&page=$page"
        $releases = @($response)
        $found += @($releases | Where-Object tag_name -CEQ $Tag)
        if ($found.Count -gt 1) { throw 'Multiple releases occupy the stable target. Resolve duplicate drafts before retrying.' }
        if ($releases.Count -lt 100) {
            if ($found.Count -eq 1) { return $found[0] }
            return $null
        }
    }
    throw 'Release discovery pagination exceeded its limit.'
}

function Assert-PromotionApproval {
    $environment = Invoke-PromotionApi -Path 'environments/stable-release'
    $review = @($environment.protection_rules | Where-Object type -CEQ 'required_reviewers')
    if ($review.Count -ne 1 -or @($review[0].reviewers).Count -eq 0 -or $environment.can_admins_bypass -ne $false) {
        throw 'Configure stable-release with required reviewers and no admin bypass before preparing a promotion.'
    }
    if ($review[0].prevent_self_review -ne $true) {
        $reviewers = @($review[0].reviewers)
        # GitHub enforces active membership in this required team. Its immutable
        # ID identifies openclaw/release-managers-openclaw, not another org team.
        if ($reviewers.Count -ne 1 -or $reviewers[0].type -cne 'Team' -or
            $reviewers[0].reviewer.id -ne 16590423) {
            throw 'Self-review requires the OpenClaw release managers team as the only required reviewer.'
        }
    }
    if (-not $environment.deployment_branch_policy -or
        $environment.deployment_branch_policy.custom_branch_policies -ne $true) {
        throw 'Restrict stable-release deployments to the main branch.'
    }
    $policies = Invoke-PromotionApi -Path 'environments/stable-release/deployment-branch-policies'
    $branches = @($policies.branch_policies)
    if ($policies.total_count -ne 1 -or $branches.Count -ne 1 -or
        $branches[0].name -cne 'main' -or $branches[0].type -cne 'branch') {
        throw 'The only stable-release deployment policy must be the exact main branch.'
    }
}

function Get-PromotionCiRun {
    param([string]$AlphaTag, [string]$SourceSha)

    $workflow = Invoke-PromotionApi -Path 'actions/workflows/ci.yml'
    for ($page = 1; $page -le 10; $page++) {
        $response = Invoke-PromotionApi -Path "actions/workflows/ci.yml/runs?head_sha=$SourceSha&per_page=100&page=$page"
        foreach ($run in $response.workflow_runs) {
            if ($run.head_sha -cne $SourceSha -or $run.head_branch -cne $AlphaTag -or
                $run.workflow_id -ne $workflow.id -or $run.event -cnotin 'push', 'workflow_dispatch' -or
                $run.status -cne 'completed' -or $run.conclusion -cne 'success') { continue }
            $jobs = @()
            for ($jobPage = 1; $jobPage -le 10; $jobPage++) {
                $jobResponse = Invoke-PromotionApi -Path "actions/runs/$($run.id)/attempts/$($run.run_attempt)/jobs?per_page=100&page=$jobPage"
                $jobs += @($jobResponse.jobs)
                if (@($jobResponse.jobs).Count -lt 100) { break }
                if ($jobPage -eq 10) { throw 'Candidate CI job pagination exceeded its limit.' }
            }
            foreach ($required in @('CI Gate', 'release', 'Core and CLI tests', 'Tray, setup, and integration tests',
                'UI, functional, and accessibility tests', 'Setup and connect E2E', 'Revocation recovery E2E',
                'Network recovery E2E', 'x64 release publish smoke', 'ARM64 release publish',
                'MSIX artifacts (x64)', 'MSIX artifacts (arm64)', 'Multi-architecture Store MSIX bundle')) {
                $matching = @($jobs | Where-Object name -CEQ $required)
                if ($matching.Count -ne 1 -or $matching[0].conclusion -cne 'success') {
                    throw "Candidate run $($run.id) lacks successful required job '$required'."
                }
            }
            return $run
        }
        if (@($response.workflow_runs).Count -lt 100) { break }
    }
    throw 'No successful full canonical alpha release run was found for the exact candidate SHA.'
}

function Get-AlphaPromotion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AlphaTag,
        [Parameter(Mandatory)][string]$PipelineSha,
        [string]$ExpectedSourceSha,
        [switch]$RequireApproval,
        [switch]$RequireReservation
    )

    $match = [regex]::Match($AlphaTag, '\Av(?<base>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))-alpha\.(?:0|[1-9]\d*)\z')
    if (-not $match.Success) { throw 'Select a canonical vX.Y.Z-alpha.N release.' }
    if ($PipelineSha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'PipelineSha must be an immutable lowercase commit SHA.' }
    if ($ExpectedSourceSha -and $ExpectedSourceSha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Invalid expected source SHA.' }
    $version = $match.Groups['base'].Value
    $stableTag = "v$version"
    $release = Invoke-PromotionApi -Path "releases/tags/$AlphaTag"
    if ($release.tag_name -cne $AlphaTag -or $release.draft -ne $false -or
        $release.prerelease -ne $true -or -not $release.published_at) {
        throw 'The candidate must be an existing published alpha prerelease.'
    }
    $source = Get-PromotionTag $AlphaTag
    if ($ExpectedSourceSha -and $source.sha -cne $ExpectedSourceSha) { throw 'The candidate tag moved after selection.' }
    $latest = Invoke-PromotionApi -Path 'releases/latest'
    if ($latest.draft -ne $false -or $latest.prerelease -ne $false -or -not $latest.published_at -or
        $latest.tag_name -cnotmatch '\Av(?<base>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:-[1-9]\d*)?\z') {
        throw 'Latest must be a published canonical stable release.'
    }
    if ([version]$version -le [version]$Matches.base) { throw 'Promotion must advance the latest stable base version.' }
    $previous = Get-PromotionTag $latest.tag_name
    $main = Invoke-PromotionApi -Path 'git/ref/heads/main'
    Assert-PromotionAncestry $PipelineSha $main.object.sha
    Assert-PromotionAncestry $source.sha $PipelineSha
    Assert-PromotionAncestry $previous.sha $source.sha
    $run = Get-PromotionCiRun $AlphaTag $source.sha
    $record = [pscustomobject][ordered]@{
        schemaVersion = 1
        kind = 'alpha-to-stable'
        alphaTag = $AlphaTag
        sourceSha = $source.sha
        stableTag = $stableTag
        version = $version
        pipelineSha = $PipelineSha
        previousStableTag = $latest.tag_name
        previousStableSha = $previous.sha
        alphaRunId = [string]$run.id
        alphaRunAttempt = [string]$run.run_attempt
    }
    $existing = Get-PromotionTag $stableTag -AllowNotFound
    if ($existing) {
        # Only our immutable annotation authorizes reuse of an unpublished tag.
        if ($existing.sha -cne $source.sha -or
            $existing.annotation -cne ($record | ConvertTo-Json -Compress)) {
            throw 'The stable tag already exists with different promotion provenance. Never move it.'
        }
    } elseif ($RequireReservation) { throw 'The promotion tag has not been reserved.' }
    $stable = Get-PromotionRelease -Tag $stableTag
    if ($stable -and ($stable.draft -ne $true -or $stable.published_at)) {
        throw 'The stable target is already published. Never replace a published release.'
    }
    if ($stable -and (-not $existing -or $stable.body -cnotmatch '<!-- alpha-promotion:[0-9a-f]{64} -->')) {
        throw 'An unrelated draft already occupies the stable target.'
    }
    if ($RequireApproval) { Assert-PromotionApproval }
    return $record
}

function New-AlphaPromotionTag {
    param([Parameter(Mandatory)][object]$Record)

    $fresh = Get-AlphaPromotion -AlphaTag $Record.alphaTag -PipelineSha $Record.pipelineSha `
        -ExpectedSourceSha $Record.sourceSha -RequireApproval
    if (($fresh | ConvertTo-Json -Compress) -cne ($Record | ConvertTo-Json -Compress)) {
        throw 'Promotion eligibility changed after preview.'
    }
    if (-not (Get-PromotionTag $Record.stableTag -AllowNotFound)) {
        $tag = Invoke-PromotionApi -Path 'git/tags' -Method POST -Body @{
            tag = $Record.stableTag
            message = ($Record | ConvertTo-Json -Compress)
            object = $Record.sourceSha
            type = 'commit'
        }
        # Use GITHUB_TOKEN, not a PAT: ref creation must not launch the old tag workflow.
        Invoke-PromotionApi -Path 'git/refs' -Method POST -Body @{
            ref = "refs/tags/$($Record.stableTag)"
            sha = $tag.sha
        } | Out-Null
    }
    Get-AlphaPromotion -AlphaTag $Record.alphaTag -PipelineSha $Record.pipelineSha `
        -ExpectedSourceSha $Record.sourceSha -RequireApproval -RequireReservation
}

function Get-PromotionAssetNames {
    param([string]$Version)
    @('OpenClawCompanion-Setup-x64.exe', 'OpenClawCompanion-Setup-arm64.exe',
        "OpenClawTray-$Version-win-x64.zip", "OpenClawTray-$Version-win-arm64.zip",
        'OpenClaw-Dev-x64.zip', 'OpenClaw-Dev-arm64.zip')
}

function New-PromotionArtifactManifest {
    param([object]$Record, [string]$Directory, [object]$VersionInfo, [string]$RunId, [string]$RunAttempt)

    . (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
    $allocation = Assert-MsixVersionInfo $VersionInfo -SourceCommit $Record.sourceSha -SourceVersion $Record.version -RequireReserved
    $files = foreach ($name in (Get-PromotionAssetNames $Record.version)) {
        $file = Get-Item -LiteralPath (Join-Path $Directory $name) -ErrorAction Stop
        if ($file.PSIsContainer -or $file.LinkType) { throw 'Promotion assets must be regular files.' }
        [pscustomobject]@{ name = $name; size = $file.Length; sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [pscustomobject][ordered]@{
        promotion = $Record
        runId = $RunId
        runAttempt = $RunAttempt
        msixVersionAllocation = $allocation
        files = @($files)
    }
}

function Assert-PromotionArtifactManifest {
    param([object]$Manifest, [object]$Record, [string]$Directory, [string]$RunId, [string]$RunAttempt)

    $actual = New-PromotionArtifactManifest -Record $Record -Directory $Directory `
        -VersionInfo $Manifest.msixVersionAllocation -RunId $RunId -RunAttempt $RunAttempt
    if (($actual | ConvertTo-Json -Depth 10 -Compress) -cne ($Manifest | ConvertTo-Json -Depth 10 -Compress)) {
        throw 'Promotion artifacts, source, allocation, or preparation run do not match the approved manifest.'
    }
    $expected = @((Get-PromotionAssetNames $Record.version); 'promotion.json') | Sort-Object
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    if (@($entries | Where-Object { $_.PSIsContainer -or $_.LinkType }).Count -gt 0 -or
        @(Compare-Object $expected @($entries.Name | Sort-Object)).Count -gt 0) {
        throw 'Unexpected files in the promotion artifact.'
    }
}

function Get-PromotionWorkflowFiles {
    param([string]$Commit)

    $tree = Invoke-PromotionApi -Path "git/trees/${Commit}?recursive=1"
    if ($tree.truncated) { throw 'Workflow permission check requires a complete Git tree.' }
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $tree.tree) {
        if ($entry.type -cne 'tree' -and $entry.path.StartsWith('.github/workflows/', [StringComparison]::Ordinal)) {
            $files.Add($entry.path, "$($entry.mode):$($entry.type):$($entry.sha)")
        }
    }
    return $files
}

function Resolve-PromotionReleaseToken {
    param([object]$Record, [string]$ReleaseToken)

    $main = Invoke-PromotionApi -Path 'git/ref/heads/main'
    if ($Record.sourceSha -cne $main.object.sha) {
        $sourceFiles = Get-PromotionWorkflowFiles $Record.sourceSha
        $mainFiles = Get-PromotionWorkflowFiles $main.object.sha
        foreach ($file in $sourceFiles.GetEnumerator()) {
            # GitHub requires workflow-write authority for additions/modifications,
            # not for older product code or workflow deletions alone.
            if (-not $mainFiles.ContainsKey($file.Key) -or $mainFiles[$file.Key] -cne $file.Value) {
                if ([string]::IsNullOrWhiteSpace($ReleaseToken) -or $ReleaseToken -match '\p{Cc}' -or
                    $ReleaseToken -ceq $env:GH_TOKEN) {
                    throw 'Candidate workflow files differ from main. Configure a distinct STABLE_RELEASE_TOKEN with Contents and Workflows write permission for this repository.'
                }
                return $ReleaseToken
            }
        }
    }
    return $env:GH_TOKEN
}

function Publish-AlphaPromotion {
    param([object]$Record, [string]$Directory, [string]$RunId, [string]$PreparedAttempt,
        [string]$ReleaseToken = $env:STABLE_RELEASE_TOKEN)

    $fresh = Get-AlphaPromotion -AlphaTag $Record.alphaTag -PipelineSha $Record.pipelineSha `
        -ExpectedSourceSha $Record.sourceSha -RequireApproval
    if (($fresh | ConvertTo-Json -Compress) -cne ($Record | ConvertTo-Json -Compress)) { throw 'Promotion provenance changed.' }
    # Ordinary same-repository releases use GITHUB_TOKEN. Resolve the historical
    # workflow exception before any public tag or release mutation.
    $ReleaseToken = Resolve-PromotionReleaseToken $Record $ReleaseToken
    $manifestPath = Join-Path $Directory 'promotion.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-PromotionArtifactManifest $manifest $Record $Directory $RunId $PreparedAttempt
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $marker = "<!-- alpha-promotion:$manifestHash -->"
    # This function runs only after protected approval. Preparation uses local refs.
    New-AlphaPromotionTag -Record $Record | Out-Null
    $tag = $Record.stableTag
    $release = Get-PromotionRelease -Tag $tag
    if (-not $release) {
        $notes = Invoke-PromotionApi -Path 'releases/generate-notes' -Method POST -Body @{
            tag_name = $tag
            target_commitish = $Record.sourceSha
            previous_tag_name = $Record.previousStableTag
        }
        $release = Invoke-PromotionApi -Path 'releases' -Method POST -Token $ReleaseToken -Body @{
            tag_name = $tag
            target_commitish = $Record.sourceSha
            name = $tag
            draft = $true
            prerelease = $false
            make_latest = 'false'
            body = @"
Promoted from $($Record.alphaTag) at $($Record.sourceSha).

$($notes.body)

### Signed Dev MSIX packages

OpenClaw-Dev-x64.zip and OpenClaw-Dev-arm64.zip contain development-signed
packages, matching public certificates, provenance, and installation instructions.
These are not Microsoft Store-signed packages. Unsigned Store submission packages
remain [workflow artifacts](https://github.com/openclaw/openclaw-windows-node/actions/runs/$RunId).

$marker
"@
        }
    }
    if ($release.tag_name -cne $tag -or $release.draft -ne $true -or
        $release.published_at -or -not $release.body.Contains($marker)) {
        throw 'Existing release is not the draft for these exact prepared artifacts. Do not overwrite it.'
    }
    $files = @($manifest.files) + [pscustomobject]@{
        name = 'promotion.json'; sha256 = $manifestHash; size = (Get-Item -LiteralPath $manifestPath).Length
    }
    foreach ($file in $files) {
        $assets = @($release.assets | Where-Object name -CEQ $file.name)
        if ($assets.Count -eq 0) {
            & gh release upload $tag (Join-Path $Directory $file.name) --repo openclaw/openclaw-windows-node
            if ($LASTEXITCODE -ne 0) { throw "Could not upload $($file.name). Resume with the same prepared artifact." }
        } elseif ($assets.Count -ne 1 -or $assets[0].digest -cne "sha256:$($file.sha256)") {
            throw 'A draft asset has different bytes. Refusing to overwrite it.'
        }
    }
    $release = Invoke-PromotionApi -Path "releases/$($release.id)"
    if ($release.tag_name -cne $tag -or $release.draft -ne $true -or
        $release.published_at -or -not $release.body.Contains($marker) -or
        @($release.assets).Count -ne $files.Count) {
        throw 'Draft release state or asset set changed during upload.'
    }
    foreach ($file in $files) {
        $assets = @($release.assets | Where-Object name -CEQ $file.name)
        if ($assets.Count -ne 1 -or $assets[0].state -cne 'uploaded' -or
            $assets[0].size -ne $file.size -or $assets[0].digest -cne "sha256:$($file.sha256)") {
            throw 'Uploaded release assets do not match the approved manifest.'
        }
    }
    Get-AlphaPromotion -AlphaTag $Record.alphaTag -PipelineSha $Record.pipelineSha `
        -ExpectedSourceSha $Record.sourceSha -RequireApproval -RequireReservation | Out-Null
    try {
        Invoke-PromotionApi -Path "releases/$($release.id)" -Method PATCH -Token $ReleaseToken -Body @{
            draft = $false
            prerelease = $false
            make_latest = 'true'
        } | Out-Null
    } catch { throw 'Stable publication failed. Inspect release state before retrying.' }
    $published = Invoke-PromotionApi -Path "releases/tags/$tag"
    $latest = Invoke-PromotionApi -Path 'releases/latest'
    if ($published.draft -ne $false -or $published.prerelease -ne $false -or
        -not $published.published_at -or $latest.tag_name -cne $tag) {
        throw 'Post-publication verification failed.'
    }
}
