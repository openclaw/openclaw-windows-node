<#
.SYNOPSIS
  Closes orphan Windows Terminal "ghost" frames left behind by tray tests
  or MSIX packaging tools.

.DESCRIPTION
  A safety-net for the case where the in-process cleanup in
  tests/OpenClaw.Tray.Tests/WinAppSdkGhostWindowCleanup.cs cannot catch a
  ghost — either because:
   - the ghost was created by msbuild / MakeAppx.exe / signtool.exe spawning
     a console child during MSIX packaging (runs outside the test process), or
   - a testhost was killed abnormally (SIGKILL, hung Ctrl+C, OOM) before its
     ProcessExit / AssemblyLoadContext.Unloading hooks could fire.

  Mirrors the production cleanup's filter so we don't ever close the user's
  real Terminal windows by accident: window class must be
  CASCADIA_HOSTING_WINDOW_CLASS, title must be EXACTLY "Terminal" (a real
  interactive Terminal updates its title to the running command or cwd as
  soon as the user types anything), owner must be the WindowsTerminal
  process, and size must be at least 1000x500.

  The close sequence is the one we proved works against stuck Terminal
  frames during MSIX-E2E manual testing: ShowWindow(SW_HIDE) ->
  PostMessage(WM_SYSCOMMAND, SC_CLOSE) ->
  SendMessageTimeout(WM_CLOSE, SMTO_ABORTIFHUNG, 1000ms). A plain
  SendMessage(WM_SYSCOMMAND, SC_CLOSE) alone does NOT work — WindowsTerminal
  swallows it on these orphan frames.

  ## WHY YOU MAY STILL SEE GHOSTS AFTER A FIX

  On Win11 with Windows Terminal as the default terminal app (the new Win11
  default, registry value
  HKCU\Console\%%Startup\DelegationConsole = {2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}),
  EVERY console-spawning child process gets a Cascadia hosting frame
  allocated. Most close cleanly when the child exits; a small fraction
  leak under timing conditions. The in-process test cleanup
  (tests/OpenClaw.Tray.Tests/WinAppSdkGhostWindowCleanup.cs) only fires
  inside the testhost lifetime. build.ps1's end-of-build hook only fires
  after a successful build. NEITHER catches leaks from:

   - `gh`, `git`, `dotnet`, `pwsh`, or any other CLI you run from your
     shell outside of `./build.ps1`
   - Agent tooling that spawns fresh PowerShell sessions (each session's
     console allocation is a fresh opportunity to leak)
   - Killed-with-Ctrl+C / OOM processes that never ran teardown

  For high-shell-activity sessions (e.g., agent-driven work on this branch),
  use `-Daemon` to run a background watcher, or `-InstallScheduledTask` for
  always-on protection without keeping a console alive.

.PARAMETER WhatIf
  Lists matching ghosts without closing them.

.PARAMETER Quiet
  Suppress per-window status lines; only print the summary.

.PARAMETER Daemon
  Run continuously in the foreground, polling every -PollSeconds seconds
  and cleaning any ghosts found. Use this when you're about to do a lot
  of shell work and don't want to remember to clean up. Stop with Ctrl+C.

.PARAMETER PollSeconds
  Daemon mode polling interval. Default 30s; minimum 5s.

.PARAMETER InstallScheduledTask
  Register a Windows scheduled task that runs this script every 5 minutes
  under the current user. Idempotent — replaces any existing task with the
  same name. Use -UninstallScheduledTask to remove it.

.PARAMETER UninstallScheduledTask
  Unregister the scheduled task created by -InstallScheduledTask.

.EXAMPLE
  ./scripts/cleanup-ghost-windows.ps1

.EXAMPLE
  ./scripts/cleanup-ghost-windows.ps1 -WhatIf

.EXAMPLE
  # Background watcher for the duration of a shell session:
  ./scripts/cleanup-ghost-windows.ps1 -Daemon

.EXAMPLE
  # One-time setup for developers doing heavy shell work on this branch:
  ./scripts/cleanup-ghost-windows.ps1 -InstallScheduledTask

.NOTES
  This script is also invoked automatically at the end of ./build.ps1 on
  Windows, after MSIX packaging steps that are known to spawn console
  children. Running it manually is the recommended recovery if you see
  blank Terminal frames piling up after running tests in this repo.
