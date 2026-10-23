$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Invoke-ProofCaseCollection.ps1"
$specifications = @(
    @{ name = 'one'; negative = $true },
    @{ name = 'two'; negative = $true },
    @{ name = 'three'; negative = $false },
    @{ name = 'four'; negative = $false }
)
$calls = [Collections.Generic.List[string]]::new()
$results = @(Invoke-ProofCaseCollection $specifications {
    param($case)
    $calls.Add($case.name)
    if ($case.name -in @('one','three')) { throw "failed-$($case.name)" }
    'incidental launcher output'
    [ordered]@{ name = $case.name; passed = $true; exitCode = 0 }
})
if (($calls -join ',') -cne 'one,two,three,four' -or $results.Count -ne 4) {
    throw 'Every independent case must execute once after earlier failures.'
}
if ($results[0].passed -or $results[0].error -cne 'failed-one' -or
    !$results[1].passed -or $results[1].exitCode -ne 0 -or
    $results[2].passed -or $results[2].error -cne 'failed-three' -or !$results[3].passed) {
    throw 'Case collection lost an independent failure or success.'
}
$missing = @(Invoke-ProofCaseCollection $specifications {
    param($case)
    if ($case.name -ne 'one') { @{ name = $case.name; passed = $true } }
})
if ($missing.Count -ne 4 -or $missing[0].passed -or !$missing[3].passed) {
    throw 'Missing output must fail its own case without dropping later cases.'
}
$invalid = @(Invoke-ProofCaseCollection @($specifications[0]) {
    @{ name = 'different-case'; passed = $true }
})
if ($invalid.Count -ne 1 -or $invalid[0].passed) { throw 'Mismatched case identity cannot pass.' }
$output = 'controlled-proof-root'
$paths = @(Invoke-ProofCaseCollection $specifications {
    param($case)
    [ordered]@{ name = $case.name; passed = $true; directory = "$output\$($case.name)" }
})
if (($paths.directory -join ',') -cne
    'controlled-proof-root\one,controlled-proof-root\two,controlled-proof-root\three,controlled-proof-root\four') {
    throw 'Collector locals must not replace the callback-owned output directory after a successful case.'
}
'PASS: 4 native-free case-collection regressions.'
