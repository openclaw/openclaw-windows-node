Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CompanionStoreRequestRunId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Ref,
        [Parameter(Mandatory)][string]$EventName,
        [string]$RunId,
        [string]$EventPath
    )

    $canonicalRepository = 'openclaw/openclaw-windows-node'
    if ($Repository -cne $canonicalRepository -or $Ref -cne 'refs/heads/main') {
        throw 'Request Store publication from main in openclaw/openclaw-windows-node.'
    }
    if ($EventName -ceq 'workflow_dispatch') {
        if ($RunId -cnotmatch '\A[1-9][0-9]*\z') {
            throw 'The source run ID must be a canonical positive integer.'
        }
        return $RunId
    }
    if ($EventName -cne 'workflow_run') {
        throw 'Store publication requests require workflow_dispatch or workflow_run.'
    }

    $run = (Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json).workflow_run
    if ($run.status -cne 'completed' -or $run.conclusion -cne 'success' -or
        $run.repository.full_name -cne $canonicalRepository -or
        $run.head_repository.full_name -cne $canonicalRepository) {
        return $null
    }

    # This is only a cheap event filter. The central publisher owns provenance,
    # artifact authorization, and release-manager approval.
    $tagRun = $run.path -ceq '.github/workflows/ci.yml' -and
        $run.event -cin @('push', 'workflow_dispatch') -and
        $run.head_branch -cmatch '\Av[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9]+)?\z'
    $promotionRun = $run.path -ceq '.github/workflows/promote-alpha-release.yml' -and
        $run.event -ceq 'workflow_dispatch' -and $run.head_branch -ceq 'main'
    if (-not ($tagRun -or $promotionRun)) { return $null }

    $sourceRunId = [string]$run.id
    if ($sourceRunId -cnotmatch '\A[1-9][0-9]*\z') {
        throw 'The source run ID must be a canonical positive integer.'
    }
    if ($promotionRun) {
        # A successful promotion preview has no prepared release. Its completed
        # event alone must not mint a cross-repository publication request.
        $attempt = [string]$run.run_attempt
        if ($attempt -cnotmatch '\A[1-9][0-9]*\z') {
            throw 'The source run attempt must be a canonical positive integer.'
        }
        $json = & gh api --paginate --slurp "repos/$canonicalRepository/actions/runs/$sourceRunId/artifacts?per_page=100"
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read promotion release evidence.' }
        $witnesses = @($json | ConvertFrom-Json | ForEach-Object { $_.artifacts } |
            Where-Object { $_.name -ceq "store-release-source-$attempt" -and -not $_.expired })
        if ($witnesses.Count -ne 1) { return $null }
    }
    return $sourceRunId
}

function Request-CompanionStorePublication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A[1-9][0-9]*\z')]
        [string]$RunId
    )

    & gh workflow run windows-store-publish.yml --repo openclaw/releases --ref main `
        -f product=companion -f "run_id=$RunId" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The central publication request was not confirmed. Inspect openclaw/releases Actions before retrying.'
    }
    Write-Host "Central Store publication requested for Companion source run $RunId. Release-manager approval and Store acceptance are separate."
}
