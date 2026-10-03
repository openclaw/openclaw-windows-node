#requires -Version 5.1
#requires -PSEdition Desktop
<#
.SYNOPSIS
    Standalone, current-user OpenClaw cleanup for demo/test devices. Dry-run by default.
.DESCRIPTION
    Preview first, then repeat with -ConfirmDestructive. Removes release Companion
    and native Gateway packages, supported Inno installs, and ordinary app profiles.
    Runs native MXC teardown BEFORE removing the Gateway package. No build, Copilot,
    Python, or repository dependencies. Use Windows PowerShell as the affected user.

    WSL, dev profiles, extra test profiles, and shared cached weights are opt-ins.
    Deleting profiles destroys identities, conversations, settings, and local models.
    Only existing diagnostic ZIPs are copied, NOT a recoverable profile backup.
    Close other test sessions first. This is not safe against concurrent hostile
    filesystem changes. Errors stop cleanup and produce a nonzero exit status.
.PARAMETER All
    Shorthand for -IncludeDev -RemoveCachedModels -RemoveWslGateway.
    Still previews unless -ConfirmDestructive is supplied. Extra isolated profiles
    must still be named with -AdditionalProfilePath; no directories are auto-swept.
.PARAMETER ExcludeCachedModels
    Preserve external shared cached models even with -All or -RemoveCachedModels.
    Models inside deleted app profiles or WSL filesystems are still removed.
.PARAMETER RemoveIsolatedProfilePath
    Explicit Windows isolated-profile directories to remove through Win32_UserProfile.
    Only unloaded, non-special S-1-5-110 profiles directly under ProfilesDirectory
    are eligible. Works when the folder was already deleted but its registration
    remains. Never implied by -All. Applying this option requires elevation.
.EXAMPLE
    .\clean-uninstall.ps1
.EXAMPLE
    .\clean-uninstall.ps1 -ConfirmDestructive
.EXAMPLE
    .\clean-uninstall.ps1 -IncludeDev -RemoveCachedModels -RemoveWslGateway
.EXAMPLE
    .\clean-uninstall.ps1 -All
.EXAMPLE
    .\clean-uninstall.ps1 -All -ConfirmDestructive
.EXAMPLE
    .\clean-uninstall.ps1 -All -ExcludeCachedModels -ConfirmDestructive
.EXAMPLE
    .\clean-uninstall.ps1 -AdditionalProfilePath C:\Demo\OpenClawTest -ConfirmDestructive
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$ConfirmDestructive,
    [switch]$All,
    [switch]$IncludeDev,
    [switch]$RemoveCachedModels,
    [switch]$ExcludeCachedModels,
    [switch]$RemoveWslGateway,
    [string[]]$AdditionalProfilePath = @(),
    [string[]]$RemoveIsolatedProfilePath = @(),
    [string]$ReportDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CleanProperty($Object, [string]$Name) {
    if ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]) {
        return $Object.PSObject.Properties[$Name].Value
    }
    return $null
}

