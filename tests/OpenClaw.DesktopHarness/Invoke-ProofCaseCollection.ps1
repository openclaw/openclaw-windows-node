function Invoke-ProofCaseCollection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Cases,
        [Parameter(Mandatory)][scriptblock]$RunCase
    )
    foreach ($case in $Cases) {
        try {
            $caseInvocationOutput = @(& $RunCase $case)
            if (!$caseInvocationOutput.Count) { throw "No result returned for $($case.name)" }
            $collectedCaseResult = $caseInvocationOutput[-1]
            if ($collectedCaseResult.name -cne $case.name -or $collectedCaseResult.passed -isnot [bool]) {
                throw "Invalid result returned for $($case.name)"
            }
            $collectedCaseResult
        }
        catch {
            [ordered]@{
                name = $case.name
                passed = $false
                negativeControl = [bool]$case.negative
                error = $_.Exception.Message
            }
        }
    }
}