#>

[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'OneShot')]
param(
  [Parameter(ParameterSetName = 'OneShot')]
  [Parameter(ParameterSetName = 'Daemon')]
  [switch] $Quiet,

  [Parameter(ParameterSetName = 'Daemon', Mandatory = $true)]
  [switch] $Daemon,

  [Parameter(ParameterSetName = 'Daemon')]
  [ValidateRange(5, 3600)]
  [int] $PollSeconds = 30,

  [Parameter(ParameterSetName = 'InstallTask', Mandatory = $true)]
  [switch] $InstallScheduledTask,

  [Parameter(ParameterSetName = 'UninstallTask', Mandatory = $true)]
  [switch] $UninstallScheduledTask
)

if (-not $IsWindows -and $PSVersionTable.Platform -ne 'Win32NT') {
  if (-not $Quiet) { Write-Host "Not on Windows; nothing to do." }
  return
}

Add-Type -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool EnumWindows(EnumWindowsProc enumProc, System.IntPtr lParam);

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetWindowText(System.IntPtr hWnd, System.Text.StringBuilder text, int count);

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder text, int count);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint lpdwProcessId);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PostMessage(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.IntPtr wParam, System.IntPtr lParam, uint flags, uint timeout, out System.IntPtr result);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

public delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
'@ -Name TerminalGhostCleanupNative -Namespace OpenClawCleanup -ErrorAction SilentlyContinue

function Find-GhostFrames {
  $found = New-Object System.Collections.ArrayList
  $proc = [OpenClawCleanup.TerminalGhostCleanupNative+EnumWindowsProc]{
    param($hWnd, $lParam)
    if (-not [OpenClawCleanup.TerminalGhostCleanupNative]::IsWindowVisible($hWnd)) { return $true }

    $cls = New-Object System.Text.StringBuilder 256
    [OpenClawCleanup.TerminalGhostCleanupNative]::GetClassName($hWnd, $cls, 256) | Out-Null
    if ($cls.ToString() -ne 'CASCADIA_HOSTING_WINDOW_CLASS') { return $true }

    $sb = New-Object System.Text.StringBuilder 256
    [OpenClawCleanup.TerminalGhostCleanupNative]::GetWindowText($hWnd, $sb, 256) | Out-Null
    if ($sb.ToString() -ne 'Terminal') { return $true }

    $ownerPid = 0
    [OpenClawCleanup.TerminalGhostCleanupNative]::GetWindowThreadProcessId($hWnd, [ref]$ownerPid) | Out-Null
    try {
      $owner = Get-Process -Id $ownerPid -ErrorAction Stop
      if ($owner.ProcessName -ne 'WindowsTerminal') { return $true }
    } catch { return $true }

    $rect = New-Object OpenClawCleanup.TerminalGhostCleanupNative+RECT
    if (-not [OpenClawCleanup.TerminalGhostCleanupNative]::GetWindowRect($hWnd, [ref]$rect)) { return $true }
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -lt 1000 -or $h -lt 500) { return $true }

    $null = $found.Add([PSCustomObject]@{
      HWND = $hWnd; OwnerPid = $ownerPid; Width = $w; Height = $h
    })
    return $true
  }
  [OpenClawCleanup.TerminalGhostCleanupNative]::EnumWindows($proc, [System.IntPtr]::Zero) | Out-Null
  return $found
}

function Close-GhostFrame {
  param([System.IntPtr] $HWnd)
  # Proven sequence: hide first so the screen doesn't strobe, then PostMessage
  # SYSCOMMAND/SC_CLOSE (queued, non-blocking), then SendMessageTimeout WM_CLOSE
  # with SMTO_ABORTIFHUNG so we never block on a hung Terminal frame.
  [OpenClawCleanup.TerminalGhostCleanupNative]::ShowWindow($HWnd, 0) | Out-Null
  [OpenClawCleanup.TerminalGhostCleanupNative]::PostMessage($HWnd, 0x0112, [System.IntPtr]0xF060, [System.IntPtr]::Zero) | Out-Null
  $result = [System.IntPtr]::Zero
  [OpenClawCleanup.TerminalGhostCleanupNative]::SendMessageTimeout($HWnd, 0x0010, [System.IntPtr]::Zero, [System.IntPtr]::Zero, 0x0002, 1000, [ref]$result) | Out-Null
}