function Get-CleanFullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:\\' -or
        $Path -match '[*?\[\]/]' -or $Path.Substring(2).Contains(':')) {
        throw "Expected an absolute local literal path, not a wildcard, UNC path, or stream: $Path"
    }
    foreach ($segment in $Path.Substring(3).Split('\') | Where-Object { $_ }) {
        if ($segment -match '[. ]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
            throw "Refusing an ambiguous Windows path segment: $Path"
        }
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '~[0-9]') { $full = Expand-CleanLongPath $full }
    if ($full.Length -eq 3) { return $full }
    return $full.TrimEnd('\')
}

function Expand-CleanLongPath([string]$Path) {
    if (-not ('OpenClawCleanup.NativePaths' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace OpenClawCleanup {
    public static class NativePaths {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetLongPathName(string path, StringBuilder buffer, uint length);
    }
}
'@
    }
    $cursor = $Path
    $suffix = [Collections.Generic.Stack[string]]::new()
    while (-not (Test-Path -LiteralPath $cursor)) {
        $leaf = Split-Path -Leaf $cursor
        if (-not $leaf -or $leaf -match '~[0-9]') {
            throw "Refusing an unresolved ambiguous Windows short path: $Path"
        }
        $suffix.Push($leaf)
        $cursor = Split-Path -Parent $cursor
    }
    $buffer = [Text.StringBuilder]::new(32768)
    $length = [OpenClawCleanup.NativePaths]::GetLongPathName($cursor, $buffer, $buffer.Capacity)
    if ($length -eq 0 -or $length -ge $buffer.Capacity -or $buffer.ToString() -match '~[0-9]') {
        throw "Cannot resolve Windows short path safely: $Path"
    }
    $full = $buffer.ToString()
    while ($suffix.Count) { $full = Join-Path $full $suffix.Pop() }
    return $full
}

function Test-CleanWithin([string]$Path, [string]$Root) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CleanPath([string]$Path) {
    $full = Get-CleanFullPath $Path
    if ($full -eq [IO.Path]::GetPathRoot($full)) { throw "Refusing a filesystem root: $full" }
    $protected = @($env:USERPROFILE, $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP,
        $env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $PSHOME, $PSScriptRoot)
    foreach ($folder in [Enum]::GetValues([Environment+SpecialFolder])) {
        $protected += [Environment]::GetFolderPath($folder)
    }
    foreach ($root in $protected | Where-Object { $_ }) {
        # Targets are local drive paths; redirected UNC special folders cannot
        # be their ancestors. Do not contact the server or treat it as a target.
        if ($root.StartsWith('\\')) { continue }
        if (Test-CleanWithin (Get-CleanFullPath $root) $full) {
            throw "Refusing a protected directory or its ancestor: $full"
        }
    }
    foreach ($root in @($env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        if (Test-CleanWithin $full (Get-CleanFullPath $root)) { throw "Refusing a system/program directory: $full" }
    }
    $userHome = Get-CleanFullPath $env:USERPROFILE
    if ((Test-CleanWithin $full (Split-Path -Parent $userHome)) -and -not (Test-CleanWithin $full $userHome)) {
        throw "Refusing another user's profile: $full"
    }
    if ($full -match '(?i)\\(?:WindowsApps|Packages|\.cache|huggingface|hub)$' -or
        $full -match '(?i)\\\.copilot(?:\\.*)?$' -and
        $full -notmatch '(?i)\\\.copilot\\session-state\\[0-9a-f-]{36}\\files\\[^\\]+(?:\\.*)?$') {
        throw "Refusing a shared cache, package root, or Copilot workspace root: $full"
    }
    Assert-CleanNoReparsePath $full
    return $full
}

function Assert-CleanNoReparsePath([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing reparse point in path: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Get-CleanTree([string]$Path) {
    $full = Assert-CleanPath $Path
    if (-not (Test-Path -LiteralPath $full)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($full)
    while ($pending.Count -gt 0) {
        $item = Get-Item -LiteralPath $pending.Pop() -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing reparse point: $($item.FullName)"
        }
        if ($item.Name -eq '.git') { throw "Refusing a source checkout: $full" }
        $item
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Get-CleanPackages([bool]$Dev) {
    $names = @('OpenClawFoundation.OpenClaw', 'OpenClawFoundation.OpenClawGateway', 'OpenClaw.Gateway')
    if ($Dev) { $names += 'OpenClawFoundation.OpenClaw.Dev' }
    @(Get-AppxPackage -ErrorAction Stop | Where-Object { $_.Name -in $names })
}

function Get-CleanWindowsProfiles {
    Get-CimInstance Win32_UserProfile -ErrorAction Stop
}

function Get-CleanIsolatedProfiles([string[]]$Paths, [object[]]$Inventory) {
    if (-not $Paths) { return }
    $settings = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    $root = Get-CleanFullPath ([Environment]::ExpandEnvironmentVariables($settings.ProfilesDirectory))
    foreach ($path in @($Paths | ForEach-Object { Get-CleanFullPath $_ } | Select-Object -Unique)) {
        if ((Split-Path -Parent $path) -ne $root -or
            $path -eq (Get-CleanFullPath $env:USERPROFILE)) {
            throw "Select an isolated profile directly under '$root', never a current-user or nested path: $path"
        }
        Assert-CleanNoReparsePath $path
        $matches = @($Inventory | Where-Object { (Get-CleanFullPath $_.LocalPath) -eq $path })
        if ($matches.Count -eq 0) {
            if (Test-Path -LiteralPath $path) { throw "No Windows profile registration proves this directory's identity: $path" }
            Write-Host "Already absent isolated profile path: $path"
            continue
        }
        $profile = $matches[0]
        if ($matches.Count -ne 1 -or $profile.SID -notmatch '^S-1-5-110-\d+-\d+-\d+-\d+$' -or
            @($Inventory | Where-Object { $_.SID -eq $profile.SID }).Count -ne 1 -or
            $profile.Loaded -isnot [bool] -or $profile.Loaded -or
            $profile.Special -isnot [bool] -or $profile.Special) {
            throw "Refusing a loaded, special, ordinary, or ambiguous Windows profile: $path"
        }
        $null = [Security.Principal.SecurityIdentifier]::new($profile.SID)
        [pscustomobject]@{ Path = $path; SID = $profile.SID; Instance = $profile }
    }
}

function Assert-CleanProfileRemovalAccess {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Explicit Windows profile removal requires an elevated Windows PowerShell running as the same affected user. No cleanup started.'
    }
}

function Get-CleanIsolatedProfileForRemoval($Expected) {
    $inventory = @(Get-CleanWindowsProfiles)
    $current = @(Get-CleanIsolatedProfiles @($Expected.Path) $inventory)
    if ($current.Count -eq 0) {
        if (@($inventory | Where-Object { $_.SID -eq $Expected.SID }).Count) {
            throw "Isolated profile SID moved to a different path: $($Expected.SID)"
        }
        return
    }
    if ($current[0].SID -ne $Expected.SID) { throw "Isolated profile identity changed: $($Expected.Path)" }
    return $current[0].Instance
}

function Remove-CleanIsolatedProfile($Expected) {
    $current = Get-CleanIsolatedProfileForRemoval $Expected
    if ($null -ne $current) {
        # Windows owns both profile registration and file cleanup. Never fall
        # back to deleting ProfileList keys or recursively sweeping C:\Users.
        Remove-CimInstance -InputObject $current -Confirm:$false -ErrorAction Stop
    }
    $remaining = @(Get-CleanWindowsProfiles | Where-Object {
        $_.SID -eq $Expected.SID -or (Get-CleanFullPath $_.LocalPath) -eq $Expected.Path
    })
    if ($remaining.Count -gt 0 -or (Test-Path -LiteralPath $Expected.Path)) {
        throw "Isolated Windows profile registration or files remain: $($Expected.Path)"
    }
    Write-Host "REMOVED isolated Windows profile: $($Expected.Path) [$($Expected.SID)]"
}

function Get-CleanWin32([bool]$Dev) {
    $identities = @{ '{M0LTB0T-TRAY-4PP1-D3N7}_is1' = 'OpenClaw Companion' }
    if ($Dev) { $identities['{M0LTB0T-TRAY-4PP1-DEV}_is1'] = 'OpenClaw Companion (Dev)' }
    foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($key in Get-ChildItem -LiteralPath $root) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath
            $keyName = $key.PSChildName
            $display = [string](Get-CleanProperty $entry 'DisplayName')
            if (-not $identities.ContainsKey($keyName)) {
                foreach ($name in $identities.Values) {
                    if ($display -eq $name -or $display.StartsWith("$name version ", [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Unrecognized Companion registration. Remove it in Installed apps first: $display"
                    }
                }
                continue
            }
            $name = $identities[$keyName]
            $version = [string](Get-CleanProperty $entry 'DisplayVersion')
            if ((Get-CleanProperty $entry 'Publisher') -ne 'OpenClaw Foundation' -or
                ($display -ne $name -and ($version -notmatch '^\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or $display -ne "$name version $version"))) {
                throw "Inno registration identity is inconsistent: $keyName"
            }
            $command = [string](Get-CleanProperty $entry 'UninstallString')
            if ($command -notmatch '^"(?<exe>[A-Za-z]:\\[^"]+\\unins[0-9]+\.exe)"\s*$') {
                throw "Unsupported Win32 uninstaller. Remove '$($entry.DisplayName)' in Installed apps first."
            }
            $exe = Get-CleanFullPath $Matches.exe
            $install = Get-CleanFullPath ([string](Get-CleanProperty $entry 'InstallLocation'))
            if (-not (Test-CleanWithin $exe $install) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                throw "Uninstaller is missing or outside its registered install location: $exe"
            }
            [pscustomobject]@{ Name = $entry.DisplayName; Key = $key.PSPath; Exe = $exe; Install = $install }
        }
    }
}

function Get-CleanDistroRecords {
    # Query registrations without starting WSL or invoking its first-install stub.
    $root = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($key in Get-ChildItem -LiteralPath $root) {
        $entry = Get-ItemProperty -LiteralPath $key.PSPath
        $name = Get-CleanProperty $entry 'DistributionName'
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'WSL registration cannot be identified. Inspect it before cleanup.' }
        $basePath = [string](Get-CleanProperty $entry 'BasePath')
        if ($basePath.StartsWith('\\?\') -and $basePath.Substring(4) -match '^[A-Za-z]:\\') {
            $basePath = $basePath.Substring(4)
        }
        $basePath = Get-CleanFullPath $basePath
        Assert-CleanNoReparsePath $basePath
        [pscustomobject]@{ Name = $name; BasePath = $basePath }
    }
}

function Get-CleanDistros {
    Get-CleanDistroRecords | ForEach-Object { $_.Name }
}

function Assert-CleanDistroStorage([object[]]$Records, [string[]]$Targets, [string[]]$Removing = @()) {
    foreach ($record in $Records) {
        if ($record.Name -in $Removing) { continue }
        foreach ($target in $Targets) {
            $target = Get-CleanFullPath $target
            if ((Test-CleanWithin $record.BasePath $target) -or (Test-CleanWithin $target $record.BasePath)) {
                throw "Preserved WSL storage overlaps cleanup target '$target': $($record.Name) at $($record.BasePath). Explicitly select its removal or relocate/exclude the target."
            }
        }
    }
}

function Get-CleanModelProvenance($Asset) {
    $source = [string](Get-CleanProperty $Asset 'SourceUrl')
    # Match the original URL, not a Uri-normalized path that can erase traversal.
    if ($source -cnotmatch '^https://huggingface\.co/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/resolve/([0-9a-f]{40})/([^?#]+)(?:\?download=true)?$') {
        throw 'Model receipt source must be an immutable Hugging Face resolve URL.'
    }
    $owner = $Matches[1]
    $repo = $Matches[2]
    $revision = $Matches[3]
    $relative = [Uri]::UnescapeDataString($Matches[4])
    foreach ($part in @($owner, $repo) + @($relative.Split('/'))) {
        if (-not $part -or $part -match '[\\:*?"<>|\x00-\x1f]' -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
            throw 'Model receipt source contains an unsafe path segment.'
        }
    }
    if (($relative.Split('/')[-1]) -cne (Get-CleanProperty $Asset 'FileName')) {
        throw 'Model receipt filename disagrees with its source.'
    }
    [pscustomobject]@{
        ModelId = "$owner/$repo@$revision"
        SnapshotPath = "models--$owner--$repo\snapshots\$revision\$($relative.Replace('/', '\'))"
    }
}

function Get-CleanModels([object[]]$Files) {
    foreach ($file in $Files | Where-Object { $_.FullName -match '\\LocalAI\\state\.json$' }) {
        $receipt = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $schema = Get-CleanProperty $receipt 'SchemaVersion'
        if ($schema -eq 3) { continue }
        if ($schema -notin @(4, 5)) { throw "Unsupported Local AI receipt: $($file.FullName)" }
        $cache = Get-CleanFullPath ([string](Get-CleanProperty $receipt 'ModelCacheRoot'))
        $paths = @([string](Get-CleanProperty $receipt 'CachedModelPath'))
        $assets = @((Get-CleanProperty $receipt 'ModelAsset'))
        if ($schema -eq 5) {
            $additionalPaths = @((Get-CleanProperty $receipt 'AdditionalModelPaths'))
            $additionalAssets = @((Get-CleanProperty $receipt 'AdditionalModelAssets'))
            if ($additionalPaths.Count -eq 0 -or $additionalPaths.Count -ne $additionalAssets.Count) { throw 'Invalid additional model receipts.' }
            $paths += $additionalPaths
            $assets += $additionalAssets
        } elseif (@(Get-CleanProperty $receipt 'AdditionalModelPaths' | Where-Object { $null -ne $_ }).Count -gt 0 -or
            @(Get-CleanProperty $receipt 'AdditionalModelAssets' | Where-Object { $null -ne $_ }).Count -gt 0) {
            throw 'Only schema-5 receipts can contain additional model assets.'
        }
        if ($paths.Count -ne $assets.Count) { throw "Invalid model receipt: $($file.FullName)" }
        $seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        for ($index = 0; $index -lt $paths.Count; $index++) {
            $path = Assert-CleanPath $paths[$index]
            $provenance = Get-CleanModelProvenance $assets[$index]
            if (-not $seenNames.Add([string](Get-CleanProperty $assets[$index] 'FileName'))) {
                throw 'Model receipt asset filenames must be unique.'
            }
            if ($index -eq 0 -and $provenance.ModelId -cne (Get-CleanProperty $receipt 'ModelId')) {
                throw 'Primary model receipt provenance disagrees with ModelId.'
            }
            $expected = Get-CleanFullPath (Join-Path $cache $provenance.SnapshotPath)
            $prefix = $cache + '\'
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not $path.Equals($expected, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($path) -ne '.gguf') {
                throw "Model receipt does not name an exact Hugging Face snapshot GGUF: $path"
            }
            $hash = [string](Get-CleanProperty $assets[$index] 'Sha256')
            $size = Get-CleanProperty $assets[$index] 'SizeBytes'
            if ($hash -notmatch '^[0-9a-f]{64}$' -or $null -eq $size -or $size -le 0) {
                throw "Missing model digest or size: $path"
            }
            if (Test-Path -LiteralPath $path) {
                $item = Get-Item -LiteralPath $path -Force
                if ($item.PSIsContainer -or $item.Length -ne $size) { throw "Model receipt size/type mismatch: $path" }
                [pscustomobject]@{ Path = $path; Size = $size; Hash = $hash }
            }
        }
    }
}

function Get-CleanProcesses([string[]]$OwnedRoots) {
    foreach ($process in Get-CimInstance Win32_Process -ErrorAction Stop) {
        if ($process.Name -notmatch '^(OpenClaw\.Tray\.WinUI|llama-server|openclaw-wsl-keepalive)\.exe$') { continue }
        $path = $process.ExecutablePath
        if (-not $path) { throw "Cannot determine ownership of candidate process PID $($process.ProcessId). Close it manually." }
        $path = Get-CleanFullPath $path
        $owned = $false
        foreach ($root in $OwnedRoots) {
            if (Test-CleanWithin $path (Get-CleanFullPath $root)) { $owned = $true; break }
        }
        if (-not $owned) { throw "Candidate process is outside selected roots. Close it manually before cleanup: PID $($process.ProcessId), $path" }
        $process
    }
}

function Stop-CleanProcess($Expected) {
    $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($Expected.ProcessId)" -ErrorAction Stop)
    if ($current.Count -eq 0) { return }
    if ($current.Count -ne 1 -or $current[0].ExecutablePath -ne $Expected.ExecutablePath -or
        $current[0].CreationDate -ne $Expected.CreationDate) {
        throw "PID $($Expected.ProcessId) changed identity. Cleanup stopped."
    }
    Stop-Process -Id $Expected.ProcessId -Force -ErrorAction Stop
}

function Invoke-CleanNativeCommand([string]$FilePath, [string]$Arguments, [int]$TimeoutMilliseconds = 240000) {
    # Match Test-InnoMigration's direct process pattern: Start-Process with
    # redirection does not reliably retain ExitCode on Windows PowerShell 5.1.
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
            throw 'Native command timed out. Remaining state is preserved.'
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 10000)) {
            throw 'Native output capture timed out. Remaining state is preserved.'
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    } finally { $process.Dispose() }
}

function Invoke-CleanTeardown($Package, [string]$Reports) {
    $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($Package.PackageFamilyName)\clawctl.exe"
    if (-not (Test-Path -LiteralPath $alias -PathType Leaf)) { throw "Missing native teardown CLI: $alias" }
    $state = Join-Path $env:LOCALAPPDATA "Packages\$($Package.PackageFamilyName)\LocalState\OpenClawGatewayMSIX\session.json"
    $accountSid = $null
    $accountProfiles = @()
    if (Test-Path -LiteralPath $state) {
        $null = Assert-CleanPath $state
        $session = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
        $accountSid = Get-CleanProperty $session 'agentUserSid'
        if ($accountSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$') { throw "Cannot verify native session account: $state" }
        $accountProfiles = @(Get-CimInstance Win32_UserProfile -ErrorAction Stop |
            Where-Object { $_.SID -eq $accountSid } | Select-Object -ExpandProperty LocalPath)
    }
    $stdout = Join-Path $Reports "$($Package.Name)-teardown.json"
    $stderr = Join-Path $Reports "$($Package.Name)-teardown.stderr.txt"
    $command = Invoke-CleanNativeCommand $alias 'teardown --force --json'
    $command.Stdout | Set-Content -LiteralPath $stdout -Encoding UTF8
    $command.Stderr | Set-Content -LiteralPath $stderr -Encoding UTF8
    $result = $command.Stdout | ConvertFrom-Json
    if ($command.ExitCode -ne 0 -or (Get-CleanProperty $result 'ok') -isnot [bool] -or
        -not $result.ok -or (Get-CleanProperty $result 'command') -ne 'teardown' -or
        (Get-CleanProperty (Get-CleanProperty $result 'gateway') 'state') -ne 'records-removed' -or
        (Get-CleanProperty (Get-CleanProperty $result 'session') 'state') -notin @('removed','not-configured')) {
        throw 'Native teardown did not confirm success. Remaining state is preserved; inspect the local report.'
    }
    if (Test-Path -LiteralPath $state) { throw 'Native session record remains after teardown.' }
    if ($accountSid) {
        $accounts = @(Get-CimInstance Win32_UserAccount -Filter "LocalAccount = True" -ErrorAction Stop |
            Where-Object { $_.SID -eq $accountSid })
        $profiles = @(Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -eq $accountSid })
        if ($accounts.Count -gt 0 -or $profiles.Count -gt 0) { throw 'Native MXC account/profile remains after teardown.' }
        foreach ($profile in $accountProfiles) {
            if (Test-Path -LiteralPath $profile) { throw 'Native MXC profile files remain after teardown.' }
        }
    }
}

function Invoke-CleanWin32($App) {
    # No silent flags: Inno must ask the operator whether WSL should be retained.
    $process = Start-Process -FilePath $App.Exe -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while (Test-Path -LiteralPath $App.Key) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Uninstall cancelled, blocked, or timed out: $($App.Name)" }
        Start-Sleep -Seconds 2
    }
    if (-not $process.HasExited) { $null = $process.WaitForExit(30000) }
}

function Assert-CleanStartupExecutable([string]$Executable, [string[]]$OwnedRoots) {
    if ($Executable -match '^"([^"]+)"$') { $Executable = $Matches[1] }
    if ($Executable.Contains('"')) { throw 'Startup executable has ambiguous quoting.' }
    $path = Get-CleanFullPath $Executable
    if ([IO.Path]::GetFileName($path) -ne 'OpenClaw.Tray.WinUI.exe') { throw 'Startup executable is not Companion.' }
    foreach ($root in $OwnedRoots) {
        if (Test-CleanWithin $path (Get-CleanFullPath $root)) { return }
    }
    throw "Startup executable is outside selected ownership roots: $path"
}

function Get-CleanStartup([bool]$Dev, [string[]]$OwnedRoots) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $names = @('OpenClawTray')
    $taskNames = @('OpenClaw Companion')
    if ($Dev) { $names += 'OpenClawTray-Dev'; $taskNames += 'OpenClaw Companion (Dev)' }
    if (Test-Path -LiteralPath $runKey) {
        $values = Get-ItemProperty -LiteralPath $runKey
        foreach ($name in $names) {
            $value = Get-CleanProperty $values $name
            if ($null -ne $value) {
                if ($value -notmatch '^(?:"([^"]+)"|([^\s"]+))(?:\s+--background)?\s*$') {
                    throw "Startup Run command is not a supported Companion command: $name"
                }
                $executable = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                Assert-CleanStartupExecutable $executable $OwnedRoots
                [pscustomobject]@{ Kind = 'Run'; Name = $name; Key = $runKey; Value = $value }
            }
        }
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($task in Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -in $taskNames }) {
        $principal = $task.Principal.UserId
        if ([string]::IsNullOrWhiteSpace($principal)) { throw 'Startup task has no identifiable principal.' }
        if ($principal -notmatch '^S-1-') {
            $principal = ([Security.Principal.NTAccount]$principal).Translate([Security.Principal.SecurityIdentifier]).Value
        }
        $actions = @($task.Actions)
        if ($principal -ne $sid -or $actions.Count -ne 1 -or $task.TaskPath -ne '\') {
            throw "Startup task has unexpected ownership. Inspect it manually: $($task.TaskPath)$($task.TaskName)"
        }
        Assert-CleanStartupExecutable $actions[0].Execute $OwnedRoots
        [pscustomobject]@{ Kind = 'Task'; Name = $task.TaskName; Key = $task.TaskPath; Value = (Export-ScheduledTask -InputObject $task) }
    }
}

function Invoke-OpenClawClean {
    [CmdletBinding(SupportsShouldProcess)]
    param([bool]$Apply, [bool]$Dev, [bool]$Models, [bool]$Wsl, [string[]]$Extra, [string]$Reports,
        [string[]]$IsolatedProfiles = @())

    $overrides = @(Get-ChildItem Env: | Where-Object {
        $_.Name -in @('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR',
            'OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_TRAY_LOCAL_DATA_DIR',
            'OPENCLAW_STATE_DIR','OPENCLAW_CONFIG_PATH') -and $_.Value
    })
    if ($overrides.Count -gt 0) { throw "Clear path overrides before cleanup. Use -AdditionalProfilePath explicitly: $($overrides.Name -join ', ')" }
    $packages = @(Get-CleanPackages $Dev)
    $apps = @(Get-CleanWin32 $Dev)
    $windowsProfiles = @(Get-CleanWindowsProfiles)
    $isolatedToRemove = @(Get-CleanIsolatedProfiles $IsolatedProfiles $windowsProfiles)
    $distroRecords = @(Get-CleanDistroRecords)
    $distros = @($distroRecords | ForEach-Object { $_.Name })
    $selectedDistros = @()
    if ($Wsl) {
        $distroNames = @('OpenClawGateway')
        if ($Dev) { $distroNames += 'OpenClawGateway-Dev' }
        $selectedDistros = @($distros | Where-Object { $_ -in $distroNames })
    }
    $profiles = @()
    foreach ($root in @($env:APPDATA, $env:LOCALAPPDATA)) {
        $profiles += Join-Path $root 'OpenClawTray'
        if ($Dev) { $profiles += Join-Path $root 'OpenClawTray-Dev' }
    }
    $profiles += $Extra
    foreach ($extraPath in $Extra) {
        $extraFull = Assert-CleanPath $extraPath
        if (Test-CleanWithin $extraFull (Join-Path $env:LOCALAPPDATA 'Packages')) {
            throw 'Extra profiles cannot select package data. Use Windows package removal.'
        }
    }
    $ownedRoots = @($profiles) + @($packages | ForEach-Object { $_.InstallLocation }) +
        @($apps | ForEach-Object { $_.Install })
    $packageData = @($packages | ForEach-Object { Join-Path $env:LOCALAPPDATA "Packages\$($_.PackageFamilyName)" })
    $ownedRoots += $packageData
    $ownedRoots = @($ownedRoots | ForEach-Object { Get-CleanFullPath $_ } | Select-Object -Unique)
    $deletionTargets = @($profiles + $packageData + @($apps | ForEach-Object { $_.Install }) +
        @($isolatedToRemove | ForEach-Object { $_.Path }))
    Assert-CleanDistroStorage $distroRecords $deletionTargets $selectedDistros
    $preservedStorage = @()
    foreach ($record in $distroRecords | Where-Object { $_.Name -notin $selectedDistros }) {
        if (Test-Path -LiteralPath $record.BasePath) {
            $preservedStorage += $record.BasePath
            $preservedStorage += @(Get-ChildItem -LiteralPath $record.BasePath -File -Filter '*.vhdx' -Force |
                ForEach-Object { $_.FullName })
        }
    }
    $packagesRoot = Join-Path $env:LOCALAPPDATA 'Packages'
    if (Test-Path -LiteralPath $packagesRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $packagesRoot -Directory -Force) {
            if ($directory.Name -notmatch '^(OpenClawFoundation\.OpenClawGateway|OpenClaw\.Gateway)_[a-z0-9]{13}$') { continue }
            if ($directory.Name -notin @($packages | ForEach-Object { $_.PackageFamilyName }) -and
                (Test-Path -LiteralPath (Join-Path $directory.FullName 'LocalState\OpenClawGatewayMSIX\session.json'))) {
                throw "Orphan native session metadata remains. Repair/reinstall its Gateway package for teardown: $($directory.FullName)"
            }
        }
    }
    # Package profile trees are inventoried but removed only by Windows package management.
    $trees = @()
    foreach ($profile in @($profiles + $packageData | Select-Object -Unique)) {
        $trees += @(Get-CleanTree $profile)
    }
    $profiles = @($profiles | ForEach-Object { Assert-CleanPath $_ } | Select-Object -Unique)
    $files = @($trees | Where-Object { -not $_.PSIsContainer })
    $modelsToRemove = @()
    if ($Models) { $modelsToRemove = @(Get-CleanModels $files | Sort-Object Path -Unique) }
    $processes = @(Get-CleanProcesses $ownedRoots)
    $startup = @(Get-CleanStartup $Dev $ownedRoots)
    $gateways = @($packages | Where-Object { $_.Name -in @('OpenClawFoundation.OpenClawGateway','OpenClaw.Gateway') })
    foreach ($gateway in $gateways) {
        $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($gateway.PackageFamilyName)\clawctl.exe"
        if (-not (Test-Path -LiteralPath $alias -PathType Leaf)) { throw "Native teardown CLI missing. Repair the Gateway package first: $alias" }
    }

    Write-Host 'OpenClaw clean uninstall: exact current-user plan'
    foreach ($package in $packages) { Write-Host "REMOVE package: $($package.PackageFullName)" }
    foreach ($app in $apps) { Write-Host "UNINSTALL interactively: $($app.Name) [$($app.Exe)]. Choose No to preserve WSL." }
    foreach ($profile in $profiles) { Write-Host "REMOVE profile if present: $profile" }
    foreach ($profile in $isolatedToRemove) {
        Write-Host "REMOVE isolated Windows profile and registration: $($profile.Path) [$($profile.SID)]"
    }
    $selectedProfileSids = @($isolatedToRemove | ForEach-Object { $_.SID })
    foreach ($profile in $windowsProfiles | Where-Object { $_.SID -like 'S-1-5-110-*' -and $_.SID -notin $selectedProfileSids }) {
        Write-Warning "Unselected isolated Windows profile (OpenClaw ownership unverified): $($profile.LocalPath) [$($profile.SID)]. Preserved; use -RemoveIsolatedProfilePath only after confirming ownership."
    }
    foreach ($model in $modelsToRemove) { Write-Host "REMOVE shared model: $($model.Path) ($($model.Size) bytes; digest checked before removal)" }
    foreach ($process in $processes) { Write-Host "STOP PID $($process.ProcessId): $($process.ExecutablePath) [$($process.CreationDate)]" }
    foreach ($entry in $startup) { Write-Host "REMOVE $($entry.Kind): $($entry.Key)\$($entry.Name)" }
    foreach ($distro in $distros) {
        $action = if ($distro -in $selectedDistros) { 'DESTROY WSL filesystem' } else { 'PRESERVE WSL' }
        Write-Host "${action}: $distro"
    }
    [long]$bytes = 0
    foreach ($file in $files) { $bytes += $file.Length }
    Write-Host "Inventoried $($files.Count) profile/package files ($bytes bytes). Package data is removed by Windows."
    Write-Host 'Preserved: unrelated apps/profiles, system Node.js, Tailscale, shared cache roots, source/worktrees.'
    if (-not $Models) { Write-Host 'Preserved: external shared model weights. Use -RemoveCachedModels only after reviewing ownership.' }
    if (-not $Apply) { Write-Host 'DRY RUN. Nothing changed. Repeat the same options with -ConfirmDestructive to apply.'; return }
    if (-not $PSCmdlet.ShouldProcess('the exact OpenClaw targets printed above', 'Permanently remove apps, identities, state, and selected models/distros')) { return }
    if ($isolatedToRemove.Count) { Assert-CleanProfileRemovalAccess }

    if (-not $Reports) { $Reports = Join-Path $env:TEMP ("OpenClawCleanReports\" + [guid]::NewGuid().ToString('N')) }
    $Reports = Assert-CleanPath $Reports
    foreach ($target in $deletionTargets) {
        $target = Get-CleanFullPath $target
        if ((Test-CleanWithin $Reports $target) -or (Test-CleanWithin $target $Reports)) { throw 'Report directory overlaps a cleanup target.' }
    }
    if (Test-Path -LiteralPath $Reports) { throw "Use a new report directory: $Reports" }
    New-Item -ItemType Directory -Path $Reports -Force | Out-Null
    $transcribing = $false
    try {
        Start-Transcript -LiteralPath (Join-Path $Reports 'cleanup.log') | Out-Null
        $transcribing = $true
        Write-Host "Reports (sensitive, local only): $Reports"
        [ordered]@{
            packages = @($packages | ForEach-Object { $_.PackageFullName })
            win32Apps = @($apps | ForEach-Object { $_.Name })
            profiles = $profiles
            isolatedWindowsProfiles = @($isolatedToRemove | Select-Object Path, SID)
            models = $modelsToRemove
            processes = @($processes | Select-Object ProcessId, CreationDate, ExecutablePath)
            startup = @($startup | Select-Object Kind, Key, Name)
            removeDistros = $selectedDistros
            preserveDistros = @($distros | Where-Object { $_ -notin $selectedDistros })
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Reports 'plan.json') -Encoding UTF8
        foreach ($zip in $files | Where-Object { $_.Name -like 'openclaw-diagnostics-*.zip' }) {
            $destination = Join-Path $Reports ("$([guid]::NewGuid().ToString('N'))-$($zip.Name)")
            Copy-Item -LiteralPath $zip.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $zip.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
                throw 'Diagnostic copy hash mismatch. Cleanup stopped.'
            }
        }
        # Complete expensive shared-file validation before any teardown or deletion.
        foreach ($model in $modelsToRemove) {
            if ((Get-FileHash -LiteralPath $model.Path -Algorithm SHA256).Hash -ne $model.Hash) {
                throw "Model digest mismatch. Nothing removed: $($model.Path)"
            }
        }
        $livePackages = @(Get-CleanPackages $Dev | ForEach-Object { $_.PackageFullName })
        foreach ($package in $packages) {
            if ($package.PackageFullName -notin $livePackages) { throw 'Package inventory changed. Preview again before cleanup.' }
        }
        Assert-CleanDistroStorage @(Get-CleanDistroRecords) $deletionTargets $selectedDistros
        foreach ($profile in $isolatedToRemove) { $null = Get-CleanIsolatedProfileForRemoval $profile }
        foreach ($process in $processes) { Stop-CleanProcess $process }
        foreach ($gateway in $gateways) {
            Write-Host "TEARDOWN: $($gateway.PackageFullName)"
            Invoke-CleanTeardown $gateway $Reports
        }
        foreach ($package in @($gateways) + @($packages | Where-Object { $_ -notin $gateways })) {
            Write-Host "UNINSTALL: $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }
        foreach ($app in $apps) { Invoke-CleanWin32 $app }
        foreach ($entry in $startup) {
            if ($entry.Kind -eq 'Run') {
                if (Test-Path -LiteralPath $entry.Key) {
                    $value = Get-CleanProperty (Get-ItemProperty -LiteralPath $entry.Key) $entry.Name
                    if ($null -ne $value) {
                        if ($value -ne $entry.Value) { throw "Startup value changed: $($entry.Name)" }
                        Remove-ItemProperty -LiteralPath $entry.Key -Name $entry.Name
                    }
                }
            } else {
                $task = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq $entry.Name -and $_.TaskPath -eq $entry.Key })
                if ($task.Count -gt 0) {
                    if ((Export-ScheduledTask -InputObject $task[0]) -ne $entry.Value) { throw "Startup task changed: $($entry.Name)" }
                    Unregister-ScheduledTask -InputObject $task[0] -Confirm:$false -ErrorAction Stop
                }
            }
        }
        foreach ($distro in $selectedDistros) {
            if ($distro -notin @(Get-CleanDistros)) {
                Write-Host "WSL already removed by uninstall: $distro"
                continue
            }
            & wsl.exe --unregister $distro
            if ($LASTEXITCODE -ne 0) { throw "WSL unregister failed: $distro" }
        }
        if (@(Get-CleanPackages $Dev).Count -gt 0 -or @(Get-CleanWin32 $Dev).Count -gt 0) { throw 'An app is still registered. State deletion stopped.' }
        if (@(Get-CleanProcesses $ownedRoots).Count -gt 0) { throw 'An owned process is still running. State deletion stopped.' }
        # Even an approved distro must be fully unregistered before its storage
        # can be removed as profile data.
        Assert-CleanDistroStorage @(Get-CleanDistroRecords) $deletionTargets
        foreach ($path in $packageData) {
            if (@(Get-CleanTree $path | Where-Object { -not $_.PSIsContainer }).Count -gt 0) {
                throw "Package data remains after uninstall. State deletion stopped: $path"
            }
        }
        foreach ($profile in $isolatedToRemove) { Remove-CleanIsolatedProfile $profile }
        foreach ($profile in $profiles) {
            $null = @(Get-CleanTree $profile)
            if (Test-Path -LiteralPath $profile) {
                Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction Stop
                Write-Host "REMOVED profile: $profile"
            }
        }
        foreach ($model in $modelsToRemove) {
            $null = Assert-CleanPath $model.Path
            if (Test-Path -LiteralPath $model.Path) {
                if ((Get-Item -LiteralPath $model.Path).Length -ne $model.Size -or
                    (Get-FileHash -LiteralPath $model.Path -Algorithm SHA256).Hash -ne $model.Hash) {
                    throw "Model changed during cleanup: $($model.Path)"
                }
                Remove-Item -LiteralPath $model.Path -Force -ErrorAction Stop
                Write-Host "REMOVED shared model: $($model.Path)"
            }
        }
        foreach ($path in @($profiles) + @($modelsToRemove | ForEach-Object { $_.Path })) {
            if (Test-Path -LiteralPath $path) { throw "Target remains: $path" }
        }
        if (@(Get-CleanStartup $Dev $ownedRoots).Count -gt 0) { throw 'A startup entry remains.' }
        $afterRecords = @(Get-CleanDistroRecords)
        $afterDistros = @($afterRecords | ForEach-Object { $_.Name })
        foreach ($distro in $selectedDistros) {
            if ($distro -in $afterDistros) { throw "WSL distro remains: $distro" }
        }
        foreach ($distro in $distros | Where-Object { $_ -notin $selectedDistros }) {
            if ($distro -notin $afterDistros) { throw "A preserved WSL distro was removed (check the interactive uninstaller): $distro" }
            $before = @($distroRecords | Where-Object { $_.Name -eq $distro })[0]
            $after = @($afterRecords | Where-Object { $_.Name -eq $distro })[0]
            if ($before.BasePath -ne $after.BasePath) { throw "Preserved WSL storage location changed: $distro" }
        }
        foreach ($path in $preservedStorage) {
            if (-not (Test-Path -LiteralPath $path)) { throw "Preserved WSL storage disappeared: $path" }
        }
        Write-Host 'SUCCESS: selected apps, startup entries, profiles, and opt-in targets are absent. No app was relaunched.'
    } catch {
        Write-Warning "PARTIAL OR BLOCKED CLEANUP. Do not assume a clean device. Reports: $Reports"
        throw
    } finally {
        if ($transcribing) { Stop-Transcript | Out-Null }
    }
}

Invoke-OpenClawClean -Apply ([bool]$ConfirmDestructive) -Dev ($All -or $IncludeDev) `
    -Models (($All -or $RemoveCachedModels) -and -not $ExcludeCachedModels) -Wsl ($All -or $RemoveWslGateway) `
    -Extra $AdditionalProfilePath -Reports $ReportDirectory -IsolatedProfiles $RemoveIsolatedProfilePath
