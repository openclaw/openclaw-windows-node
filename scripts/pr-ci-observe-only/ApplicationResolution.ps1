# Git/dotnet metadata selection only. The callers explicitly request native precedence with TotalCount 1.
# Leave Candidates/Source untyped until cardinality and ApplicationInfo identity have been checked.
function Select-ObserveApplicationSource {
    param($Candidates, [Parameter(Mandatory)][scriptblock]$CanonicalPath)
    if ($null -eq $Candidates) { throw 'application-cardinality' }
    $items = @($Candidates)
    if ($items.Count -ne 1) { throw 'application-cardinality' }
    $application = $items[0]
    if ($application -isnot [System.Management.Automation.ApplicationInfo] -or
        $application.CommandType -ne [System.Management.Automation.CommandTypes]::Application) { throw 'application-type' }
    $source = $application.Source
    if ($source -isnot [string]) { throw 'application-source-shape' }
    if ([string]::IsNullOrWhiteSpace($source) -or $source -cmatch '[\x00-\x1f\x7f"]' -or
        -not [IO.Path]::IsPathFullyQualified($source)) { throw 'application-source-shape' }
    if (-not [IO.File]::Exists($source)) { throw 'application-source-file' }
    $null = & $CanonicalPath $source # Existing full-path/no-reparse validation; never coerces a collection.
    return $source # Preserve the selected scalar, rather than joining, trimming or guessing another path.
}