function Invoke-OneShotCleanup {
  param([switch] $Quiet)
  $totalClosed = 0
  for ($pass = 1; $pass -le 5; $pass++) {
    $ghosts = Find-GhostFrames
    if ($ghosts.Count -eq 0) { break }

    if ($pass -eq 1 -and -not $Quiet) {
      Write-Host ("Found {0} ghost Terminal frame(s):" -f $ghosts.Count) -ForegroundColor Yellow
    }

    foreach ($g in $ghosts) {
      if ($PSCmdlet.ShouldProcess("HWND $($g.HWND) (Owner WindowsTerminal PID $($g.OwnerPid), $($g.Width)x$($g.Height))", "Close")) {
        Close-GhostFrame -HWnd $g.HWND
        $totalClosed++
        if (-not $Quiet) {
          Write-Host ("  Pass {0}: closed HWND {1} ({2}x{3})" -f $pass, $g.HWND, $g.Width, $g.Height) -ForegroundColor Green
        }
      }
    }

    Start-Sleep -Milliseconds 500
  }

  $remaining = (Find-GhostFrames).Count
  if ($remaining -gt 0) {
    Write-Host ("WARNING: {0} ghost frame(s) still present after 5 passes. Try running again or reboot." -f $remaining) -ForegroundColor Red
    return @{ Closed = $totalClosed; Remaining = $remaining }
  }

  if (-not $Quiet -and $totalClosed -eq 0) {
    Write-Host "No ghost Terminal frames detected."
  } elseif (-not $Quiet -and $totalClosed -gt 0) {
    Write-Host ("Closed {0} ghost Terminal frame(s)." -f $totalClosed) -ForegroundColor Green
  }
  return @{ Closed = $totalClosed; Remaining = 0 }
}

# -----------------------------------------------------------------------------
# Scheduled-task installer
# -----------------------------------------------------------------------------
$taskName = 'OpenClaw-Ghost-Terminal-Cleanup'

if ($InstallScheduledTask) {
  # Idempotent: drop any existing registration first.
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null

  $scriptPath = $MyInvocation.MyCommand.Path
  $action = New-ScheduledTaskAction `
    -Execute 'pwsh.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -File `"$scriptPath`" -Quiet"
  $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 5)
  $settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
  $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive

  Register-ScheduledTask -TaskName $taskName `
    -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "Auto-close orphan Windows Terminal 'Terminal' frames left by OpenClaw tests and msbuild. See scripts/cleanup-ghost-windows.ps1." | Out-Null

  Write-Host "Installed scheduled task '$taskName' (runs every 5 minutes, current user, hidden)." -ForegroundColor Green
  Write-Host "  Uninstall:  ./scripts/cleanup-ghost-windows.ps1 -UninstallScheduledTask"
  exit 0
}

if ($UninstallScheduledTask) {
  $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
  if ($null -eq $existing) {
    Write-Host "Scheduled task '$taskName' is not registered; nothing to remove."
    exit 0
  }
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false | Out-Null
  Write-Host "Unregistered scheduled task '$taskName'." -ForegroundColor Green
  exit 0
}

# -----------------------------------------------------------------------------
# Daemon mode
# -----------------------------------------------------------------------------
if ($Daemon) {
  Write-Host "Daemon mode: polling every $PollSeconds s. Ctrl+C to stop." -ForegroundColor Cyan
  $cycle = 0
  while ($true) {
    $cycle++
    $r = Invoke-OneShotCleanup -Quiet:$Quiet
    if ($r.Closed -gt 0 -and -not $Quiet) {
      Write-Host ("[{0}] cycle {1}: closed {2}" -f (Get-Date -Format HH:mm:ss), $cycle, $r.Closed) -ForegroundColor Green
    }
    Start-Sleep -Seconds $PollSeconds
  }
}

# -----------------------------------------------------------------------------
# Default: one-shot
# -----------------------------------------------------------------------------
$result = Invoke-OneShotCleanup -Quiet:$Quiet
if ($result.Remaining -gt 0) { exit 1 }
exit 0
