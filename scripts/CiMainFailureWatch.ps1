<#
.SYNOPSIS
Finds the tests that failed in a main-branch CI run and the commits that last changed them.
.DESCRIPTION
Helpers for scripts\Watch-CiMainFailures.ps1, kept free of GitHub calls so
scripts\test-ci-main-failure-watch.ps1 can test them. Test names and other run data are untrusted:
they only reach markdown through ConvertTo-CiMarkdownCode and never reach a command line.

- Get-CiFailedTests reads every TRX file below a folder and returns each failed test method once,
  with the artifact it came from.
- Get-CiTestIndex maps every xUnit test in tests\ to its file and line span (scripts\CiChangedTests.ps1).
- Get-CiLastChange returns the most recent commit that changed a test method's lines.
- New-CiMainFailureComment builds the tracking-issue comment, with a marker that makes each run
  attempt report once.
- Publish-CiMainFailureReport finds or creates the tracking issue and posts the comment. Its -Gh
  script block runs the GitHub CLI, so tests can record the calls instead.

Example:
    . .\scripts\CiMainFailureWatch.ps1
    $failed = Get-CiFailedTests -Directory $artifacts
    $index = Get-CiTestIndex -Root (Get-Location).Path
    $commit = Get-CiLastChange -Root . -Location $index[$failed[0].FullyQualifiedName] -Head HEAD
#>

. (Join-Path $PSScriptRoot 'CiChangedTests.ps1')

# The issue intro (about 300 characters) is added to the first report, so keep a margin below 65,536.
$script:CiMainFailureMaxCommentLength = 60000
$script:CiMainFailureTitle = 'Tests failing on main (tracking)'

function Get-CiFailedTests {
    param([Parameter(Mandatory)][string]$Directory)
    $failed = [ordered]@{}
    $root = [IO.Path]::GetFullPath($Directory)
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -Filter *.trx -File -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        try { [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw } catch { continue }
        $namespace = [Xml.XmlNamespaceManager]::new($trx.NameTable)
        $namespace.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
        $definitions = @{}
        foreach ($test in $trx.SelectNodes('//t:UnitTest', $namespace)) {
            $method = $test.SelectSingleNode('t:TestMethod', $namespace)
            if ($method) { $definitions[$test.id] = $method }
        }
        $artifact = ($file.FullName.Substring($root.Length).TrimStart('\', '/') -split '[\\/]')[0]
        foreach ($result in $trx.SelectNodes("//t:UnitTestResult[@outcome='Failed']", $namespace)) {
            $method = $definitions[$result.testId]
            $name = if ($method) { (($method.className -split ',')[0].Trim() + '.' + $method.name) } else { ($result.testName -replace '\(.*$', '') }
            if (-not $failed.Contains($name)) {
                $failed[$name] = [pscustomobject]@{ FullyQualifiedName = $name; DisplayName = [string]$result.testName; Artifact = $artifact }
            }
        }
    }
    return , @($failed.Values)
}

function Get-CiTestIndex {
    param([Parameter(Mandatory)][string]$Root)
    $index = @{}
    $testsRoot = Join-Path $Root 'tests'
    foreach ($file in @(Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue)) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj)/') { continue }
        foreach ($method in (Get-CiTestMethods -Source ([IO.File]::ReadAllText($file.FullName)))) {
            # A name defined twice (for example partial classes) keeps its first location.
            if (-not $index.ContainsKey($method.FullyQualifiedName)) {
                $index[$method.FullyQualifiedName] = [pscustomobject]@{ Path = $relative; StartLine = $method.StartLine; EndLine = $method.EndLine }
            }
        }
    }
    return $index
}

function Get-CiLastChange {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)]$Location, [Parameter(Mandatory)][string]$Head)
    $range = '{0},{1}:{2}' -f $Location.StartLine, $Location.EndLine, $Location.Path
    $commit = (& git -C $Root log -1 --format=%H --no-patch -L $range $Head 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { return $null }
    return $commit
}

# Untrusted text becomes one inline code span: no mentions, links, HTML or table breaks.
function ConvertTo-CiMarkdownCode {
    param([AllowEmptyString()][string]$Text, [int]$MaxLength = 200)
    $clean = ([regex]::Replace([string]$Text, '\p{C}', ' ')).Replace('`', "'").Trim()
    if ($clean.Length -gt $MaxLength) { $clean = $clean.Substring(0, $MaxLength) + '...' }
    if (-not $clean) { $clean = '(empty)' }
    return '`' + $clean.Replace('|', '\|') + '`'
}

function Test-CiGitHubLogin {
    param([AllowEmptyString()][string]$Login)
    return [bool]($Login -cmatch '^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$')
}

