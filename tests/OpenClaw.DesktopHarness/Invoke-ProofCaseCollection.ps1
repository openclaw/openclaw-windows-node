function Invoke-ProofCaseCollection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Cases,
        [Parameter(Mandatory)][scriptblock]$RunCase
    )
    foreach ($case in $Cases) {
        try {
            $output = @(& $RunCase $case)
            if (!$output.Count) { throw "No result returned for $($case.name)" }
            $result = $output[-1]
            if ($result.name -cne $case.name -or $result.passed -isnot [bool]) {
                throw "Invalid result returned for $($case.name)"
            }
            $result
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
