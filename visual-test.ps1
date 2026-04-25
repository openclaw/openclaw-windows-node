<#
.SYNOPSIS
    Visual test framework for OpenClaw onboarding wizard.
    Captures screenshots of the app window for LLM-based visual validation.

.DESCRIPTION
    Launches the tray app, finds the onboarding window, captures screenshots,
    and can click buttons to navigate between pages. Screenshots are saved as
    PNGs for analysis with the Copilot CLI view tool.

.EXAMPLE
    .\visual-test.ps1                        # Capture current window
    .\visual-test.ps1 -Launch -Clean         # Launch fresh app + capture
    .\visual-test.ps1 -Launch -Clean -Pages 3  # Launch + capture 3 pages
    .\visual-test.ps1 -CaptureAll            # Capture all pages (navigates automatically)
#>
param(
    [switch]$Launch,
    [switch]$Clean,
    [int]$Pages = 1,
    [switch]$CaptureAll,
    [string]$OutputDir = ".\visual-test-output"
)

$ErrorActionPreference = "Stop"

# ============================================================================
# P/Invoke: Window finding
# ============================================================================

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WinApi
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public static IntPtr FindWindowByPid(uint targetPid)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid && IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (sb.Length > 0)
                {
                    result = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

# Load System.Drawing for screenshot capture
Add-Type -AssemblyName System.Drawing

# ============================================================================
# UIAutomation: Button clicking
# ============================================================================

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-UIElement {
    param(
        [System.Windows.Automation.AutomationElement]$Parent,
        [string]$Name
    )
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Parent.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-UIButton {
    param(
        [System.Windows.Automation.AutomationElement]$Parent,
        [string]$ButtonName
    )
    $btn = Find-UIElement -Parent $Parent -Name $ButtonName
    if ($btn -eq $null) {
        Write-Host "    Button '$ButtonName' not found" -ForegroundColor Red
        return $false
    }
    try {
        $invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Milliseconds 800
        return $true
    } catch {
        Write-Host "    Failed to click '$ButtonName': $_" -ForegroundColor Red
        return $false
    }
}

# ============================================================================
# Core functions
# ============================================================================

function Capture-AppWindow {
    param(
        [string]$Label,
        [string]$OutputPath,
        [uint32]$ProcessId = 0,
        [string]$WindowTitle = ""
    )

    $hWnd = [IntPtr]::Zero

    if ($ProcessId -gt 0) {
        $hWnd = [WinApi]::FindWindowByPid($Pid)
    }
    if ($hWnd -eq [IntPtr]::Zero -and $WindowTitle) {
        $hWnd = [WinApi]::FindWindow($null, $WindowTitle)
    }
    if ($hWnd -eq [IntPtr]::Zero) {
        Write-Host "  [FAIL] Window not found" -ForegroundColor Red
        return $null
    }

    [WinApi]::SetForegroundWindow($hWnd) | Out-Null
    Start-Sleep -Milliseconds 500

    $rect = New-Object WinApi+RECT
    [WinApi]::GetWindowRect($hWnd, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    if ($width -le 0 -or $height -le 0) {
        Write-Host "  [FAIL] Window has zero size" -ForegroundColor Red
        return $null
    }

    $bmp = New-Object System.Drawing.Bitmap($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    $g.Dispose()
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  [OK] Captured: $Label -> $OutputPath (${width}x${height})" -ForegroundColor Green
    $bmp.Dispose()
    return $OutputPath
}

function Get-AutomationWindow {
    param([uint32]$ProcessId)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$ProcessId)
    $windows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $pidCond)
    foreach ($w in $windows) {
        if ($w.Current.Name -and $w.Current.Name.Length -gt 0) {
            return $w
        }
    }
    return $null
}

# ============================================================================
# Main
# ============================================================================

Write-Host ""
Write-Host "  OpenClaw Visual Test Framework" -ForegroundColor Magenta
Write-Host ""

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

$appPid = $null

# Launch app if requested
if ($Launch) {
    $proc = Get-Process "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Seconds 1
        Write-Host "  Killed existing instance" -ForegroundColor Yellow
    }

    if ($Clean) {
        $sf = "$env:APPDATA\OpenClawTray\settings.json"
        if (Test-Path $sf) {
            Copy-Item $sf "$sf.bak" -Force
            Remove-Item $sf -Force
        }
        Write-Host "  Settings cleared (clean start)" -ForegroundColor Yellow
    }

    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
    $exe = Join-Path $PSScriptRoot "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\$arch\OpenClaw.Tray.WinUI.exe"

    if (-not (Test-Path $exe)) {
        Write-Host "  [FAIL] Exe not found: $exe" -ForegroundColor Red
        Write-Host "  Run .\build.ps1 first" -ForegroundColor Gray
        exit 1
    }

    $appProcess = Start-Process $exe -PassThru
    $appPid = $appProcess.Id
    Write-Host "  Launched app (PID $appPid)" -ForegroundColor Green
    Write-Host "  Waiting for window..." -ForegroundColor Gray
    Start-Sleep -Seconds 5
} else {
    $proc = Get-Process "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue
    if ($proc) {
        $appPid = $proc.Id
        Write-Host "  Found running app (PID $appPid)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] No running app found. Use -Launch to start one." -ForegroundColor Red
        exit 1
    }
}

# Dismiss update dialog if present (try clicking Skip/Close)
$autoWin = Get-AutomationWindow -ProcessId $appPid
if ($autoWin) {
    Click-UIButton -Parent $autoWin -ButtonName "Skip This Version" | Out-Null
    Click-UIButton -Parent $autoWin -ButtonName "Remind Me Later" | Out-Null
    Start-Sleep -Seconds 1
    # Re-find window after dialog dismiss
    $autoWin = Get-AutomationWindow -ProcessId $appPid
}

# Determine number of pages to capture
$pagesToCapture = if ($CaptureAll) { 6 } else { $Pages }

Write-Host ""
Write-Host "  Capturing $pagesToCapture page(s)..." -ForegroundColor Cyan

for ($i = 0; $i -lt $pagesToCapture; $i++) {
    $pageName = "page-$i"
    $outFile = Join-Path $OutputDir "$timestamp-$pageName.png"

    Capture-AppWindow -Label "Page $i" -OutputPath $outFile -ProcessId $appPid

    # Navigate to next page (if not the last one we want)
    if ($i -lt ($pagesToCapture - 1)) {
        $autoWin = Get-AutomationWindow -ProcessId $appPid
        if ($autoWin) {
            $clicked = Click-UIButton -Parent $autoWin -ButtonName "Next"
            if (-not $clicked) {
                $clicked = Click-UIButton -Parent $autoWin -ButtonName "Onboarding_Next"
                if (-not $clicked) {
                    Write-Host "    Could not find Next button, stopping" -ForegroundColor Yellow
                    break
                }
            }
        }
    }
}

Write-Host ""
Write-Host "  Screenshots saved to: $OutputDir" -ForegroundColor Green
Write-Host "  Files:" -ForegroundColor Gray
Get-ChildItem $OutputDir -Filter "$timestamp-*.png" | ForEach-Object {
    Write-Host "    $($_.Name)" -ForegroundColor Gray
}
Write-Host ""