function Get-CiMainFailureMarker {
    param([Parameter(Mandatory)][long]$RunId, [Parameter(Mandatory)][int]$RunAttempt)
    return "<!-- openclaw-main-failure-watch run=$RunId attempt=$RunAttempt -->"
}

function New-CiMainFailureComment {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][long]$RunId,
        [Parameter(Mandatory)][int]$RunAttempt,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$HeadSha,
        [AllowEmptyCollection()][string[]]$FailedJobs = @(),
        # Objects with FullyQualifiedName, Artifact, Commit, PullRequest and Author (any may be empty).
        [AllowEmptyCollection()][object[]]$Tests = @()
    )
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add((Get-CiMainFailureMarker -RunId $RunId -RunAttempt $RunAttempt))
    $lines.Add(("### [Run {0}, attempt {1}](https://github.com/{2}/actions/runs/{0}/attempts/{1}) failed on {3}" -f $RunId, $RunAttempt, $Repository, $HeadSha.Substring(0, 12)))
    $lines.Add('')
    if ($FailedJobs.Count) {
        $lines.Add('Failed jobs: ' + (($FailedJobs | ForEach-Object { ConvertTo-CiMarkdownCode $_ 100 }) -join ', '))
        $lines.Add('')
    }
    if (-not $Tests.Count) {
        $lines.Add('No failed test results were found in this attempt''s test artifacts. The failure is outside the reported tests (for example a build, setup or script step, or a test host that stopped before writing results); see the run.')
        return ($lines -join "`n")
    }
    # Every author is mentioned, including those of tests beyond the table's limit.
    $authors = @($Tests | ForEach-Object { $_.Author } | Where-Object { Test-CiGitHubLogin $_ } | Sort-Object -Unique)
    if ($authors.Count) {
        $lines.Add('Please check: ' + (($authors | ForEach-Object { "@$_" }) -join ' '))
        $lines.Add('')
    }
    $lines.Add('| Failed test | Artifact | Last changed in | Pull request | Author |')
    $lines.Add('|---|---|---|---|---|')
    # Rows are added while the comment stays under GitHub's comment size limit (65,536 characters).
    $length = ($lines -join "`n").Length + 1200
    $listed = 0
    foreach ($test in $Tests) {
        $commit = if ($test.Commit -match '^[0-9a-f]{40}$') { "[$($test.Commit.Substring(0, 12))](https://github.com/$Repository/commit/$($test.Commit))" } else { 'not found' }
        $pull = if ($test.PullRequest -is [int] -or $test.PullRequest -is [long]) { "#$($test.PullRequest)" } else { '-' }
        $author = if (Test-CiGitHubLogin $test.Author) { "@$($test.Author)" } else { '-' }
        $row = '| {0} | {1} | {2} | {3} | {4} |' -f (ConvertTo-CiMarkdownCode $test.FullyQualifiedName), (ConvertTo-CiMarkdownCode $test.Artifact 80), $commit, $pull, $author
        if ($length + $row.Length + 1 -gt $script:CiMainFailureMaxCommentLength) { break }
        $lines.Add($row)
        $length += $row.Length + 1
        $listed++
    }
    if ($Tests.Count -gt $listed) {
        $lines.Add('')
        $lines.Add(('{0} more failed test(s) did not fit in this comment; see the run.' -f ($Tests.Count - $listed)))
    }
    $lines.Add('')
    $lines.Add('"Last changed in" is the most recent commit that changed the test method. It is often the cause of a new failure, but it can be a later refactor, and an older test can fail because of a product change. Authors: please check whether your change made this test fail or flaky, then fix forward or propose a revert. This watch never reruns or reverts anything.')
    return ($lines -join "`n")
}

