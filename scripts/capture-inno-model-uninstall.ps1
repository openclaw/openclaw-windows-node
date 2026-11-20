#requires -Version 5.1
<#
.SYNOPSIS
    Capture real installed Inno uninstall UI on an exclusive disposable CI desktop.
.DESCRIPTION
    Uses full production/Dev installers, official hash-pinned Kokoro v1.1 assets,
    process-bound UIA, cropped window PNGs and live ffmpeg desktop recordings.
    Before: ordinary uninstall, keep Gateway/models. After: cancel, keep, reinstall
    the same owned payload, select/remove models. Never silently uninstall, launch
    Companion, remove Gateway/WSL, or sweep profiles. CI host disposal owns cleanup.
    Run with Windows PowerShell 5.1 -STA for the in-box UIAutomation/Drawing assemblies.
    ConfirmDisposableHost asserts an authorized exclusive unlocked Windows desktop
    and disposable account with synthetic data only. Not for developer workstations.
    Source/build attribution is supplied by the caller; actual installer/payload
    hashes are recorded. This is unsigned Dev behavior, not signed acceptance.
.EXAMPLE
    .\scripts\capture-inno-model-uninstall.ps1 -BeforeInstaller C:\inputs\released.exe `
      -AfterInstaller C:\inputs\candidate-Dev.exe -BeforeSourceRevision <40-hex-sha> `
      -AfterSourceRevision <40-hex-sha> -BeforeBuildIdentity 'release v2026.11.3' `
      -AfterBuildIdentity 'CI run 123, full Dev publish' -OutputDirectory C:\proof\uninstall `
      -ConfirmDisposableHost
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BeforeInstaller,
    [Parameter(Mandatory)][string]$AfterInstaller,
    [Parameter(Mandatory)][Alias('BeforeRevision')][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$BeforeSourceRevision,
    [Parameter(Mandatory)][Alias('AfterRevision')][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$AfterSourceRevision,
    [ValidateNotNullOrEmpty()][string]$BeforeBuildIdentity = 'Official production release (caller-verified asset)',
    [ValidateNotNullOrEmpty()][string]$AfterBuildIdentity = 'Full locally built unsigned Dev installer',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BeforeReleaseUrl,
    [switch]$ConfirmDisposableHost
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ConfirmDisposableHost) { throw 'Pass -ConfirmDisposableHost only for an authorized exclusive disposable CI desktop.' }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This capture is restricted to a fresh GitHub-hosted disposable runner.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Native Windows is required. No proof was captured.' }
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Use powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File for the in-box native UI assemblies.'
}
if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
    throw 'Refusing a noninteractive/session-0 desktop.'
}

# Named configurable hang guards, never readiness delays or blind retries.
function Get-Limit([string]$Name, [int]$Default, [int]$Maximum) {
    $value = [Environment]::GetEnvironmentVariable("OPENCLAW_INNO_CAPTURE_$Name")
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    $number = 0
    if (-not [int]::TryParse($value, [ref]$number) -or $number -lt 1 -or $number -gt $Maximum) {
        throw "Invalid capture limit $Name (1..$Maximum)."
    }
    return $number
}
$script:StepSeconds = Get-Limit STEP_SECONDS 300 900
$script:InstallSeconds = Get-Limit INSTALL_SECONDS 600 1800
$script:DownloadSeconds = Get-Limit DOWNLOAD_SECONDS 900 1800
$script:VideoSeconds = Get-Limit VIDEO_SECONDS 480 900
$script:StopSeconds = Get-Limit STOP_SECONDS 30 120
$script:PollMilliseconds = Get-Limit POLL_MILLISECONDS 200 2000
$script:CimSeconds = Get-Limit CIM_SECONDS 15 60
$script:Runs = [Collections.Generic.List[object]]::new()
$script:Actions = [Collections.Generic.List[object]]::new()
$script:Recording = $null
$script:Proof = [ordered]@{
    schemaVersion = 1; status = 'running'; startedUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'Real installed Inno UI on exclusive disposable Windows CI desktop'
    limits = 'Unsigned Dev behavior, not signed acceptance; no real Gateway installed or removed.'
    authority = 'ConfirmDisposableHost on github-hosted runner'; prHead = $env:PROOF_PR_HEAD; scenarios = [Collections.Generic.List[object]]::new()
    tools = [ordered]@{}; artifacts = @(); cleanup = @()
}
function Assert-OrdinaryPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:\\') {
        throw "Only fully qualified local drive paths are accepted: $Path"
    }
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected path refused: $current" }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Get-Digest([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $OutputDirectory $Name) -Encoding utf8
}
function Save-Proof { Write-Json 'proof.json' $script:Proof }
function Wait-Condition([string]$Description, [scriptblock]$Condition, [int]$Seconds = $script:StepSeconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        foreach ($action in $script:Actions) { if ($action.Error) { throw "Owned UI action failed: $($action.Error)" } }
        $value = & $Condition
        if ($value) { return $value }
        if ($clock.Elapsed.TotalSeconds -ge $Seconds) { throw "Timed out waiting for $Description; outcome unconfirmed." }
        Start-Sleep -Milliseconds $script:PollMilliseconds
    } while ($true)
}

