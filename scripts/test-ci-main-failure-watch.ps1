# Tests the main failure watch: failed-test discovery, attribution, escaping and publishing.
# Run: pwsh -NoProfile -File .\scripts\test-ci-main-failure-watch.ps1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiMainFailureWatch.ps1')

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (($Expected -join "`n") -cne ($Actual -join "`n")) {
        throw "$Message`nExpected: $($Expected -join ', ')`nActual:   $($Actual -join ', ')"
    }
}
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function New-Trx([object[]]$Results) {
    $definitions = ($Results | ForEach-Object { "<UnitTest id=`"$($_.Id)`" name=`"x`"><TestMethod className=`"$($_.Class)`" name=`"$($_.Method)`" /></UnitTest>" }) -join ''
    $outcomes = ($Results | ForEach-Object { "<UnitTestResult testId=`"$($_.Id)`" testName=`"$([Security.SecurityElement]::Escape($_.Display))`" outcome=`"$($_.Outcome)`" />" }) -join ''
    "<?xml version=`"1.0`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$outcomes</Results><TestDefinitions>$definitions</TestDefinitions></TestRun>"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('openclaw-main-watch-' + [Guid]::NewGuid().ToString('N'))
try {
    # Failed tests: one entry per method across theory cases and reports, with the artifact folder.
    New-Item -ItemType Directory -Force -Path (Join-Path $temp 'artifacts\test-results-ui\TrayUI\abc'), (Join-Path $temp 'artifacts\test-results-core\Shared') | Out-Null
    New-Trx @(
        @{ Id = '1'; Class = 'Ui.WindowTests'; Method = 'Opens'; Display = 'Ui.WindowTests.Opens(theme: "Dark")'; Outcome = 'Failed' },
        @{ Id = '2'; Class = 'Ui.WindowTests'; Method = 'Opens'; Display = 'Ui.WindowTests.Opens(theme: "Light")'; Outcome = 'Failed' },
        @{ Id = '3'; Class = 'Ui.WindowTests'; Method = 'Closes'; Display = 'Ui.WindowTests.Closes'; Outcome = 'Passed' }
    ) | Set-Content -LiteralPath (Join-Path $temp 'artifacts\test-results-ui\TrayUI\abc\ui.trx')
    New-Trx @(
        @{ Id = '9'; Class = 'Shared.Outer+Inner, Shared.Tests'; Method = 'Works'; Display = 'Shared.Outer+Inner.Works'; Outcome = 'Failed' }
    ) | Set-Content -LiteralPath (Join-Path $temp 'artifacts\test-results-core\Shared\core.trx')
    'not xml' | Set-Content -LiteralPath (Join-Path $temp 'artifacts\test-results-core\broken.trx')
    $failed = Get-CiFailedTests -Directory (Join-Path $temp 'artifacts')
    Assert-Equal @('Shared.Outer+Inner.Works@test-results-core', 'Ui.WindowTests.Opens@test-results-ui') @($failed | ForEach-Object { "$($_.FullyQualifiedName)@$($_.Artifact)" }) `
        'Failed tests are listed once per method with their artifact; passed tests and unreadable reports are ignored.'
    Assert-Equal @() @((Get-CiFailedTests -Directory (Join-Path $temp 'missing'))) 'A run without artifacts has no failed tests.'

    # Untrusted text stays inside one code span.
    Assert-Equal '`a''b\|c @x <i> d`' (ConvertTo-CiMarkdownCode "a``b|c`n@x <i>`td") 'Markdown escaping must neutralize code fences, tables, line breaks and mentions.'
    Assert-Equal '`abc...`' (ConvertTo-CiMarkdownCode 'abcdef' 3) 'Long text is truncated.'
    Assert-Equal '`(empty)`' (ConvertTo-CiMarkdownCode '') 'Empty text is labeled.'
    Assert-Equal @($true, $true, $false, $false, $false, $false, $false) `
        @('karkarl', 'a-b1', '-a', 'a--b', 'dependabot[bot]', 'x y', ('a' * 40) | ForEach-Object { Test-CiGitHubLogin $_ }) 'Only valid GitHub logins are mentioned.'

    # Attribution: the last commit that changed each test method's lines.
    $repo = Join-Path $temp 'repo'
    New-Item -ItemType Directory -Force -Path (Join-Path $repo 'tests\Probe.Tests') | Out-Null
    Push-Location $repo
    try {
        & git init -q; & git config user.email 'test@example.invalid'; & git config user.name 'test'; & git config core.autocrlf false
        $file = 'tests/Probe.Tests/ProbeTests.cs'
        $before = "namespace Probe;`npublic class ProbeTests`n{`n    [Fact]`n    public void Stable()`n    {`n        Assert.True(true);`n    }`n`n    [Fact]`n    public void Changed()`n    {`n        Assert.True(true);`n    }`n}`n"
        [IO.File]::WriteAllText((Join-Path $repo $file), $before)
        & git add -A; & git commit -q -m first
        $first = (& git rev-parse HEAD).Trim()
        [IO.File]::WriteAllText((Join-Path $repo $file), $before.Replace("    public void Changed()`n    {`n        Assert.True(true);", "    public void Changed()`n    {`n        Assert.False(false);"))
        & git add -A; & git commit -q -m second
        $second = (& git rev-parse HEAD).Trim()
        $index = Get-CiTestIndex -Root $repo
        Assert-Equal @('Probe.ProbeTests.Changed', 'Probe.ProbeTests.Stable') @($index.Keys | Sort-Object) 'The index lists every test method.'
        Assert-Equal $first (Get-CiLastChange -Root $repo -Location $index['Probe.ProbeTests.Stable'] -Head $second) 'An unchanged test is attributed to the commit that added it.'
        Assert-Equal $second (Get-CiLastChange -Root $repo -Location $index['Probe.ProbeTests.Changed'] -Head $second) 'A changed test is attributed to the commit that changed it.'
    } finally {
        Pop-Location
    }

    # The comment: marker, escaped names, mentions only for valid logins, and a cap.
    $sha = 'a' * 40
    $tests = @(
        [pscustomobject]@{ FullyQualifiedName = 'Ui.T.A @evil | x'; Artifact = 'test-results-ui'; Commit = $sha; PullRequest = 12; Author = 'karkarl' },
        [pscustomobject]@{ FullyQualifiedName = 'Ui.T.B'; Artifact = 'test-results-ui'; Commit = $null; PullRequest = $null; Author = 'bad name' }
    )
    $comment = New-CiMainFailureComment -Repository 'o/r' -RunId 5 -RunAttempt 2 -HeadSha $sha -FailedJobs @('UI tests') -Tests $tests
    $rows = @($comment -split "`n" | Where-Object { $_.StartsWith('| `') })
    Assert-Equal @(
        '| `Ui.T.A @evil \| x` | `test-results-ui` | [aaaaaaaaaaaa](https://github.com/o/r/commit/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa) | #12 | @karkarl |',
        '| `Ui.T.B` | `test-results-ui` | not found | - | - |') $rows 'Each failed test is one escaped row; only valid logins are mentioned.'
    Assert-True ($comment.StartsWith((Get-CiMainFailureMarker -RunId 5 -RunAttempt 2))) 'The comment starts with its run marker.'
    Assert-True ($comment.Contains('Failed jobs: `UI tests`')) 'The comment lists failed jobs.'
    $many = @(1..31 | ForEach-Object { [pscustomobject]@{ FullyQualifiedName = "T.M$_"; Artifact = 'a'; Commit = $null; PullRequest = $null; Author = $null } })
    $listedAll = New-CiMainFailureComment -Repository 'o/r' -RunId 5 -RunAttempt 1 -HeadSha $sha -Tests $many
    Assert-Equal 31 @($listedAll -split "`n" | Where-Object { $_.StartsWith('| `T.M') }).Count 'Every failed test that fits in a comment is attributed.'
    $huge = @(1..600 | ForEach-Object { [pscustomobject]@{ FullyQualifiedName = ("T.Long$_." + ('x' * 150)); Artifact = 'a'; Commit = $sha; PullRequest = 1; Author = 'karkarl' } })
    $capped = New-CiMainFailureComment -Repository 'o/r' -RunId 5 -RunAttempt 1 -HeadSha $sha -Tests $huge
    Assert-True ($capped.Length -le 60000) 'A report stays within GitHub''s comment size limit.'
    Assert-True ($capped -match '\d+ more failed test\(s\) did not fit') 'Tests that do not fit are counted.'
    $many[30].Author = 'late-author'
    Assert-True ((New-CiMainFailureComment -Repository 'o/r' -RunId 5 -RunAttempt 1 -HeadSha $sha -Tests $many).Contains('Please check: @late-author')) `
        'Every author is mentioned.'
    Assert-True ((New-CiMainFailureComment -Repository 'o/r' -RunId 5 -RunAttempt 1 -HeadSha $sha).Contains('No failed test results')) 'A run without failed tests still reports.'

    # Publishing: create the label and issue once, then comment once per run attempt. The fake returns
    # bot-authored data only, as the real queries filter by author; each comment body is one JSON string.
    $marker = Get-CiMainFailureMarker -RunId 9 -RunAttempt 1
    $other = Get-CiMainFailureMarker -RunId 8 -RunAttempt 1
    function Invoke-Publish([string[]]$Labels, [object[]]$IssueLists, [hashtable]$Comments = @{}, [hashtable]$Bodies = @{}, [switch]$LabelCreateFails, [string[]]$LabelsAfterCreate = @()) {
        $calls = [Collections.Generic.List[string]]::new()
        $state = @{ IssueQueries = 0; LabelCreateTried = $false }
        $fake = {
            $line = $args -join ' '
            $calls.Add($line)
            switch -Regex ($line) {
                '^api repos/o/r/labels\?' { if ($state.LabelCreateTried) { return $LabelsAfterCreate }; return $Labels }
                '^api repos/o/r/labels -f' { $state.LabelCreateTried = $true; if ($LabelCreateFails) { throw 'label exists' }; return }
                '^api repos/o/r/issues\?state=open&labels=ci%3A%20main-flake' {
                    $list = $IssueLists[[Math]::Min($state.IssueQueries, $IssueLists.Count - 1)]
                    $state.IssueQueries++
                    return $list
                }
                '^api repos/o/r/issues/(\d+)/comments' { return @($Comments[$Matches[1]] | Where-Object { $_ } | ForEach-Object { $_ | ConvertTo-Json -Compress }) }
                '^api repos/o/r/issues/(\d+) ' { if ($Bodies.ContainsKey($Matches[1])) { return ($Bodies[$Matches[1]] -split "`n") }; return 'intro' }
                '^issue create' { return 'https://github.com/o/r/issues/77' }
            }
        }.GetNewClosure()
        $result = Publish-CiMainFailureReport -Repository 'o/r' -Label 'ci: main-flake' -Marker $marker -Comment "$marker`nbody" -Gh $fake -WorkDirectory $temp
        [pscustomobject]@{ Result = "$($result.Action) $($result.Target)"; Calls = @($calls) }
    }
    $fresh = Invoke-Publish -Labels @('bug') -IssueLists @(@(), @('77'))
    Assert-Equal 'created #77' $fresh.Result 'Without an open issue, one is created.'
    Assert-True (@($fresh.Calls | Where-Object { $_ -like 'api repos/o/r/labels -f name=ci: main-flake *' }).Count -eq 1) 'A missing label is created.'
    Assert-True (@($fresh.Calls | Where-Object { $_ -like 'issue create --repo o/r --title * --label ci: main-flake --body-file *' }).Count -eq 1) 'The issue is created with the label.'
    $race = Invoke-Publish -Labels @() -LabelsAfterCreate @('ci: main-flake') -IssueLists @(@(), @('40', '77')) -Bodies @{ '77' = "intro`n`n$marker`nbody" } -LabelCreateFails
    Assert-Equal 'commented #40' $race.Result 'When a parallel watch created an older issue, the report goes there.'
    Assert-Equal 1 @($race.Calls | Where-Object { $_ -like 'issue comment 40 *' }).Count 'The report is posted to the kept issue once.'
    Assert-True (@($race.Calls | Where-Object { $_ -like 'issue close 77 *Duplicate of #40.*' }).Count -eq 1) 'The newer duplicate issue is closed.'
    $lostLabel = $false
    try { Invoke-Publish -Labels @() -IssueLists @(@()) -LabelCreateFails | Out-Null } catch { $lostLabel = $true }
    Assert-True $lostLabel 'A label that cannot be created and still does not exist fails the watch.'
    $open = Invoke-Publish -Labels @('ci: main-flake') -IssueLists @(, @('40')) -Comments @{ '40' = @("$other`nolder report") }
    Assert-Equal 'commented #40' $open.Result 'A new run attempt is added to the open tracking issue.'
    Assert-True (-not @($open.Calls | Where-Object { $_ -like 'api repos/o/r/labels -f*' -or $_ -like 'issue close*' }).Count) 'An existing label is not recreated and nothing is closed.'
    $botOnly = 'select(.user.login == "github-actions[bot]")'
    Assert-Equal 3 @($open.Calls | Where-Object { $_.Contains($botOnly) }).Count 'Issues, comments and the issue body only count when the workflow wrote them.'
    $leftover = Invoke-Publish -Labels @('ci: main-flake') -IssueLists @(, @('55', '40')) -Bodies @{ '55' = "intro`n`n$other`nonly here" }
    Assert-Equal 'commented #40' $leftover.Result 'Reports go to the oldest open tracking issue.'
    Assert-Equal 2 @($leftover.Calls | Where-Object { $_ -like 'issue comment 40 *' }).Count 'A report held only by a duplicate is copied over, then the new report is added.'
    Assert-True (@($leftover.Calls | Where-Object { $_ -like 'issue close 55 *Duplicate of #40.*' }).Count -eq 1) 'A duplicate left by an interrupted watch is closed later.'
    $repeat = Invoke-Publish -Labels @('ci: main-flake') -IssueLists @(, @('40')) -Comments @{ '40' = @("$marker`nmulti`nline") }
    Assert-Equal 'already-reported #40' $repeat.Result 'A run attempt is reported once.'
    Assert-True (-not @($repeat.Calls | Where-Object { $_ -like 'issue comment*' }).Count) 'A repeated attempt posts nothing.'
    # Artifacts: per name, the newest one the attempt produced or reused.
    $t0 = [datetime]::new(2026, 1, 1, 10, 0, 0, [DateTimeKind]::Utc)
    $listed = @(
        [pscustomobject]@{ Id = 1; Name = 'test-results-ui'; CreatedAt = $t0.AddMinutes(10); Expired = $false },
        [pscustomobject]@{ Id = 2; Name = 'test-results-ui'; CreatedAt = $t0.AddMinutes(70); Expired = $false },
        [pscustomobject]@{ Id = 3; Name = 'test-results-core'; CreatedAt = $t0.AddMinutes(5); Expired = $false },
        [pscustomobject]@{ Id = 4; Name = 'e2e-test-results-setup-connect'; CreatedAt = $t0.AddMinutes(8); Expired = $true },
        [pscustomobject]@{ Id = 5; Name = 'crash-dumps'; CreatedAt = $t0.AddMinutes(9); Expired = $false })
    Assert-Equal @('3:test-results-core', '1:test-results-ui') @((Select-CiAttemptArtifacts -Artifacts $listed -After $t0 -Before $t0.AddMinutes(60)) | ForEach-Object { "$($_.Id):$($_.Name)" }) `
        'An earlier attempt uses the artifacts created before the next attempt started.'
    Assert-Equal @('2:test-results-ui') @((Select-CiAttemptArtifacts -Artifacts $listed -After $t0.AddMinutes(60) -Before $null) | ForEach-Object { "$($_.Id):$($_.Name)" }) `
        'A later attempt uses only its own uploads; reused, other and expired artifacts are skipped.'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Main failure watch checks passed.'
