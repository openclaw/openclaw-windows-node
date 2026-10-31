param(
    [Parameter(Mandatory)][string]$TaskName,
    [Parameter(Mandatory)][string]$HostPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$Family
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) {
    throw 'The windowless cleanup host is missing. Automatic Gateway cleanup could not be registered.'
}
$action = New-ScheduledTaskAction -Execute $HostPath -Argument $Family
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$logon = New-ScheduledTaskTrigger -AtLogOn -User $principal.UserId
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Trigger @($trigger, $logon) -Settings $settings -Force | Out-Null
