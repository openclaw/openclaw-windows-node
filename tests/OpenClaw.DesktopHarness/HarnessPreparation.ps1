function Resolve-DesktopHarnessExecutable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Executable,
        [string] $ExpectedSha256,
        [scriptblock] $Build
    )
    if ($ExpectedSha256) {
        if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'A frozen harness requires a complete SHA256 digest.'
        }
    } else {
        if (!$Build) { throw 'Harness preparation requires a build operation or a frozen digest.' }
        $null = & $Build
    }
    if (!(Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw 'The prepared harness executable is missing.'
    }
    if ($ExpectedSha256 -and (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash -ine $ExpectedSha256) {
        throw 'The prepared harness executable differs from its frozen digest.'
    }
    $Executable
}