# Process objects retain lifetimes after launcher exit. Inno's _unins.tmp must be
# an observed descendant, created during its parent's lifetime, with identical
# bytes. Missing intermediate ancestry fails closed rather than guessing by name.
function Start-OwnedProcess([string]$Path, [string[]]$Arguments, [string]$Purpose, [switch]$Uninstaller) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Executable for $Purpose does not exist: $Path"
    }
    $info = [Diagnostics.ProcessStartInfo]::new($Path)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = -not $Uninstaller
    $info.RedirectStandardOutput = -not $Uninstaller
    $info.RedirectStandardError = -not $Uninstaller
    $info.RedirectStandardInput = -not $Uninstaller
    # Same Windows quoting policy as Uninstall-DownloadedModels.ps1: reject embedded
    # quotes, surround every argument, and double trailing backslashes before closing.
    $quoted = foreach ($argument in $Arguments) {
        if ($argument.Contains('"')) { throw 'Embedded quote in process argument refused.' }
        '"' + ($argument -replace '(\\+)$', '$1$1') + '"'
    }
    $info.Arguments = $quoted -join ' '
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Process.Start returned false." }
    } catch { throw "Could not start $Purpose at ${Path}: $($_.Exception.Message)" }
    $node = [pscustomobject]@{
        Process = $process; Id = $process.Id; StartUtc = $process.StartTime.ToUniversalTime()
        Path = $Path; UiAllowed = [bool]$Uninstaller; ParentId = $PID
    }
    $run = [pscustomobject]@{
        Purpose = $Purpose; Root = $node; Nodes = @{}; Uninstaller = [bool]$Uninstaller
        ImageHash = $(if ($Uninstaller) { Get-Digest $Path } else { $null })
        Stdout = $null; Stderr = $null
    }
    $run.Nodes[$node.Id] = $node
    if (-not $Uninstaller) {
        $run.Stdout = $process.StandardOutput.ReadToEndAsync()
        $run.Stderr = $process.StandardError.ReadToEndAsync()
    }
    $script:Runs.Add($run)
    return $run
}
function Update-OwnedTree($Run) {
    $snapshot = @(Get-CimInstance Win32_Process -OperationTimeoutSec $script:CimSeconds)
    do {
        $added = $false
        foreach ($item in $snapshot) {
            $id = [int]$item.ProcessId
            if ($Run.Nodes.ContainsKey($id) -or -not $Run.Nodes.ContainsKey([int]$item.ParentProcessId)) { continue }
            $parent = $Run.Nodes[[int]$item.ParentProcessId]
            $created = $item.CreationDate.ToUniversalTime()
            if ($created -lt $parent.StartUtc) { continue }
            if ($parent.Process.HasExited -and $created -gt $parent.Process.ExitTime.ToUniversalTime()) { continue }
            $process = Get-Process -Id $id -ErrorAction SilentlyContinue
            if (-not $process -or $process.HasExited) { continue }
            # CIM and Process report the same kernel creation timestamp at different precision.
            if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $created).TotalMilliseconds) -gt 1) { continue }
            if (-not $item.ExecutablePath) { throw "Cannot verify descendant path for PID $id." }
            $path = [string]$item.ExecutablePath
            $uiAllowed = $false
            if ($Run.Uninstaller -and [IO.Path]::GetFileName($path) -ieq '_unins.tmp') {
                Assert-OrdinaryPath $path
                if (-not $path.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -or
                    (Get-Digest $path) -ne $Run.ImageHash) { throw 'Inno self-copy does not match the owned uninstaller.' }
                $uiAllowed = $true
            }
            $Run.Nodes[$id] = [pscustomobject]@{
                Process = $process; Id = $id; StartUtc = $process.StartTime.ToUniversalTime()
                Path = $path; UiAllowed = $uiAllowed; ParentId = [int]$item.ParentProcessId
            }
            $added = $true
        }
    } while ($added)
}
function Wait-ProcessExit($Run, [int]$Seconds) {
    $null = Wait-Condition "$($Run.Purpose) process-tree exit" {
        Update-OwnedTree $Run
        return @($Run.Nodes.Values | Where-Object { -not $_.Process.HasExited }).Count -eq 0
    } $Seconds
    return $Run.Root.Process.ExitCode
}
function Read-ProcessOutput($Task) {
    $null = Wait-Condition 'owned process output pipe closure' { $Task.IsCompleted } $script:StopSeconds
    return $Task.GetAwaiter().GetResult()
}
function Invoke-Native([string]$Path, [string[]]$Arguments, [string]$Purpose, [int]$Seconds = $script:StepSeconds) {
    $run = Start-OwnedProcess $Path $Arguments $Purpose
    $code = Wait-ProcessExit $run $Seconds
    $stdout = Read-ProcessOutput $run.Stdout
    $stderr = Read-ProcessOutput $run.Stderr
    if ($code -ne 0) { throw "$Purpose exited $code. $stderr" }
    return $stdout
}
function Get-RelatedRegistrations {
    foreach ($hive in @('HKCU:', 'HKLM:')) {
        foreach ($view in @('Software', 'Software\WOW6432Node')) {
            $root = "$hive\$view\Microsoft\Windows\CurrentVersion\Uninstall"
            if (-not (Test-Path -LiteralPath $root)) { continue }
            foreach ($key in Get-ChildItem -LiteralPath $root) {
                $entry = Get-ItemProperty -LiteralPath $key.PSPath
                if ($key.PSChildName -match 'M0LTB0T' -or
                    ($entry.PSObject.Properties['DisplayName'] -and $entry.DisplayName -match 'OpenClaw|Moltbot')) {
                    [pscustomobject]@{ Key = $key.PSPath; Values = $entry }
                }
            }
        }
    }
}
function Get-GatewayInventory {
    # Read the distro registry without starting WSL or provisioning it.
    $distros = @()
    $lxss = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (Test-Path -LiteralPath $lxss) {
        $distros = @(Get-ChildItem -LiteralPath $lxss | ForEach-Object {
            $p = Get-ItemProperty -LiteralPath $_.PSPath
            [pscustomobject]@{ Id = $_.PSChildName; Name = $p.DistributionName; BasePath = $p.BasePath }
        } | Sort-Object Id)
    }
    # CIM avoids loading the Appx module in pwsh. Failure is not proof of absence.
    $packages = @(Get-CimInstance -Namespace root/cimv2 -ClassName Win32_InstalledStoreProgram `
        -OperationTimeoutSec $script:CimSeconds | Where-Object { $_.Name -match 'OpenClaw|Moltbot' } |
        Select-Object Name, PackageFullName | Sort-Object Name)
    $packageKeys = @()
    $packageRoot = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages'
    if (Test-Path -LiteralPath $packageRoot) {
        $packageKeys = @(Get-ChildItem -LiteralPath $packageRoot | Where-Object {
            $_.PSChildName -match 'OpenClaw|Moltbot'
        } | Select-Object -ExpandProperty PSChildName | Sort-Object)
    }
    return [pscustomobject]@{ Distros = $distros; RelatedPackages = $packages; RelatedPackageKeys = $packageKeys }
}
function Assert-FreshHost {
    if ($env:APPDATA -ine [Environment]::GetFolderPath('ApplicationData') -or
        $env:LOCALAPPDATA -ine [Environment]::GetFolderPath('LocalApplicationData') -or
        $env:USERPROFILE -ine [Environment]::GetFolderPath('UserProfile')) {
        throw 'Profile environment differs from Windows known folders used by Inno.'
    }
    foreach ($name in @('OPENCLAW_TRAY_DATA_DIR', 'OPENCLAW_TRAY_APPDATA_DIR', 'OPENCLAW_TRAY_LOCALAPPDATA_DIR',
        'OPENCLAW_TRAY_LOCAL_DATA_DIR', 'OPENCLAW_STATE_DIR', 'OPENCLAW_WSL_DISTRO_NAME', 'OPENCLAW_NATIVE_GATEWAY_DEV_PATCH')) {
        if ([Environment]::GetEnvironmentVariable($name)) { throw "Path/runtime override refused: $name" }
    }
    $protected = @()
    foreach ($root in @($env:APPDATA, $env:LOCALAPPDATA)) {
        foreach ($name in @('OpenClawTray', 'OpenClawTray-Dev', 'OpenClawGatewayMSIX')) { $protected += Join-Path $root $name }
    }
    $protected += Join-Path $env:USERPROFILE '.openclaw'
    $protected += Join-Path $env:USERPROFILE '.openclaw-dev'
    $packageData = Join-Path $env:LOCALAPPDATA 'Packages'
    if (Test-Path -LiteralPath $packageData) {
        $protected += @(Get-ChildItem -LiteralPath $packageData -Directory -Force | Where-Object {
            $_.Name -match 'OpenClaw|Moltbot'
        } | Select-Object -ExpandProperty FullName)
    }
    foreach ($path in $protected) {
        Assert-OrdinaryPath $path
        if (Test-Path -LiteralPath $path) { throw "Existing installation/profile refused, including empty directories: $path" }
        if ($OutputDirectory.StartsWith($path.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $OutputDirectory -ieq $path) {
            throw 'Output directory overlaps a protected profile.'
        }
    }
    if (@(Get-RelatedRegistrations).Count) { throw 'Existing OpenClaw uninstall registration refused.' }
    foreach ($name in @('openclaw', 'openclaw-dev')) {
        if (Test-Path -LiteralPath "HKCU:\Software\Classes\$name") { throw 'Existing OpenClaw protocol registration refused.' }
    }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        $autoruns = Get-ItemProperty -LiteralPath $runKey
        if (@($autoruns.PSObject.Properties | Where-Object { $_.Name -match 'OpenClaw|Moltbot' }).Count) {
            throw 'Existing OpenClaw autostart registration refused.'
        }
    }
    if (@(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -match 'OpenClaw|Moltbot' }).Count) {
        throw 'Existing OpenClaw scheduled task refused.'
    }
    $processes = @(Get-CimInstance Win32_Process -OperationTimeoutSec $script:CimSeconds | Where-Object {
        $_.Name -match '^(OpenClaw|Moltbot|llama-server)' -or
        ($_.Name -match '^(node|wsl|wslhost)\.exe$' -and $_.CommandLine -match 'openclaw|moltbot')
    })
    if ($processes.Count) { throw 'Existing related processes refused. This helper will not stop them.' }
    $inventory = Get-GatewayInventory
    if ($inventory.RelatedPackages.Count -or $inventory.RelatedPackageKeys.Count -or
        @($inventory.Distros | Where-Object { $_.Name -match 'OpenClaw|Moltbot' }).Count) {
        throw 'Existing related Store/native package or WSL distro refused.'
    }
    return $inventory
}

# Real pixels and asynchronous UIA patterns, not reconstructed dialogs. Dispatch
# patterns only after binding their containing HWND to the installed uninstaller.
function Initialize-Desktop {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class InnoCaptureAction {
    public volatile bool Done;
    public Exception Error;
    public static InnoCaptureAction Start(object pattern, string method) {
        var result = new InnoCaptureAction();
        var thread = new Thread(() => {
            try { pattern.GetType().GetMethod(method).Invoke(pattern, null); }
            catch (Exception ex) { result.Error = ex; }
            finally { result.Done = true; }
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        return result;
    }
}
public static class InnoCaptureWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
    $null = [InnoCaptureWindow]::SetProcessDPIAware()
}
function Test-OwnedWindow($Run, $Window) {
    $id = $Window.Current.ProcessId
    if (-not $Run.Nodes.ContainsKey($id)) { return $false }
    $node = $Run.Nodes[$id]
    if (-not $node.UiAllowed -or $node.Process.HasExited) { return $false }
    $live = Get-Process -Id $id -ErrorAction SilentlyContinue
    return $live -and $live.StartTime.ToUniversalTime() -eq $node.StartUtc -and $live.Path -ieq $node.Path
}
function Get-OwnedWindows($Run) {
    Update-OwnedTree $Run
    $children = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $children) {
        try {
            if (-not $window.Current.IsOffscreen -and (Test-OwnedWindow $Run $window)) { $window }
        } catch [Windows.Automation.ElementNotAvailableException] { continue }
    }
}
function Get-WindowText($Window) {
    $text = [Collections.Generic.List[string]]::new()
    $text.Add($Window.Current.Name)
    foreach ($child in $Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $text.Add($child.Current.Name)
        $pattern = $null
        if ($child.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $text.Add($pattern.Current.Value) }
    }
    return $text -join "`n"
}
function Find-Control($Window, [string]$Name, $Type) {
    $matches = @($Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType -eq $Type -and ($_.Current.Name -replace '&', '') -eq $Name -and -not $_.Current.IsOffscreen })
    if ($matches.Count -gt 1) { throw "Ambiguous owned control: $Name" }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}
