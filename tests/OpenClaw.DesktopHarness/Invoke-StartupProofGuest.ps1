[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Config,
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $RunRoot,
    [Parameter(Mandatory)][ValidateSet('Disabled','Record')][string] $Mode
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\StartupProof.ps1"
. "$PSScriptRoot\StartupProof.Native.ps1"
$c = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.Major -lt 7 -or $c.computer -cne $env:COMPUTERNAME -or
    $c.leaseId -cne $LeaseId -or $LeaseId -notmatch '^cbx_[0-9a-f]+$' -or
    !$c.countedInvalidShowsAuthorized -or (Test-Path -LiteralPath $RunRoot)) {
    throw 'Exact new guest authorization, explicit counted limitation and fresh output are required.'
}
if ($Mode -ceq 'Record' -and !(Test-Path $c.dryRunReceipt)) {
    throw 'Recording requires a complete reaped full dry-run receipt before even launching the actors.'
}
$null = New-Item -ItemType Directory -Path (Join-Path $RunRoot 'control')
$taskName = 'OpenClaw1791-' + [Guid]::NewGuid().ToString('N')
$created = $false; $recorder = $null; $run = $null; $errorText = $null
$taskAbsent = $false; $mediumExited = $false; $recorderExited = $false
try {
    $arguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-StartupRecorder.ps1'),
        '-Config',$Config,'-LeaseId',$LeaseId,'-RunRoot',$RunRoot,'-Mode',$Mode)
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $c.powerShell; $psi.UseShellExecute = $false
    foreach ($arg in $arguments) { $psi.ArgumentList.Add($arg) }
    $recorder = [Diagnostics.Process]::Start($psi)
    $mediumArguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Invoke-StartupProof.ps1'),
        '-Config',$Config,'-LeaseId',$LeaseId,'-RunRoot',$RunRoot,'-Mode',$Mode)
    $quoted = @($mediumArguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Invalid task argument.' }
        '"' + $_ + '"'
    }) -join ' '
    $action = New-ScheduledTaskAction -Execute $c.powerShell -Argument $quoted -WorkingDirectory $c.workingDirectory
    $principal = New-ScheduledTaskPrincipal -UserId $c.actorSid -LogonType Interactive -RunLevel Limited
    $null = Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal
    $created = $true
    Start-ScheduledTask -TaskName $taskName
    $run = Wait-StartupReceipt (Join-Path $RunRoot 'scenario\run.json') $c.deadlineSeconds
    $caller = Wait-StartupReceipt (Join-Path $RunRoot 'scenario\caller-ready.json') 10
    $medium = Get-Process -Id $caller.pid -ErrorAction SilentlyContinue
    if (!(Test-StartupProcessIdentity $caller.pid $caller.creationUtc $medium)) { $mediumExited = $true }
    else {
        try { $mediumExited = $medium.WaitForExit(10000) } finally { $medium.Dispose() }
    }
    $recorderExited = $recorder.WaitForExit(30000)
    if (!$mediumExited -or !$recorderExited) { throw 'Exact medium controller or recorder did not exit.' }
    $stopped = Wait-StartupReceipt (Join-Path $RunRoot 'control\trace-stopped.json') 10
    if (!$stopped.instanceAbsent -or !$run.traceInstanceAbsent) { throw 'Exact named trace is not absent.' }
} catch { $errorText = $_.Exception.Message }
finally {
    if ($created) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task -and $task.State -ceq 'Running') { Stop-ScheduledTask -TaskName $taskName }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        $taskAbsent = $null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    }
    if ($recorder) {
        if (!$recorder.HasExited) {
            # Kill only this retained owned recorder. Its independent watchdog sees the identity exit and stops the owned instance.
            $recorder.Kill(); $recorder.WaitForExit()
            $null = Wait-StartupReceipt (Join-Path $RunRoot 'control\trace-stopped.json') 60
        }
        $recorderExited = $recorder.HasExited
        $recorder.Dispose()
    }
}
Complete-StartupProofController -Config $c -Mode $Mode -RunRoot $RunRoot -Run $run `
    -TaskAbsent $taskAbsent -MediumProcessExited $mediumExited -RecorderProcessExited $recorderExited `
    -ErrorText $errorText | ConvertTo-Json
