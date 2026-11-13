<#
.SYNOPSIS
    Rejects new timing-sensitive patterns in repository test code.
.DESCRIPTION
    Compiles tests\OpenClaw.Tray.Tests\TestTimingPatternGuard.cs with Add-Type and compares
    each test file's literal short hang guards, fixed delays, and one-shot UI Automation lookups
    with the checked-in baseline, which may only shrink. Runs in always-on fast validation, so
    pull requests that change only UI or Shared tests are still checked. Requires PowerShell 7.
.PARAMETER LowerBaseline
    Write shrunk counts back to the baseline. Counts are never raised.
.EXAMPLE
    .\scripts\validate-test-timing-patterns.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$LowerBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($RepoRoot)
if (-not ('OpenClaw.Tray.Tests.TestTimingPatternGuard' -as [type])) {
    Add-Type -Path (Join-Path $root 'tests\OpenClaw.Tray.Tests\TestTimingPatternGuard.cs')
}
$guard = [OpenClaw.Tray.Tests.TestTimingPatternGuard]
$failures = @($guard::Evaluate($root, $LowerBaseline.IsPresent))
# Rule totals accompany failures as context for reviewing moved entries; they are not failures.
$changes = @($failures | Where-Object { -not $_.StartsWith('Total ', [StringComparison]::Ordinal) })
if ($changes.Count -gt 0) {
    foreach ($failure in $changes) {
        $escaped = $failure.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error title=Timing-sensitive test pattern::$escaped"
    }
    $failures | Where-Object { $_.StartsWith('Total ', [StringComparison]::Ordinal) } | ForEach-Object { Write-Host $_ }
    throw ("{0} timing-sensitive test pattern change(s). Use the named helper, add '// {1} <reason>' for a deliberate case, or lower the baseline. See {2}." -f
        $changes.Count, $guard::ExceptionMarker, $guard::GuideAnchor)
}
Write-Host 'Test timing pattern ratchet passed.'
