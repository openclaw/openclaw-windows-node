<#
.SYNOPSIS
Reports the tests that failed in a main-branch CI run to one tracking issue.
.DESCRIPTION
Run by .github\workflows\main-failure-watch.yml when "Build and Test" fails on a push to main. It
downloads the run's test-result artifacts, finds the failed test methods, attributes each to the
most recent commit that changed the method (git log -L at the failing commit) and to that commit's
pull request and author, then adds one comment per run attempt to the open issue labeled
-Label, creating the label and issue when missing. It never reruns or reverts anything.
Run data is untrusted: it is escaped for markdown and passed to the GitHub CLI only as separate
arguments or files. The checkout must have full history at -HeadSha.
.EXAMPLE
.\scripts\Watch-CiMainFailures.ps1 -Repository openclaw/openclaw-windows-node -RunId 43479032853 -RunAttempt 1 -HeadSha (git rev-parse HEAD) -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory)][ValidateRange(1, 1000)][int]$RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$HeadSha,
    [string]$Label = 'ci: main-flake',
    [string]$WorkDirectory = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'main-failure-watch' } else { Join-Path ([IO.Path]::GetTempPath()) 'main-failure-watch' }),
    # Prints the comment instead of publishing it.
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiMainFailureWatch.ps1')

$gh = {
    $output = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args[0]) failed with exit code $LASTEXITCODE." }
    $output
}
# A pull request lookup is retried briefly before the report falls back to the commit author.
$lookupAttempts = if ($env:OPENCLAW_MAIN_WATCH_LOOKUP_ATTEMPTS) { [int]$env:OPENCLAW_MAIN_WATCH_LOOKUP_ATTEMPTS } else { 3 }
$lookupRetrySeconds = if ($env:OPENCLAW_MAIN_WATCH_LOOKUP_RETRY_SECONDS) { [int]$env:OPENCLAW_MAIN_WATCH_LOOKUP_RETRY_SECONDS } else { 5 }
$root = (& git rev-parse --show-toplevel).Trim()
if ((& git -C $root rev-parse --is-shallow-repository).Trim() -eq 'true') { throw 'The checkout needs full history for attribution.' }
& git -C $root cat-file -e "$HeadSha^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Commit $HeadSha is not in this checkout." }
# Test line spans come from the working tree, so it must be the failing commit.
if ((& git -C $root rev-parse HEAD).Trim() -ne $HeadSha) { throw "Check out $HeadSha before running the watch." }

New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$artifacts = Join-Path $WorkDirectory "run-$RunId-$RunAttempt"
if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }

$failedJobs = @(& $gh api "repos/$Repository/actions/runs/$RunId/attempts/$RunAttempt/jobs?per_page=100" --paginate `
        --jq '.jobs[] | select(.conclusion == "failure" or .conclusion == "timed_out") | .name')

# Artifact names carry no attempt number, so this attempt's uploads are told apart by time.
$attemptTime = {
    param([int]$Attempt)
    [datetime]::Parse((& $gh api "repos/$Repository/actions/runs/$RunId/attempts/$Attempt" --jq '.run_started_at'),
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
}
$after = & $attemptTime $RunAttempt
$latestAttempt = [int](& $gh api "repos/$Repository/actions/runs/$RunId" --jq '.run_attempt')
$before = if ($latestAttempt -gt $RunAttempt) { & $attemptTime ($RunAttempt + 1) } else { $null }
$listed = @(& $gh api "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100" --paginate --jq '.artifacts[] | [.id, .name, .created_at, .expired] | @tsv' | ForEach-Object {
        $fields = $_ -split "`t"
        [pscustomobject]@{
            Id = [long]$fields[0]; Name = $fields[1]; Expired = $fields[3] -eq 'true'
            CreatedAt = [datetime]::Parse($fields[2], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        }
    })
$token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { (& gh auth token).Trim() }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
foreach ($artifact in (Select-CiAttemptArtifacts -Artifacts $listed -After $after -Before $before)) {
    if ($artifact.Name -notmatch '^[A-Za-z0-9._-]+$') { throw "Unexpected artifact name '$($artifact.Name)'." }
    $zip = Join-Path $WorkDirectory "$($artifact.Id).zip"
    # A failed download fails the watch rather than posting an incomplete report under the final marker.
    Invoke-WebRequest -Uri "https://api.github.com/repos/$Repository/actions/artifacts/$($artifact.Id)/zip" -OutFile $zip `
        -Headers @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' } -MaximumRetryCount 3 -RetryIntervalSec 5
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $artifacts $artifact.Name) -Force
    Remove-Item -LiteralPath $zip
}

$failed = Get-CiFailedTests -Directory $artifacts
$index = Get-CiTestIndex -Root $root
$pulls = @{}
$tests = foreach ($test in $failed) {
    $location = $index[$test.FullyQualifiedName]
    $commit = if ($location) { Get-CiLastChange -Root $root -Location $location -Head $HeadSha } else { $null }
    if ($commit -and -not $pulls.ContainsKey($commit)) {
        # Prefer the pull request merged into main; a commit can also belong to stacked or merged-in branches.
        # Without one (or when the lookup keeps failing), the commit's GitHub author is mentioned instead.
        $fields = @()
        $author = $null
        for ($try = 1; $try -le $lookupAttempts -and -not $fields.Count; $try++) {
            try {
                $pull = @(& $gh api "repos/$Repository/commits/$commit/pulls" --jq '[.[] | select(.merged_at != null)] | (map(select(.base.ref == "main")) + .)[0] | [.number, .user.login] | @tsv')
                $fields = if ($pull.Count -and $pull[0] -and $pull[0].Trim()) { @($pull[0] -split "`t") } else { @('', '') }
            } catch {
                if ($try -lt $lookupAttempts) { Start-Sleep -Seconds $lookupRetrySeconds }
            }
        }
        if (-not $fields.Count -or -not $fields[0]) {
            try { $author = [string](& $gh api "repos/$Repository/commits/$commit" --jq '.author.login // empty' | Select-Object -First 1) } catch { $author = $null }
            if (-not $fields.Count) { Write-Host "::warning title=Main failure watch::Could not look up the pull request for $commit; mentioning its commit author." }
        }
        $pulls[$commit] = [pscustomobject]@{
            Number = if ($fields.Count -ge 1 -and $fields[0] -match '^\d+$') { [int]$fields[0] } else { $null }
            Author = if ($fields.Count -ge 2 -and $fields[1]) { $fields[1] } else { $author }
        }
    }
    [pscustomobject]@{
        FullyQualifiedName = $test.FullyQualifiedName
        Artifact = $test.Artifact
        Commit = $commit
        PullRequest = if ($commit) { $pulls[$commit].Number } else { $null }
        Author = if ($commit) { $pulls[$commit].Author } else { $null }
    }
}
$comment = New-CiMainFailureComment -Repository $Repository -RunId $RunId -RunAttempt $RunAttempt -HeadSha $HeadSha -FailedJobs $failedJobs -Tests @($tests)
if ($DryRun) {
    Write-Output $comment
    return
}
$result = Publish-CiMainFailureReport -Repository $Repository -Label $Label -Marker (Get-CiMainFailureMarker -RunId $RunId -RunAttempt $RunAttempt) `
    -Comment $comment -Gh $gh -WorkDirectory $WorkDirectory
Write-Host ("Main failure watch: {0} {1} for {2} failed test(s)." -f $result.Action, $result.Target, @($tests).Count)
if ($env:GITHUB_STEP_SUMMARY) { $comment | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
