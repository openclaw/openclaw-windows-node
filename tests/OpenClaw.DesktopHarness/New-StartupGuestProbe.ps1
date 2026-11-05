[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $Destination,
    [string] $EntryPoint = "$PSScriptRoot\Get-StartupGuestQualification.ps1"
)
$ErrorActionPreference = 'Stop'
if ($LeaseId -notmatch '^cbx_[a-f0-9]+$' -or (Test-Path -LiteralPath $Destination)) {
    throw 'Exact fresh authorized lease and new script destination required.'
}
$body = Get-Content -LiteralPath $EntryPoint -Raw -ErrorAction Stop
$output = 'C:\crabbox\hub-proof1791\qualification-' + $LeaseId
$script = "& {`n$body`n} -LeaseId '$LeaseId' -OutputDirectory '$output' -GuestAuthorized`n"
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Destination),$script,[Text.UTF8Encoding]::new($false))
[pscustomobject]@{ leaseId = $LeaseId; scriptSha256 = (Get-FileHash -LiteralPath $Destination).Hash }