function Publish-CiMainFailureReport {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][string]$Comment,
        # Runs the GitHub CLI with the given arguments and returns its standard output.
        [Parameter(Mandatory)][scriptblock]$Gh,
        [Parameter(Mandatory)][string]$WorkDirectory,
        # Only this account's issues and comments count, so nobody else can post a marker to hide a report.
        [string]$BotLogin = 'github-actions[bot]'
    )
    $encodedLabel = [Uri]::EscapeDataString($Label)
    $botFilter = 'select(.user.login == "' + $BotLogin + '")'
    $listIssues = {
        @(& $Gh api "repos/$Repository/issues?state=open&labels=$encodedLabel&per_page=100" --jq ".[] | select(.pull_request == null) | $botFilter | .number" |
                Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Sort-Object)
    }
    $labels = @(& $Gh api "repos/$Repository/labels?per_page=100" --paginate --jq '.[].name')
    if ($Label -cnotin $labels) {
        # A concurrent watch may create it first; only a label that is still missing is an error.
        try {
            & $Gh api "repos/$Repository/labels" -f "name=$Label" -f 'color=B60205' -f 'description=Tests failing on main, reported by the main failure watch' | Out-Null
        } catch {
            if ($Label -cnotin @(& $Gh api "repos/$Repository/labels?per_page=100" --paginate --jq '.[].name')) { throw }
        }
    }
    $issues = @(& $listIssues)
    $bodyPath = Join-Path $WorkDirectory 'main-failure-comment.md'
    $created = 0
    if (-not $issues.Count) {
        $intro = 'This issue collects test failures on `main`, one comment per failed CI run attempt. Each comment mentions the authors of the pull requests that last changed the failing tests. Close it when main is green again; the next failure opens a new one.'
        Set-Content -LiteralPath $bodyPath -Value ($intro + "`n`n" + $Comment) -Encoding utf8
        $url = [string](& $Gh issue create --repo $Repository --title $script:CiMainFailureTitle --label $Label --body-file $bodyPath | Select-Object -Last 1)
        if ($url -notmatch '/issues/(\d+)\s*$') { throw "Unexpected issue URL '$url'." }
        $created = [int]$Matches[1]
        $issues = @(@(& $listIssues) + $created | Sort-Object -Unique)
    }
    # Watches run in parallel, so two may each create an issue. Every publication keeps the oldest and
    # closes newer ones, first copying over any report that only the newer one holds.
    $issue = $issues[0]
    $readReports = {
        param([int]$Number)
        $texts = @((& $Gh api "repos/$Repository/issues/$Number" --jq "$botFilter | .body") -join "`n") +
            @(& $Gh api "repos/$Repository/issues/$Number/comments?per_page=100" --paginate --jq "[.[] | $botFilter | .body] | .[] | @json" | ForEach-Object { $_ | ConvertFrom-Json })
        # A report starts at its marker; an issue body holds the intro before its report.
        @($texts | ForEach-Object { $at = $_.IndexOf('<!-- openclaw-main-failure-watch run='); if ($at -ge 0) { $_.Substring($at) } })
    }
    $keptReports = $null
    $postedOwn = $false
    foreach ($duplicate in @($issues | Select-Object -Skip 1)) {
        if ($null -eq $keptReports) { $keptReports = (& $readReports $issue) -join "`n" }
        foreach ($report in (& $readReports $duplicate)) {
            $reportMarker = ($report -split "`n")[0]
            if (-not $keptReports.Contains($reportMarker)) {
                Set-Content -LiteralPath $bodyPath -Value $report -Encoding utf8
                & $Gh issue comment $issue --repo $Repository --body-file $bodyPath | Out-Null
                $keptReports += "`n$reportMarker"
                if ($reportMarker -eq $Marker) { $postedOwn = $true }
            }
        }
        & $Gh issue close $duplicate --repo $Repository --reason 'not planned' --comment "Duplicate of #$issue." | Out-Null
    }
    if ($created -eq $issue) {
        return [pscustomobject]@{ Action = 'created'; Target = "#$issue" }
    }
    if ($postedOwn) {
        return [pscustomobject]@{ Action = 'commented'; Target = "#$issue" }
    }
    $reported = if ($null -ne $keptReports) { $keptReports } else { (& $readReports $issue) -join "`n" }
    if ($reported.Contains($Marker)) {
        return [pscustomobject]@{ Action = 'already-reported'; Target = "#$issue" }
    }
    Set-Content -LiteralPath $bodyPath -Value $Comment -Encoding utf8
    & $Gh issue comment $issue --repo $Repository --body-file $bodyPath | Out-Null
    return [pscustomobject]@{ Action = 'commented'; Target = "#$issue" }
}

# Picks the test-result artifacts one run attempt uploaded: for each name, the newest one created
# between the attempt's start and the next attempt's start. Artifact names carry no attempt number.
# Artifacts an attempt reused from earlier attempts are skipped: they come from jobs that did not run
# again, and only jobs that ran in this attempt can have failed in it.
function Select-CiAttemptArtifacts {
    param(
        # Objects with Id, Name, CreatedAt ([datetime]) and Expired.
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Artifacts,
        [Parameter(Mandatory)][datetime]$After,
        [Nullable[datetime]]$Before
    )
    return , @($Artifacts |
            Where-Object { $_.Name -match '^(?:e2e-)?test-results-' -and -not $_.Expired -and $_.CreatedAt -ge $After -and ($null -eq $Before -or $_.CreatedAt -lt $Before) } |
            Group-Object Name | ForEach-Object { $_.Group | Sort-Object CreatedAt -Descending | Select-Object -First 1 } | Sort-Object Name)
}