function Invoke-Control($Run, $Window, [string]$Name, [switch]$Toggle) {
    if (-not (Test-OwnedWindow $Run $Window)) { throw 'UI ownership changed before invocation.' }
    $type = if ($Toggle) { [Windows.Automation.ControlType]::CheckBox } else { [Windows.Automation.ControlType]::Button }
    $control = Find-Control $Window $Name $type
    if (-not $control -or -not $control.Current.IsEnabled) { throw "Owned control is not ready: $Name" }
    $patternType = if ($Toggle) { [Windows.Automation.TogglePattern]::Pattern } else { [Windows.Automation.InvokePattern]::Pattern }
    $pattern = $control.GetCurrentPattern($patternType)
    $action = [InnoCaptureAction]::Start($pattern, $(if ($Toggle) { 'Toggle' } else { 'Invoke' }))
    $script:Actions.Add($action)
}
function Wait-Window($Run, [scriptblock]$Predicate, [string]$Description) {
    return Wait-Condition $Description {
        foreach ($window in @(Get-OwnedWindows $Run)) {
            try { if (& $Predicate $window) { return $window } }
            catch [Windows.Automation.ElementNotAvailableException] { continue }
        }
        return $null
    }
}
function Save-Window($Run, $Window, [string]$Name) {
    if (-not (Test-OwnedWindow $Run $Window)) { throw 'Cannot capture a foreign window.' }
    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $null = [InnoCaptureWindow]::SetForegroundWindow($handle)
    $state = @{ Last = ''; Count = 0; Rect = $null }
    $null = Wait-Condition 'stable owned foreground window bounds' {
        if ([InnoCaptureWindow]::GetForegroundWindow() -ne $handle -or -not (Test-OwnedWindow $Run $Window)) { return $false }
        $rect = [InnoCaptureWindow+Rect]::new()
        if (-not [InnoCaptureWindow]::GetWindowRect($handle, [ref]$rect)) { return $false }
        $key = "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
        if ($state.Last -eq $key) { $state.Count++ } else { $state.Count = 0; $state.Last = $key }
        $state.Rect = $rect
        return $state.Count -ge 2 -and $rect.Right -gt $rect.Left -and $rect.Bottom -gt $rect.Top
    }
    $r = $state.Rect
    $bounds = [Windows.Forms.SystemInformation]::VirtualScreen
    if ($r.Left -lt $bounds.Left -or $r.Top -lt $bounds.Top -or $r.Right -gt $bounds.Right -or $r.Bottom -gt $bounds.Bottom) {
        throw 'Owned window is clipped by the desktop. Increase the CI desktop resolution.'
    }
    $bitmap = [Drawing.Bitmap]::new(($r.Right - $r.Left), ($r.Bottom - $r.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($r.Left, $r.Top, 0, 0, $bitmap.Size)
        if ([InnoCaptureWindow]::GetForegroundWindow() -ne $handle) { throw 'Foreground changed during capture.' }
        $bitmap.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Json "$Name.window.json" @{
        processId = $Window.Current.ProcessId; hwnd = $handle.ToInt64(); text = Get-WindowText $Window
        bounds = @{ left = $r.Left; top = $r.Top; width = $r.Right - $r.Left; height = $r.Bottom - $r.Top }
    }
}
function Get-VideoFrames {
    if (-not $script:Recording -or -not (Test-Path -LiteralPath $script:Recording.Progress)) { return 0 }
    $frames = @(Get-Content -LiteralPath $script:Recording.Progress | Where-Object { $_ -match '^frame=\d+$' })
    if (-not $frames.Count) { return 0 }
    return [int]($frames[-1] -replace '^frame=', '')
}
function Start-Recording([string]$Name) {
    if ($script:Recording) { throw 'A capture is already active.' }
    $path = Join-Path $OutputDirectory "$Name.mp4"
    $progress = Join-Path $script:WorkDirectory "$Name.ffmpeg-progress.txt"
    $run = Start-OwnedProcess $script:Ffmpeg @(
        '-hide_banner', '-loglevel', 'warning', '-f', 'gdigrab', '-framerate', '12', '-draw_mouse', '0',
        '-i', 'desktop', '-t', "$script:VideoSeconds", '-vf', 'pad=ceil(iw/2)*2:ceil(ih/2)*2', '-c:v', 'libx264',
        '-preset', 'veryfast', '-crf', '28', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-progress', $progress, $path
    ) "record-$Name"
    $script:Recording = [pscustomobject]@{ Run = $run; Path = $path; Progress = $progress; Name = $Name }
    $null = Wait-Condition 'first live desktop video frames' {
        if ($run.Root.Process.HasExited) { throw "ffmpeg exited early: $(Read-ProcessOutput $run.Stderr)" }
        return (Get-VideoFrames) -gt 0
    }
}
function Hold-RecordedState($Run, $Window) {
    # Deliberate presentation dwell measured in live encoded frames, not a sleep
    # that substitutes for readiness. Verify ownership/foreground throughout.
    $startFrame = Get-VideoFrames
    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $null = [InnoCaptureWindow]::SetForegroundWindow($handle)
    $null = Wait-Condition '24 live frames of the verified UI state' {
        if ($script:Recording.Run.Root.Process.HasExited) { throw 'Video ended before the scenario completed.' }
        if (-not (Test-OwnedWindow $Run $Window) -or [InnoCaptureWindow]::GetForegroundWindow() -ne $handle) {
            throw 'Owned UI lost foreground during its recording segment.'
        }
        return (Get-VideoFrames) -ge ($startFrame + 24)
    }
}
function Stop-Recording {
    if (-not $script:Recording) { return }
    $recording = $script:Recording
    $script:Recording = $null
    $run = $recording.Run
    $endedEarly = $run.Root.Process.HasExited
    if (-not $endedEarly) { $run.Root.Process.StandardInput.WriteLine('q'); $run.Root.Process.StandardInput.Flush() }
    $code = Wait-ProcessExit $run $script:StopSeconds
    Read-ProcessOutput $run.Stderr | Set-Content -LiteralPath (Join-Path $script:WorkDirectory "$($recording.Name).ffmpeg.log")
    if ($endedEarly -or $code -ne 0) { throw 'Recording incomplete or ended before the scenario.' }
    $probe = Invoke-Native $script:Ffprobe @('-v', 'error', '-count_frames', '-select_streams', 'v:0',
        '-show_entries', 'stream=codec_name,width,height,nb_read_frames:format=duration,size', '-of', 'json', $recording.Path) 'validate-video'
    $parsed = $probe | ConvertFrom-Json
    if ([double]$parsed.format.duration -le 0 -or [int]$parsed.streams[0].nb_read_frames -lt 24) { throw 'MP4 has no meaningful decoded live frames.' }
    $probe | Set-Content -LiteralPath (Join-Path $OutputDirectory "$($recording.Name).video.json")
    if ((Get-Item -LiteralPath $recording.Path).Length -gt 10000000) {
        throw 'Recording exceeds 10 MB. Preserve native-resolution text readability when preparing delivery.'
    }
    if ($recording.Name -eq 'after-remove') {
        # Six genuine decoded frames from the live video, not frames used to make
        # the video. The sheet is an inspection aid, not a substitute recording.
        $duration = [double]$parsed.format.duration
        $frameFiles = @()
        $samples = @()
        for ($index = 0; $index -lt 6; $index++) {
            $timestamp = (($index + 0.5) * $duration / 6).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
            $frame = Join-Path $OutputDirectory ("after-remove-frame-{0:D2}.png" -f ($index + 1))
            $null = Invoke-Native $script:Ffmpeg @('-hide_banner', '-loglevel', 'error', '-ss', $timestamp,
                '-i', $recording.Path, '-frames:v', '1', '-update', '1', $frame) 'extract-live-video-frame'
            $frameFiles += $frame
            $samples += @{ file = [IO.Path]::GetFileName($frame); seconds = $timestamp }
        }
        $first = [Drawing.Image]::FromFile($frameFiles[0])
        $width = $first.Width; $height = $first.Height; $first.Dispose()
        $sheet = [Drawing.Bitmap]::new(($width * 3), ($height * 2))
        $graphics = [Drawing.Graphics]::FromImage($sheet)
        try {
            for ($index = 0; $index -lt 6; $index++) {
                $image = [Drawing.Image]::FromFile($frameFiles[$index])
                try { $graphics.DrawImage($image, (($index % 3) * $width), ([int][Math]::Floor($index / 3) * $height), $width, $height) }
                finally { $image.Dispose() }
            }
            $sheet.Save((Join-Path $OutputDirectory 'after-remove-contact-sheet.png'), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $sheet.Dispose() }
        Write-Json 'after-remove-frame-samples.json' $samples
    }
}

# Independent scenario pins must match the production catalog. No synthetic model,
# extracted Pascal function, helper replacement or UI-only stub is admissible.
$script:Assets = @(
    @{ FileName = 'kokoro-v1.1-zh.onnx'; Size = 343605188L; Sha256 = 'eefec708cbc7aba8e8129b5c2f7cb92e1fe7d281af1e1dd451592d9ff0714a0d' },
    @{ FileName = 'voices-v1.1-zh.bin'; Size = 53792177L; Sha256 = 'e678019845e6cfe3b7c34531779396b28f509451b91e6535d5dc09bbf11a4be5' }
)
$script:ExpectedBytes = 397397365L
function Get-OfficialAssets {
    $catalogPath = Join-Path $PSScriptRoot '..\src\OpenClaw.Shared\Audio\KokoroDownloadCatalog.cs'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw
    $baseUrl = 'https://github.com/larroy/kokoro.cpp/releases/download/voices_model_files/'
    if (-not $catalog.Contains($baseUrl)) { throw 'Source catalog no longer has the pinned official release URL.' }
    $cache = Join-Path $script:WorkDirectory 'input-assets'
    $null = New-Item -ItemType Directory -Path $cache
    foreach ($asset in $script:Assets) {
        if (-not $catalog.Contains($asset.FileName) -or -not $catalog.Contains($asset.Sha256) -or -not $catalog.Contains([string]$asset.Size)) {
            throw 'Pinned media scenario does not match the production download catalog.'
        }
        $asset.Url = $baseUrl + $asset.FileName
        $asset.Path = Join-Path $cache $asset.FileName
        Invoke-WebRequest -UseBasicParsing -Uri $asset.Url -OutFile $asset.Path -TimeoutSec $script:DownloadSeconds
        if ((Get-Item -LiteralPath $asset.Path).Length -ne $asset.Size -or (Get-Digest $asset.Path) -ne $asset.Sha256) {
            throw "Official asset size/hash mismatch: $($asset.FileName)"
        }
    }
    $script:Proof.assets = @($script:Assets | ForEach-Object { @{ file = $_.FileName; url = $_.Url; sha256 = $_.Sha256; bytes = $_.Size } })
}
function Get-Inventory([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    Assert-OrdinaryPath $Root
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        Assert-OrdinaryPath $_.FullName
        [pscustomobject]@{ path = $_.FullName.Substring($prefix.Length); bytes = $_.Length; sha256 = Get-Digest $_.FullName }
    })
}
function Seed-Profile($Install) {
    Assert-OrdinaryPath $Install.Profile
    if (Test-Path -LiteralPath $Install.Profile) { throw 'Profile unexpectedly exists before seed; refusing adoption.' }
    $models = Join-Path $Install.Profile 'models\kokoro\v1.1-en-zh'
    $null = New-Item -ItemType Directory -Path $models
    foreach ($asset in $script:Assets) { Copy-Item -LiteralPath $asset.Path -Destination (Join-Path $models $asset.FileName) }
    # Unrelated sibling and settings have exact preservation assertions. No credentials.
    Set-Content -LiteralPath (Join-Path $models 'unrelated-sentinel.txt') -Value 'Synthetic PR2328 sentinel. Keep this file.' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $Install.Profile 'settings.json') -Value '{"EnableNodeMode":false,"EnableMcpServer":false,"AutoStart":false}' -Encoding utf8
    $Install.Seeded = @(Get-Inventory $Install.Profile)
    Write-Json "$($Install.Label)-seeded-files.json" $Install.Seeded
}
function Assert-Profile($Install, [switch]$Removed, [string]$Scenario) {
    $inventory = @(Get-Inventory $Install.Profile)
    Write-Json "$Scenario-files.json" $inventory
    foreach ($expected in $Install.Seeded) {
        $model = @($script:Assets | Where-Object { $expected.path -eq "models\kokoro\v1.1-en-zh\$($_.FileName)" }).Count -gt 0
        $actual = @($inventory | Where-Object { $_.path -eq $expected.path })
        if ($Removed -and $model) {
            if ($actual.Count) { throw "Selected model retained: $($expected.path)" }
        } elseif ($actual.Count -ne 1 -or $actual[0].sha256 -ne $expected.sha256 -or $actual[0].bytes -ne $expected.bytes) {
            throw "Retained file changed or disappeared: $($expected.path)"
        }
    }
}
function Install-FullPayload([string]$Installer, [string]$Label, [switch]$Dev, $Previous) {
    $name = if ($Dev) { 'OpenClawTray-Dev' } else { 'OpenClawTray' }
    $root = Join-Path $env:LOCALAPPDATA $name
    $profile = Join-Path $env:APPDATA $name
    Assert-OrdinaryPath $root
    if (Test-Path -LiteralPath $root) {
        # Only this run's proven keep-uninstall permits reinstallation. No removal
        # of leftovers to make an installation appear clean.
        if (-not $Previous -or $Previous.Root -ine $root -or -not $Previous.Uninstalled) { throw 'Install directory already exists.' }
        if (@(Get-Inventory $root).Count) { throw 'Unexpected installed files remain before candidate reinstall.' }
    }
    if (@(Get-RelatedRegistrations | Where-Object {
        $_.Values.PSObject.Properties['InstallLocation'] -and $_.Values.InstallLocation.TrimEnd('\') -ieq $root
    }).Count) { throw 'Target uninstall registration already exists.' }
    $log = Join-Path $script:WorkDirectory "$Label-install.log"
    $run = Start-OwnedProcess $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=', "/LOG=$log") "install-$Label"
    $code = Wait-ProcessExit $run $script:InstallSeconds
    if ($code -ne 0) { throw "Installer exited $code. See $log. No retry or adoption attempted." }
    $expectedIdentity = if ($Dev) { 'dev' } else { 'release' }
    foreach ($relative in @('app-identity.txt', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll',
        'OpenClaw.Tray.WinUI.deps.json', 'OpenClaw.Tray.WinUI.runtimeconfig.json', 'Microsoft.UI.Xaml.dll')) {
        $path = Join-Path $root $relative
        Assert-OrdinaryPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Full production-shaped payload missing: $relative" }
    }
    if ((Get-Content -LiteralPath (Join-Path $root 'app-identity.txt') -Raw).Trim() -cne $expectedIdentity) { throw 'Installed identity mismatch.' }
    $appAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $root 'OpenClaw.Tray.WinUI.dll'))
    if ($appAssembly.Name -ne 'OpenClaw.Tray.WinUI') { throw 'Invalid Companion assembly; stub payload refused.' }
    $entries = @(Get-RelatedRegistrations | Where-Object {
        $_.Values.PSObject.Properties['InstallLocation'] -and $_.Values.InstallLocation.TrimEnd('\') -ieq $root
    })
    if ($entries.Count -ne 1) { throw 'Could not bind one exact installed Inno uninstall registration.' }
    $command = [string]$entries[0].Values.UninstallString
    # Accept exactly a path, optionally surrounded by one matched quote pair. No
    # arguments, shell expansion, registry command execution or guessed fallback.
    $uninstaller = $command
    if ($command.StartsWith('"') -and $command.EndsWith('"')) { $uninstaller = $command.Substring(1, $command.Length - 2) }
    if ($uninstaller.Contains('"') -or [IO.Path]::GetFileName($uninstaller) -notmatch '^unins[0-9]+\.exe$') {
        throw "Uncertain uninstaller command refused: $command"
    }
    if ([IO.Path]::GetDirectoryName($uninstaller) -ine $root) { throw 'Uninstaller is outside the verified install root.' }
    Assert-OrdinaryPath $uninstaller
    if ($Dev) {
        foreach ($relative in @('Uninstall-DownloadedModels.ps1', 'KokoroDownloadCatalog.cs', 'KokoroUninstallFiles.cs')) {
            $source = if ($relative -like '*.cs') { Join-Path $PSScriptRoot "..\src\OpenClaw.Shared\Audio\$relative" } else { Join-Path $PSScriptRoot $relative }
            if ((Get-Digest (Join-Path $root $relative)) -ne (Get-Digest $source)) { throw "Installed helper differs from current source: $relative" }
        }
    }
    $payload = @(Get-Inventory $root)
    Write-Json "$Label-installed-files.json" $payload
    if (@(Get-CimInstance Win32_Process -OperationTimeoutSec $script:CimSeconds | Where-Object {
        $_.ExecutablePath -ieq (Join-Path $root 'OpenClaw.Tray.WinUI.exe')
    }).Count) { throw 'Silent installation unexpectedly launched Companion.' }
    return [pscustomobject]@{
        Label = $Label; Root = $root; Profile = $profile; Uninstaller = $uninstaller
        UninstallerHash = Get-Digest $uninstaller; RegistryKey = $entries[0].Key; Seeded = @()
        Uninstalled = $false; Payload = $payload; AssemblyVersion = $appAssembly.Version.ToString()
        ProductVersion = (Get-Item -LiteralPath (Join-Path $root 'OpenClaw.Tray.WinUI.exe')).VersionInfo.ProductVersion
    }
}
function Start-Uninstall($Install, [string]$Scenario) {
    if ((Get-Digest $Install.Uninstaller) -ne $Install.UninstallerHash) { throw 'Installed uninstaller changed.' }
    # NEVER /SILENT or /VERYSILENT: real silent uninstall defaults Gateway cleanup
    # to destructive removal. Every interactive Gateway choice below is No.
    return Start-OwnedProcess $Install.Uninstaller @('/NORESTART', "/LOG=$(Join-Path $script:WorkDirectory "$Scenario-uninstall.log")") $Scenario -Uninstaller
}
function Wait-ModelDialog($Run) {
    return Wait-Window $Run { param($w)
        $w.Current.Name -eq 'Downloaded voice models' -and
        $null -ne (Find-Control $w 'Check downloaded models' ([Windows.Automation.ControlType]::Button))
    } 'actual Downloaded voice models choice dialog'
}
function Assert-ReportedBytes([string]$Text, [string]$Label, [long]$Expected) {
    $match = [regex]::Match($Text, [regex]::Escape($Label) + '\s*([0-9, .\u00a0\u202f]+) bytes')
    if (-not $match.Success -or [long]($match.Groups[1].Value -replace '[^0-9]', '') -ne $Expected) {
        throw "UI measured total differs from independently seeded bytes: $Label"
    }
}
function Check-Models($Run, [string]$CaptureName) {
    $window = Wait-ModelDialog $Run
    $box = Find-Control $window 'Remove the verified Kokoro downloads listed below' ([Windows.Automation.ControlType]::CheckBox)
    if (-not $box -or $box.Current.IsEnabled -or
        $box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw 'Removal must initially be disabled and unchecked.'
    }
    Invoke-Control $Run $window 'Check downloaded models'
    $window = Wait-Window $Run { param($w)
        if ($w.Current.Name -ne 'Downloaded voice models') { return $false }
        $choice = Find-Control $w 'Remove the verified Kokoro downloads listed below' ([Windows.Automation.ControlType]::CheckBox)
        $continue = Find-Control $w 'Continue uninstall' ([Windows.Automation.ControlType]::Button)
        if (-not $choice -or -not $choice.Current.IsEnabled -or -not $continue -or -not $continue.Current.IsEnabled) { return $false }
        return (Get-WindowText $w) -match 'Verified downloads available for selection:'
    } 'completed catalog scan and enabled unchecked selection'
    $box = Find-Control $window 'Remove the verified Kokoro downloads listed below' ([Windows.Automation.ControlType]::CheckBox)
    if ($box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw 'Model scan selected destructive removal automatically.'
    }
    $text = Get-WindowText $window
    Assert-ReportedBytes $text 'Verified downloads available for selection:' $script:ExpectedBytes
    Assert-ReportedBytes $text 'Measured retained catalog files:' $script:ExpectedBytes
    foreach ($asset in $script:Assets) { if (-not $text.Contains($asset.FileName)) { throw 'Measured file missing from model UI.' } }
    Save-Window $Run $window $CaptureName
    Hold-RecordedState $Run $window
    return $window
}
function Complete-Uninstall($Install, $Run, [string]$Scenario, [switch]$Before, [switch]$RemoveModels) {
    $state = @{ Confirmed = $false; Gateway = 'not encountered'; Summary = $false; Seen = @{} }
    $null = Wait-Condition "$Scenario real uninstall completion" {
        foreach ($window in @(Get-OwnedWindows $Run)) {
            $text = Get-WindowText $window
            $key = "$($window.Current.NativeWindowHandle):$text"
            if ($state.Seen.ContainsKey($key)) { continue }
            $button = [Windows.Automation.ControlType]::Button
            if ($text -match 'Do you also want to remove the OpenClaw local WSL gateway\?') {
                Save-Window $Run $window "$Scenario-gateway-keep"
                Invoke-Control $Run $window 'No'
                $state.Gateway = 'No'; $state.Seen[$key] = $true
            } elseif ($text -match 'Are you sure you want to completely remove') {
                if ($Before) { Save-Window $Run $window 'before' }
                Invoke-Control $Run $window 'Yes'
                $state.Confirmed = $true; $state.Seen[$key] = $true
            } elseif ($window.Current.Name -eq 'Downloaded voice models' -and
                $null -ne (Find-Control $window 'Close' $button)) {
                if ($Before) { throw 'Baseline already contains the changed model dialog. Use a pre-change release.' }
                if ($RemoveModels) { Assert-ReportedBytes $text 'Removed model file bytes:' $script:ExpectedBytes }
                elseif ($text -notmatch 'No model removal was requested') { throw 'Keep summary does not confirm no model removal.' }
                Save-Window $Run $window "$Scenario-summary"
                Hold-RecordedState $Run $window
                Invoke-Control $Run $window 'Close'
                $state.Summary = $true; $state.Seen[$key] = $true
            } elseif ($text -match '(?s)(could not (confirm whether your OpenClaw data was migrated|check whether OpenClaw from the Microsoft Store)|OpenClaw from the Microsoft Store is installed).*gateway.*(left in place|preserv)') {
                Save-Window $Run $window "$Scenario-gateway-preserved-warning"
                Invoke-Control $Run $window 'OK'
                $state.Gateway = 'preserved by installer warning'; $state.Seen[$key] = $true
            } elseif ($text -match 'successfully removed from your computer') {
                Invoke-Control $Run $window 'OK'; $state.Seen[$key] = $true
            }
            # Unknown dialogs are never clicked; timeout retains the failed receipt.
        }
        return @($Run.Nodes.Values | Where-Object { -not $_.Process.HasExited }).Count -eq 0
    }
    if (-not $state.Confirmed -or (-not $Before -and -not $state.Summary)) { throw 'Required confirmation/summary not observed.' }
    if ($state.Gateway -eq 'not encountered') { throw 'Gateway preservation not confirmed by real UI.' }
    if (Test-Path -LiteralPath $Install.RegistryKey) { throw 'Uninstall registration remains.' }
    if (Test-Path -LiteralPath (Join-Path $Install.Root 'OpenClaw.Tray.WinUI.exe')) { throw 'Companion payload remains after uninstall.' }
    foreach ($node in $Run.Nodes.Values | Where-Object UiAllowed) {
        if ($node.Process.ExitCode -ne 0) { throw "Uninstaller PID $($node.Id) exited $($node.Process.ExitCode)." }
    }
    $Install.Uninstalled = $true
    Assert-Profile $Install -Removed:$RemoveModels -Scenario $Scenario
    $script:Proof.scenarios.Add(@{
        name = $Scenario; outcome = 'passed'; gateway = $state.Gateway; modelBytes = $script:ExpectedBytes
        models = $(if ($RemoveModels) { 'removed' } else { 'retained, hashes unchanged' })
        uninstallerSha256 = $Install.UninstallerHash; install = $Install.Root; profile = $Install.Profile
        processes = @($Run.Nodes.Values | ForEach-Object { @{ id = $_.Id; parent = $_.ParentId; path = $_.Path; startUtc = $_.StartUtc; uiAllowed = $_.UiAllowed } })
    })
    Save-Proof
}

$outputReady = $false
try {
    Assert-OrdinaryPath $OutputDirectory
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must not exist. Preserve earlier evidence; choose a new directory.' }
    foreach ($path in @($BeforeInstaller, $AfterInstaller)) {
        Assert-OrdinaryPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [IO.Path]::GetExtension($path) -ine '.exe') { throw 'Supply exact full Inno EXE paths.' }
    }
    $BeforeInstaller = (Resolve-Path -LiteralPath $BeforeInstaller).Path
    $AfterInstaller = (Resolve-Path -LiteralPath $AfterInstaller).Path
    if ($BeforeInstaller -ieq $AfterInstaller -or (Get-Digest $BeforeInstaller) -eq (Get-Digest $AfterInstaller)) { throw 'Before and after installers must differ.' }
    $null = New-Item -ItemType Directory -Path $OutputDirectory
    $outputReady = $true
    # Task-owned non-public inputs/logs stay outside the media artifact directory.
    # Only PNG/MP4 and sanitized JSON evidence are suitable for artifact delivery.
    $script:WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-inno-media-" + [Guid]::NewGuid().ToString('N'))
    Assert-OrdinaryPath $script:WorkDirectory
    $null = New-Item -ItemType Directory -Path $script:WorkDirectory
    $script:Proof.diagnosticsDirectory = $script:WorkDirectory
    $script:Proof.before = @{ installer = $BeforeInstaller; sha256 = Get-Digest $BeforeInstaller; sourceRevision = $BeforeSourceRevision; buildIdentity = $BeforeBuildIdentity; releaseUrl = $BeforeReleaseUrl; provenance = 'caller-supplied release/source attribution; actual hash recorded' }
    $script:Proof.after = @{ installer = $AfterInstaller; sha256 = Get-Digest $AfterInstaller; sourceRevision = $AfterSourceRevision; buildIdentity = $AfterBuildIdentity; identity = 'full locally built unsigned Dev installer' }
    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $head = (Invoke-Native $git @('-C', (Join-Path $PSScriptRoot '..'), 'rev-parse', 'HEAD') 'source-revision').Trim()
    if ($head -cne $AfterSourceRevision.ToLowerInvariant()) { throw 'AfterSourceRevision does not match capture checkout.' }
    $script:Proof.captureScriptSha256 = Get-Digest $PSCommandPath
    # Discover WinApp before native inspection. UIA is retained for Inno self-copy
    # lifetime ownership; never invent version-specific WinApp selectors or flags.
    $winapp = Get-Command winapp -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winapp) {
        $script:Proof.tools.winapp = (Invoke-Native $winapp.Source @('--version') 'winapp-version').Trim()
        $schema = Invoke-Native $winapp.Source @('--cli-schema') 'winapp-schema'
        $schema | Set-Content -LiteralPath (Join-Path $OutputDirectory 'winapp-cli-schema.json')
        $script:Proof.tools.ui = 'WinApp discovered; process-bound UIAutomation for Inno self-copy windows; ffmpeg live desktop.'
    } else {
        $script:Proof.tools.winapp = 'absent; no installation attempted'
        $script:Proof.tools.ui = 'UIAutomationClient/InvokePattern scoped to Inno descendants; CopyFromScreen window crop.'
    }
    Save-Proof
    $script:Ffmpeg = (Get-Command ffmpeg -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $script:Ffprobe = (Get-Command ffprobe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $script:Proof.tools.ffmpeg = (Invoke-Native $script:Ffmpeg @('-version') 'ffmpeg-version').Split("`n")[0]
    $gatewayBefore = Assert-FreshHost
    Write-Json 'gateway-before.json' $gatewayBefore
    Initialize-Desktop
    Get-OfficialAssets
    Save-Proof

    $baseline = Install-FullPayload $BeforeInstaller 'before'
    Seed-Profile $baseline
    $script:Proof.before.installedProductVersion = $baseline.ProductVersion
    $script:Proof.before.installedAssemblyVersion = $baseline.AssemblyVersion
    $run = Start-Uninstall $baseline 'before-keep'
    Complete-Uninstall $baseline $run 'before-keep' -Before

    $candidate = Install-FullPayload $AfterInstaller 'after' -Dev
    Seed-Profile $candidate
    $script:Proof.after.installedProductVersion = $candidate.ProductVersion
    $script:Proof.after.installedAssemblyVersion = $candidate.AssemblyVersion

    # Cancel after a completed read-only preview. Do not claim this tests the
    # separate in-flight scan-cancellation race.
    $run = Start-Uninstall $candidate 'after-cancel'
    $window = Wait-ModelDialog $run
    Start-Recording 'after-cancel'
    $window = Check-Models $run 'after'
    Invoke-Control $run $window 'Cancel'
    $null = Wait-ProcessExit $run $script:StepSeconds
    Stop-Recording
    if (-not (Test-Path -LiteralPath $candidate.RegistryKey) -or
        (Get-Digest $candidate.Uninstaller) -ne $candidate.UninstallerHash) { throw 'Cancel changed the installed application.' }
    $cancelPayload = @(Get-Inventory $candidate.Root)
    Write-Json 'after-cancel-installed-files.json' $cancelPayload
    if (($candidate.Payload | ConvertTo-Json -Depth 4 -Compress) -cne ($cancelPayload | ConvertTo-Json -Depth 4 -Compress)) {
        throw 'Cancel changed installed payload files.'
    }
    Assert-Profile $candidate -Scenario 'after-cancel'
    $script:Proof.scenarios.Add(@{ name = 'after-cancel'; outcome = 'passed'; models = 'retained, hashes unchanged'; application = 'retained'; boundary = 'Cancel after completed preview; not mid-scan cancellation' })
    Save-Proof

    $run = Start-Uninstall $candidate 'after-keep'
    $window = Wait-ModelDialog $run
    Start-Recording 'after-keep'
    $window = Check-Models $run 'after-keep-preview'
    Invoke-Control $run $window 'Continue uninstall'
    Complete-Uninstall $candidate $run 'after-keep'
    Stop-Recording

    $reinstalled = Install-FullPayload $AfterInstaller 'after-reinstall' -Dev -Previous $candidate
    $reinstalled.Seeded = $candidate.Seeded
    Assert-Profile $reinstalled -Scenario 'after-reinstall'
    $run = Start-Uninstall $reinstalled 'after-remove'
    $window = Wait-ModelDialog $run
    Start-Recording 'after-remove'
    $window = Check-Models $run 'after-remove-preview'
    Invoke-Control $run $window 'Remove the verified Kokoro downloads listed below' -Toggle
    $window = Wait-Window $run { param($w)
        $box = Find-Control $w 'Remove the verified Kokoro downloads listed below' ([Windows.Automation.ControlType]::CheckBox)
        return $box -and $box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    } 'explicitly checked model removal'
    Save-Window $run $window 'after-remove-selected'
    Hold-RecordedState $run $window
    Invoke-Control $run $window 'Continue uninstall'
    Complete-Uninstall $reinstalled $run 'after-remove' -RemoveModels
    Stop-Recording

    Assert-Profile $baseline -Scenario 'before-final-preservation'
    $gatewayAfter = Get-GatewayInventory
    Write-Json 'gateway-after.json' $gatewayAfter
    if (($gatewayBefore | ConvertTo-Json -Depth 6 -Compress) -cne ($gatewayAfter | ConvertTo-Json -Depth 6 -Compress)) {
        throw 'Gateway/WSL package inventory changed.'
    }
    $null = Wait-Condition 'owned UI actions to return' { @($script:Actions | Where-Object { -not $_.Done }).Count -eq 0 } $script:StopSeconds
    foreach ($required in @('before.png', 'after.png', 'after-remove.mp4', 'after-remove-summary.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Required evidence missing: $required" }
    }
    $script:Proof.status = 'passed; media requires visual review'
} catch {
    $script:Proof.status = 'failed'
    $script:Proof.failure = $_.Exception.Message
    throw
} finally {
    if ($script:Recording) {
        try { Stop-Recording } catch { $script:Proof.cleanup += "Recording incomplete: $($_.Exception.Message)" }
    }
    # No blanket name-based stopping, recursive profile deletion, WSL unregister,
    # or silent uninstall. Preserve leftovers for diagnosis and CI host disposal.
    foreach ($run in $script:Runs) {
        foreach ($node in @($run.Nodes.Values | Sort-Object StartUtc -Descending)) {
            try {
                if ($node.Process.HasExited) { continue }
                $live = Get-Process -Id $node.Id -ErrorAction SilentlyContinue
                if (-not $live -or $live.StartTime.ToUniversalTime() -ne $node.StartUtc -or $live.Path -ine $node.Path) {
                    $script:Proof.cleanup += "PID $($node.Id) no longer provably owned; not stopped."; continue
                }
                if ($live.ProcessName -match '^(wsl|wslhost|node|OpenClaw\.Tray\.WinUI|openclaw-wsl-keepalive|llama-server)$') {
                    $script:Proof.cleanup += "Protected process $($node.Id) left untouched; host disposal required."; continue
                }
                $live.Kill()
                if (-not $live.WaitForExit($script:StopSeconds * 1000)) { throw 'Owned process stop unconfirmed.' }
                $script:Proof.cleanup += "Stopped owned $($run.Purpose) PID $($node.Id)."
            } catch { $script:Proof.cleanup += "Owned-process cleanup incomplete: $($_.Exception.Message)" }
        }
    }
    if ($outputReady) {
        $script:Proof.completedUtc = [DateTime]::UtcNow.ToString('o')
        $script:Proof.artifacts = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -ne 'proof.json' |
            Sort-Object Name | ForEach-Object { @{ file = $_.Name; bytes = $_.Length; sha256 = Get-Digest $_.FullName } })
        Save-Proof
    }
}
Write-Host "Capture finished. Inspect before.png, after.png, after-remove.mp4 and proof.json in $OutputDirectory. CI host disposal is the cleanup boundary."
