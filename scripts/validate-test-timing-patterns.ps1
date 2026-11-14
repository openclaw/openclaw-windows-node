<#
.SYNOPSIS
    Rejects new timing-sensitive patterns in repository test code.
.DESCRIPTION
    Compiles tests\OpenClaw.Tray.Tests\TestTimingPatternGuard.cs with Add-Type and compares
    each test file's literal short hang guards, fixed delays, and one-shot UI Automation lookups
    with the checked-in baseline. Growth fails; a shrunk count only warns, with the command that
    lowers the baseline. Runs in always-on fast validation, so pull requests that change only UI
    or Shared tests are still checked. Requires PowerShell 7.
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
$result = $guard::Evaluate($root, $LowerBaseline.IsPresent)
function ConvertTo-WorkflowData([string]$Text) { $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }
# Shrinking never fails, so pull requests that remove patterns stay green; tighten when convenient.
foreach ($entry in @($result.Shrunk)) {
    Write-Host ("::warning title=Timing baseline can be lowered::{0}" -f
        (ConvertTo-WorkflowData "$entry Run $($guard::LowerBaselineCommand) and commit the baseline."))
}
if (@($result.Failures).Count -gt 0) {
    foreach ($failure in @($result.Failures)) {
        Write-Host "::error title=Timing-sensitive test pattern::$(ConvertTo-WorkflowData $failure)"
    }
    # Totals are context for a baseline entry moved with renamed code; they are not failures.
    @($result.Totals) | ForEach-Object { Write-Host $_ }
    throw ("{0} new timing-sensitive test pattern(s). Use the named helper or add '// {1} <reason>' for a deliberate case. See {2}." -f
        @($result.Failures).Count, $guard::ExceptionMarker, $guard::GuideAnchor)
}
Write-Host 'Test timing pattern ratchet passed.'
