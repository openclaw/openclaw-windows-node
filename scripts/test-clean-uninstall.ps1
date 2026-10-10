#requires -Version 5.1
<#
.SYNOPSIS
    Isolated cleanup regressions. No installed apps, user profiles, WSL, or real processes are changed.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'clean-uninstall.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
# Load function definitions only. Never run the script's real inventory/entry point.
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    Invoke-Expression $definition.Extent.Text
}
$fixture = Get-CleanFullPath (Join-Path ([IO.Path]::GetTempPath()) ("openclaw-clean-tests-" + [guid]::NewGuid().ToString('N')))
$passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected error: $_" }
        return
    }
    throw "Expected rejection matching: $Pattern"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS: $Name"
}
$oldEnvironment = @{}
$environmentNames = @('APPDATA','LOCALAPPDATA','OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR',
    'OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_TRAY_LOCAL_DATA_DIR','OPENCLAW_STATE_DIR','OPENCLAW_CONFIG_PATH')
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Test-Case 'All expands only known opt-ins and never implies destructive confirmation' {
        $attributes = ($ast.ParamBlock.Attributes | ForEach-Object { $_.Extent.Text }) -join "`n"
        $entryPoint = [scriptblock]::Create(
            $attributes + "`n" + $ast.ParamBlock.Extent.Text + "`n" + $ast.EndBlock.Statements[-1].Extent.Text)
        function Invoke-OpenClawClean {
            param([bool]$Apply, [bool]$Dev, [bool]$Models, [bool]$Wsl, [string[]]$Extra, [string]$Reports, [string[]]$IsolatedProfiles)
            [pscustomobject]@{ Apply = $Apply; Dev = $Dev; Models = $Models; Wsl = $Wsl; Extra = $Extra; Reports = $Reports; WhatIf = $WhatIfPreference; IsolatedProfiles = $IsolatedProfiles }
        }
        $defaults = & $entryPoint
        Assert-True (-not $defaults.Apply -and -not $defaults.Dev -and -not $defaults.Models -and -not $defaults.Wsl) 'Default scope changed.'
        $all = & $entryPoint -all
        Assert-True (-not $all.Apply -and $all.Dev -and $all.Models -and $all.Wsl) 'All did not expand safely.'
        Assert-True (@($all.Extra).Count -eq 0) 'All invented extra profile paths.'
        Assert-True (@($all.IsolatedProfiles).Count -eq 0) 'All invented Windows profile targets.'
        $explicitProfiles = & $entryPoint -All -ExcludeCachedModels -RemoveIsolatedProfilePath @("$fixture\user-one", "$fixture\user-two")
        Assert-True (-not $explicitProfiles.Apply -and -not $explicitProfiles.Models -and $explicitProfiles.IsolatedProfiles.Count -eq 2) 'Explicit Windows profile scope changed confirmation or model selection.'
        $individual = & $entryPoint -All:$false -RemoveCachedModels
        Assert-True (-not $individual.Apply -and -not $individual.Dev -and $individual.Models -and -not $individual.Wsl) 'Individual flags stopped working.'
        $extras = @("$fixture\extra-one", "$fixture\extra-two")
        $confirmed = & $entryPoint -All -ConfirmDestructive -AdditionalProfilePath $extras -ReportDirectory "$fixture\reports"
        Assert-True ($confirmed.Apply -and $confirmed.Dev -and $confirmed.Models -and $confirmed.Wsl) 'Confirmed All lost an option.'
        Assert-True (($confirmed.Extra -join '|') -eq ($extras -join '|') -and $confirmed.Reports -eq "$fixture\reports") 'Explicit paths were not preserved.'
        $whatIf = & $entryPoint -All -ConfirmDestructive -WhatIf
        Assert-True ($whatIf.WhatIf) 'All did not propagate WhatIf.'
        $excluded = & $entryPoint -all -excludecachedmodels
        Assert-True (-not $excluded.Apply -and $excluded.Dev -and -not $excluded.Models -and $excluded.Wsl) 'Cached-model opt-out did not preserve the rest of All.'
        $explicitConflict = & $entryPoint -ExcludeCachedModels -RemoveCachedModels
        Assert-True (-not $explicitConflict.Models -and -not $explicitConflict.Apply -and -not $explicitConflict.Dev -and -not $explicitConflict.Wsl) 'Model exclusion must override explicit removal without expanding scope.'
        $allConflict = & $entryPoint -All -RemoveCachedModels -ExcludeCachedModels -ConfirmDestructive -WhatIf -AdditionalProfilePath $extras
        Assert-True ($allConflict.Apply -and $allConflict.Dev -and -not $allConflict.Models -and $allConflict.Wsl -and $allConflict.WhatIf) 'Excluded All changed confirmation, WhatIf, or scope.'
        Assert-True (($allConflict.Extra -join '|') -eq ($extras -join '|')) 'Excluded All lost explicit profile paths.'
        $exclusionOff = & $entryPoint -All -ExcludeCachedModels:$false
        Assert-True ($exclusionOff.Models) 'Explicit false exclusion disabled model removal.'
        $excludeOnly = & $entryPoint -ExcludeCachedModels
        Assert-True (-not $excludeOnly.Apply -and -not $excludeOnly.Dev -and -not $excludeOnly.Models -and -not $excludeOnly.Wsl) 'Exclusion alone expanded default scope.'
    }
    Test-Case 'root, ancestor, system, and session guards' {
        foreach ($path in @($env:USERPROFILE, $env:TEMP, 'C:\', "$env:USERPROFILE\.copilot",
            "$env:USERPROFILE\.copilot\session-state\11111111-1111-1111-1111-111111111111",
            "$env:USERPROFILE\.cache\huggingface\hub", "$env:WINDIR\System32")) {
            Assert-Throws { Assert-CleanPath $path } 'Refusing'
        }
        Test-Case 'explicit isolated profile cleanup removes registration when its folder is already absent' {
            $root = "$fixture\windows-users"
            $path = "$root\isolated-test"
            $profile = [pscustomobject]@{ LocalPath = $path; SID = 'S-1-5-110-1-2-3-1001'; Loaded = $false; Special = $false }
            $state = [pscustomobject]@{ Items = @($profile); Removed = 0 }
            function Get-ItemProperty { [pscustomobject]@{ ProfilesDirectory = "$fixture\windows-users" } }
            function Get-CimInstance { param($ClassName) Assert-True ($ClassName -eq 'Win32_UserProfile') 'Wrong CIM inventory.'; $state.Items }
            function Remove-CimInstance {
                param($InputObject, $Confirm, $ErrorAction)
                Assert-True ($InputObject -eq $profile) 'Wrong profile removal target.'
                $state.Removed++
                $state.Items = @()
            }
            $selected = @(Get-CleanIsolatedProfiles @($path, $path) @(Get-CleanWindowsProfiles))
            Assert-True ($selected.Count -eq 1 -and $state.Removed -eq 0) 'Selection mutated or duplicated a profile.'
            Remove-CleanIsolatedProfile $selected[0]
            Remove-CleanIsolatedProfile $selected[0]
            Assert-True ($state.Removed -eq 1) 'Absent profile was deleted again.'
        }
        Test-Case 'isolated profile selection rejects ordinary, loaded, special, ambiguous, and unregistered targets' {
            $root = "$fixture\windows-users"
            $profile = [pscustomobject]@{ LocalPath = "$root\isolated"; SID = 'S-1-5-110-1-2-3-1001'; Loaded = $false; Special = $false }
            function Get-ItemProperty { [pscustomobject]@{ ProfilesDirectory = "$fixture\windows-users" } }
            foreach ($sid in @('S-1-5-21-1-2-3-1001','S-1-5-18','not-a-sid')) {
                $profile.SID = $sid
                Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @($profile) } 'Refusing'
            }
            $profile.SID = 'S-1-5-110-1-2-3-1001'
            $profile.Loaded = $true
            Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @($profile) } 'Refusing'
            $profile.Loaded = $false; $profile.Special = $true
            Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @($profile) } 'Refusing'
            $profile.Special = $null
            Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @($profile) } 'Refusing'
            $profile.Special = $false
            Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @($profile, $profile) } 'Refusing'
            foreach ($path in @($root, "$root\isolated\child", $env:USERPROFILE)) {
                Assert-Throws { Get-CleanIsolatedProfiles @($path) @($profile) } 'directly under'
            }
            New-Item -ItemType Directory -Path $profile.LocalPath -Force | Out-Null
            Assert-Throws { Get-CleanIsolatedProfiles @($profile.LocalPath) @() } 'No Windows profile registration'
        }
        Test-Case 'isolated profile removal rechecks identity and propagates provider failures and leftovers' {
            $root = "$fixture\windows-users"
            $profile = [pscustomobject]@{ LocalPath = "$root\missing-files"; SID = 'S-1-5-110-1-2-3-1001'; Loaded = $false; Special = $false }
            $state = [pscustomobject]@{ Items = @($profile); Calls = 0; Fail = $false }
            function Get-ItemProperty { [pscustomobject]@{ ProfilesDirectory = "$fixture\windows-users" } }
            function Get-CleanWindowsProfiles { $state.Items }
            function Remove-CimInstance {
                param($InputObject, $Confirm, $ErrorAction)
                $state.Calls++
                if ($state.Fail) { throw 'TEST profile provider denied removal' }
            }
            $expected = @(Get-CleanIsolatedProfiles @($profile.LocalPath) $state.Items)[0]
            $profile.Loaded = $true
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'Refusing'
            $profile.Loaded = $false; $profile.SID = 'S-1-5-110-1-2-3-1002'
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'identity changed'
            $profile.SID = $expected.SID; $profile.LocalPath = "$root\moved"
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'SID moved'
            Assert-True ($state.Calls -eq 0) 'Identity rejection invoked profile deletion.'
            $profile.LocalPath = $expected.Path
            $state.Fail = $true
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'provider denied'
            $state.Fail = $false
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'registration or files remain'
            $state.Items = @()
            New-Item -ItemType Directory -Path $expected.Path -Force | Out-Null
            Assert-Throws { Remove-CleanIsolatedProfile $expected } 'No Windows profile registration'
        }
        foreach ($path in @('', '.', 'C:relative', '\\server\share', "$fixture\*", "$fixture\x:stream")) {
            Assert-Throws { Assert-CleanPath $path } 'absolute local literal'
        }
        foreach ($path in @("$env:TEMP.", "$fixture\..", "$fixture\NUL.txt", "$fixture\folder ", "$fixture\USERNA~1")) {
            Assert-Throws { Assert-CleanPath $path } 'ambiguous Windows'
        }
        Assert-Throws { Assert-CleanPath (Join-Path (Split-Path $env:USERPROFILE) 'UnrelatedUser\Documents') } "another user's"
    }
    Test-Case 'protected short paths expand and unrelated UNC locations do not block local targets' {
        $oldTemp = $env:TEMP
        $fso = New-Object -ComObject Scripting.FileSystemObject
        try {
            $short = $fso.GetFolder($fixture).ShortPath
            $env:TEMP = $short
            Assert-True ((Get-CleanFullPath "$short\future-child") -eq "$fixture\future-child") 'Short path was not expanded.'
            $null = Assert-CleanPath "$fixture\future-child"
            Assert-Throws { Assert-CleanPath $short } 'protected'
            $env:TEMP = '\\uncontacted-server\redirected-folder'
            $null = Assert-CleanPath "$fixture\local-child"
        } finally {
            $env:TEMP = $oldTemp
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($fso) | Out-Null
        }
    }
    Test-Case 'real Inno display names and publisher identity are detected' {
        $keyName = '{M0LTB0T-TRAY-4PP1-D3N7}_is1'
        $entry = [pscustomobject]@{
            DisplayName = 'OpenClaw Companion version 2026.9.7'
            DisplayVersion = '2026.9.7'; Publisher = 'OpenClaw Foundation'
            InstallLocation = "$fixture\inno-install"; UninstallString = "`"$fixture\inno-install\unins000.exe`""
        }
        function Test-Path {
            param($LiteralPath, $PathType)
            return $LiteralPath -eq 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -or
                $LiteralPath -eq "$fixture\inno-install\unins000.exe"
        }
        function Get-ChildItem { [pscustomobject]@{ PSPath = 'fake-key'; PSChildName = $keyName } }
        function Get-ItemProperty { $entry }
        $apps = @(Get-CleanWin32 $false)
        Assert-True ($apps.Count -eq 1 -and $apps[0].Exe -eq "$fixture\inno-install\unins000.exe") 'Real Inno registration was missed.'
        foreach ($version in @('2026.9.7-preview.1', '2026.9.7-1')) {
            $entry.DisplayVersion = $version
            $entry.DisplayName = "OpenClaw Companion version $version"
            Assert-True (@(Get-CleanWin32 $false).Count -eq 1) 'Supported Inno version was missed.'
        }
        $entry.DisplayVersion = '2026.9.7'
        $keyName = '{M0LTB0T-TRAY-4PP1-DEV}_is1'
        $entry.DisplayName = 'OpenClaw Companion (Dev) version 2026.9.7'
        Assert-True (@(Get-CleanWin32 $false).Count -eq 0) 'Release cleanup selected dev Inno.'
        Assert-True (@(Get-CleanWin32 $true).Count -eq 1) 'Dev Inno was missed.'
        $entry.Publisher = 'Unexpected publisher'
        Assert-Throws { Get-CleanWin32 $true } 'identity is inconsistent'
        $entry.Publisher = 'OpenClaw Foundation'
        $keyName = 'unknown-inno-key'
        Assert-Throws { Get-CleanWin32 $true } 'Unrecognized Companion'
    }
    Test-Case 'unrelated uninstall metadata is not interpreted as a cleanup target' {
        function Test-Path { param($LiteralPath) return $LiteralPath -like 'HKCU:*' }
        function Get-ChildItem { [pscustomobject]@{ PSPath = 'live-key'; PSChildName = 'OtherApplication' } }
        function Get-ItemProperty { [pscustomobject]@{ DisplayName = 'OtherApplication'; InstallLocation = $location } }
        foreach ($location in @('C:/Unrelated', 'C:\', '%ProgramFiles%\Foo', 'C:\Unknown~1\App', 'C:\TrailingSpace ')) {
            Assert-True (@(Get-CleanWin32 $true).Count -eq 0) 'Unrelated install metadata blocked cleanup.'
        }
    }
    Test-Case 'WSL inventory retains storage paths and guards preserved disks' {
        $entry = [pscustomobject]@{ DistributionName = 'OpenClawGateway'; BasePath = "\\?\$fixture\profile\wsl\OpenClawGateway" }
        function Test-Path { param($LiteralPath) return $LiteralPath -like 'HKCU:*' }
        function Get-ChildItem { [pscustomobject]@{ PSPath = 'wsl-key' } }
        function Get-ItemProperty { $entry }
        $records = @(Get-CleanDistroRecords)
        Assert-True ($records[0].BasePath -eq "$fixture\profile\wsl\OpenClawGateway") 'WSL BasePath was lost.'
        Assert-Throws { Assert-CleanDistroStorage $records @("$fixture\profile") } 'Preserved WSL storage overlaps'
        Assert-CleanDistroStorage $records @("$fixture\profile") @('OpenClawGateway')
        Assert-Throws { Assert-CleanDistroStorage $records @("$fixture\profile\wsl\OpenClawGateway\child") } 'Preserved WSL storage overlaps'
        $entry.DistributionName = 'UnrelatedDistro'
        $entry.BasePath = "$fixture\hub"
        Assert-CleanDistroStorage @(Get-CleanDistroRecords) @("$fixture\profile")
        $entry.BasePath = 'Z:\'
        Assert-CleanDistroStorage @(Get-CleanDistroRecords) @("$fixture\profile")
        $entry.BasePath = $null
        Assert-Throws { Get-CleanDistroRecords } 'absolute local literal'
    }
    Test-Case 'startup commands normalize quoting and reject unowned or ambiguous executables' {
        $run = [pscustomobject]@{ OpenClawTray = "`"$fixture\selected\OpenClaw.Tray.WinUI.exe`" --background" }
        $task = [pscustomobject]@{
            TaskName = 'OpenClaw Companion'; TaskPath = '\'
            Principal = [pscustomobject]@{ UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
            Actions = @([pscustomobject]@{ Execute = "`"$fixture\selected\OpenClaw.Tray.WinUI.exe`"" })
        }
        function Test-Path { param($LiteralPath) return $LiteralPath -like 'HKCU:*' }
        function Get-ItemProperty { $run }
        function Get-ScheduledTask { $task }
        function Export-ScheduledTask { 'fixture task XML' }
        Assert-True (@(Get-CleanStartup $false @("$fixture\selected")).Count -eq 2) 'Quoted owned startup was rejected.'
        $run.OpenClawTray = "`"$fixture\unselected\OpenClaw.Tray.WinUI.exe`" --background"
        Assert-Throws { Get-CleanStartup $false @("$fixture\selected") } 'outside selected ownership'
        $run.OpenClawTray = "`"$fixture\selected\OpenClaw.Tray.WinUI.exe`" --background ; evil"
        Assert-Throws { Get-CleanStartup $false @("$fixture\selected") } 'not a supported Companion'
        $run.OpenClawTray = "$fixture\selected\OpenClaw.Tray.WinUI.exe"
        $task.Principal.UserId = ''
        Assert-Throws { Get-CleanStartup $false @("$fixture\selected") } 'no identifiable principal'
    }
    Test-Case 'tree inventory includes hidden files but refuses source checkout' {
        $tree = Join-Path $fixture 'tree'
        New-Item -ItemType Directory -Path $tree | Out-Null
        Set-Content -LiteralPath "$tree\one.txt" -Value 'safe'
        Assert-True (@(Get-CleanTree $tree).Count -eq 2) 'Tree inventory omitted a file.'
        Set-Content -LiteralPath "$tree\.git" -Value 'gitdir: elsewhere'
        Assert-Throws { @(Get-CleanTree $tree) } 'source checkout'
    }
    Test-Case 'junctions and ancestor junctions are rejected without traversing' {
        $outside = Join-Path $fixture 'outside'
        $inside = Join-Path $fixture 'inside'
        New-Item -ItemType Directory -Path $outside,$inside | Out-Null
        Set-Content -LiteralPath "$outside\keep.txt" -Value 'preserve'
        $junction = Join-Path $inside 'link'
        New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
        try {
            Assert-Throws { @(Get-CleanTree $inside) } 'reparse'
            Assert-Throws { Assert-CleanPath "$junction\keep.txt" } 'reparse'
            Assert-True (Test-Path -LiteralPath "$outside\keep.txt") 'Junction destination changed.'
        } finally {
            # Delete the junction itself, never its target.
            [IO.Directory]::Delete($junction)
        }
    }
    Test-Case 'schema 4 and 5 select only exact receipt-backed cache files' {
        $cache = Join-Path $fixture 'cache'
        $snapshot = Join-Path $cache ('models--test--model\snapshots\' + ('a' * 40))
        $localAi = Join-Path $fixture 'profile\LocalAI'
        New-Item -ItemType Directory -Path $snapshot,$localAi -Force | Out-Null
        Set-Content -LiteralPath "$snapshot\model.gguf" -Value 'model'
        Set-Content -LiteralPath "$snapshot\draft.gguf" -Value 'draft'
        $asset = [ordered]@{ FileName = 'model.gguf'; SourceUrl = "https://huggingface.co/test/model/resolve/$('a' * 40)/model.gguf"; SizeBytes = (Get-Item "$snapshot\model.gguf").Length; Sha256 = (Get-FileHash "$snapshot\model.gguf").Hash }
        $manifest = [ordered]@{ SchemaVersion = 4; ModelId = "test/model@$('a' * 40)"; ModelCacheRoot = $cache; CachedModelPath = "$snapshot\model.gguf"; ModelAsset = $asset }
        $receiptFile = "$localAi\state.json"
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile
        Assert-True (@(Get-CleanModels @((Get-Item $receiptFile))).Count -eq 1) 'Schema 4 lost model.'
        $manifest.SchemaVersion = 5
        $manifest.AdditionalModelPaths = @("$snapshot\draft.gguf")
        $manifest.AdditionalModelAssets = @([ordered]@{ FileName = 'draft.gguf'; SourceUrl = "https://huggingface.co/test/model/resolve/$('a' * 40)/draft.gguf"; SizeBytes = (Get-Item "$snapshot\draft.gguf").Length; Sha256 = (Get-FileHash "$snapshot\draft.gguf").Hash })
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile
        Assert-True (@(Get-CleanModels @((Get-Item $receiptFile))).Count -eq 2) 'Schema 5 lost additional asset.'
        $manifest.CachedModelPath = "$fixture\unrelated.gguf"
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'exact Hugging Face'
        $manifest.CachedModelPath = "$snapshot\model.gguf"
        $manifest.ModelAsset.SizeBytes = 1
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptFile
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'size/type mismatch'
    }
    Test-Case 'nested camelCase receipts validate primary and independent additional provenance' {
        $cache = "$fixture\nested-cache"
        $revision = 'b' * 40
        $primary = "$cache\models--test--primary\snapshots\$revision\weights\model.gguf"
        $additional = "$cache\models--test--draft\snapshots\$revision\checkpoints\draft.gguf"
        $receiptFile = "$fixture\nested-profile\LocalAI\state.json"
        foreach ($path in @($primary, $additional, $receiptFile)) {
            New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        }
        Set-Content -LiteralPath $primary -Value 'primary'
        Set-Content -LiteralPath $additional -Value 'draft'
        $manifest = @{
            schemaVersion = 5; modelId = "test/primary@$revision"; modelCacheRoot = $cache; cachedModelPath = $primary
            modelAsset = @{ fileName = 'model.gguf'; sourceUrl = "https://huggingface.co/test/primary/resolve/$revision/weights/model.gguf?download=true"; sizeBytes = (Get-Item $primary).Length; sha256 = (Get-FileHash $primary).Hash }
            additionalModelPaths = @($additional)
            additionalModelAssets = @(@{ fileName = 'draft.gguf'; sourceUrl = "https://huggingface.co/test/draft/resolve/$revision/checkpoints/draft.gguf"; sizeBytes = (Get-Item $additional).Length; sha256 = (Get-FileHash $additional).Hash })
        }
        function Save-Receipt { $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptFile }
        Save-Receipt
        Assert-True (@(Get-CleanModels @((Get-Item $receiptFile))).Count -eq 2) 'Nested/camelCase/additional model contract failed.'
        $manifest.modelId = "test/wrong@$revision"
        Save-Receipt
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'provenance disagrees'
        $manifest.modelId = "test/primary@$revision"
        $originalSource = $manifest.modelAsset.sourceUrl
        $manifest.modelAsset.sourceUrl = "https://huggingface.co/test/primary/resolve/$revision/%2e%2e/model.gguf"
        Save-Receipt
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'unsafe path segment'
        $manifest.modelAsset.sourceUrl = $originalSource
        $manifest.additionalModelAssets[0].sourceUrl = "https://huggingface.co/test/wrong/resolve/$revision/checkpoints/draft.gguf"
        Save-Receipt
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'exact Hugging Face'
        $manifest.additionalModelAssets[0].sourceUrl = "https://huggingface.co/test/draft/resolve/$revision/checkpoints/draft.gguf"
        $manifest.cachedModelPath = $primary.Replace('models--test--primary', 'models--test--wrong')
        New-Item -ItemType Directory -Path (Split-Path $manifest.cachedModelPath) -Force | Out-Null
        Copy-Item -LiteralPath $primary -Destination $manifest.cachedModelPath
        Save-Receipt
        Assert-Throws { Get-CleanModels @((Get-Item $receiptFile)) } 'exact Hugging Face'
    }
    Test-Case 'PID reuse never stops a replacement process' {
        function Get-CimInstance { [pscustomobject]@{ ProcessId = 42; ExecutablePath = 'C:\other.exe'; CreationDate = 'new' } }
        function Stop-Process { throw 'TEST FAILURE: a real stop was attempted' }
        Assert-Throws { Stop-CleanProcess ([pscustomobject]@{ ProcessId = 42; ExecutablePath = 'C:\owned.exe'; CreationDate = 'old' }) } 'changed identity'
    }
    Test-Case 'process ownership compares short and long paths without changing PID identity observations' {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        try {
            $short = $fso.GetFolder($fixture).ShortPath
            $candidate = [pscustomobject]@{ Name = 'OpenClaw.Tray.WinUI.exe'; ExecutablePath = "$fixture\OpenClaw.Tray.WinUI.exe"; ProcessId = 456; CreationDate = 'original-time' }
            function Get-CimInstance { $candidate }
            $found = @(Get-CleanProcesses @($short))
            Assert-True ($found.Count -eq 1 -and $found[0].ExecutablePath -eq $candidate.ExecutablePath -and $found[0].CreationDate -eq 'original-time') 'Process identity or ownership changed.'
        } finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($fso) | Out-Null
        }
    }
    Test-Case 'native capture preserves nonzero exit codes and bounds waits' {
        $powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $result = Invoke-CleanNativeCommand $powerShell '-NoProfile -Command "[Console]::Out.WriteLine(''out''); [Console]::Error.WriteLine(''err''); exit 7"'
        Assert-True ($result.ExitCode -eq 7 -and $result.Stdout.Trim() -eq 'out' -and $result.Stderr.Trim() -eq 'err') 'Native output/exit code was lost.'
        Assert-Throws { Invoke-CleanNativeCommand $powerShell '-NoProfile -Command "Start-Sleep -Seconds 30"' 200 } 'timed out'
    }

    # All OS-facing operations are mocked for lifecycle tests.
    foreach ($name in $environmentNames | Where-Object { $_ -notin @('APPDATA','LOCALAPPDATA') }) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    $env:APPDATA = Join-Path $fixture 'roaming'
    $env:LOCALAPPDATA = Join-Path $fixture 'local'
    New-Item -ItemType Directory -Path $env:APPDATA,$env:LOCALAPPDATA | Out-Null
    $script:events = [Collections.Generic.List[string]]::new()
    $script:registered = $true
    $script:teardownFails = $false
    $script:gateway = [pscustomobject]@{
        Name = 'OpenClawFoundation.OpenClawGateway'
        PackageFullName = 'OpenClawFoundation.OpenClawGateway_test'
        PackageFamilyName = 'OpenClawFoundation.OpenClawGateway_123456789abcd'
        InstallLocation = "$fixture\installed"
    }
    $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($gateway.PackageFamilyName)\clawctl.exe"
    New-Item -ItemType Directory -Path (Split-Path $alias) -Force | Out-Null
    Set-Content -LiteralPath $alias -Value 'not executable'
    Test-Case 'native teardown checks structured result as well as process exit code' {
        $reports = Join-Path $fixture 'native-contract'
        New-Item -ItemType Directory -Path $reports | Out-Null
        $script:nativeExit = 0
        $script:nativeJson = '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"removed"}}'
        function Invoke-CleanNativeCommand {
            param($FilePath, $Arguments)
            Assert-True ($FilePath -eq $alias) 'Teardown did not use the package-qualified alias.'
            Assert-True ($Arguments -eq 'teardown --force --json') 'Wrong native teardown arguments.'
            return [pscustomobject]@{ ExitCode = $script:nativeExit; Stdout = $script:nativeJson; Stderr = '' }
        }
        Invoke-CleanTeardown $gateway $reports
        foreach ($json in @('{"ok":false}', '{"ok":"true"}',
            '{"ok":true,"command":"teardown","gateway":{"state":"running"},"session":{"state":"removed"}}',
            '{"ok":true,"command":"setup","gateway":{"state":"records-removed"},"session":{"state":"removed"}}')) {
            $script:nativeJson = $json
            Assert-Throws { Invoke-CleanTeardown $gateway $reports } 'did not confirm success'
        }
        $script:nativeExit = 1
        $script:nativeJson = '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"not-configured"}}'
        Assert-Throws { Invoke-CleanTeardown $gateway $reports } 'did not confirm success'
    }
    $script:distroInventory = @('UnrelatedDistro')
    $script:distroPaths = @{}
    function Get-CleanPackages { if ($script:registered) { $script:gateway } }
    function Get-CleanWin32 { }
    function Get-CleanWindowsProfiles { }
    function Get-CleanDistroRecords {
        foreach ($name in $script:distroInventory) {
            $base = if ($script:distroPaths.ContainsKey($name)) { $script:distroPaths[$name] } else { "$fixture\wsl-outside\$name" }
            [pscustomobject]@{ Name = $name; BasePath = $base }
        }
    }
    function Get-CleanDistros { $script:distroInventory }
    function wsl.exe {
        Assert-True ($args.Count -eq 2 -and $args[0] -eq '--unregister') 'Unexpected WSL command.'
        $script:events.Add("unregister:$($args[1])")
        $target = $args[1]
        $script:distroInventory = @($script:distroInventory | Where-Object { $_ -ne $target })
        $global:LASTEXITCODE = 0
    }
    function Get-CleanProcesses { }
    function Get-CleanStartup { }
    function Invoke-CleanTeardown {
        $script:events.Add('teardown')
        if ($script:teardownFails) { throw 'TEST native teardown failed' }
    }
    function Remove-AppxPackage {
        $script:events.Add('package')
        Assert-True ($script:events[0] -eq 'teardown') 'Package removed before native teardown.'
        $script:registered = $false
    }
    function Start-Transcript { }
    function Stop-Transcript { }
    function New-Profile {
        New-Item -ItemType Directory -Path "$env:APPDATA\OpenClawTray" -Force | Out-Null
        Set-Content -LiteralPath "$env:APPDATA\OpenClawTray\settings.json" -Value '{}'
    }
    New-Profile
    Test-Case 'explicit Windows profiles obey preview, WhatIf, elevation, and WSL preservation gates' {
        $root = "$fixture\windows-users"
        $path = "$root\explicit-lifecycle"
        $profile = [pscustomobject]@{ LocalPath = $path; SID = 'S-1-5-110-1-2-3-1001'; Loaded = $false; Special = $false }
        function Get-ItemProperty { [pscustomobject]@{ ProfilesDirectory = "$fixture\windows-users" } }
        function Get-CleanWindowsProfiles { $profile }
        function Assert-CleanProfileRemovalAccess { throw 'TEST elevation required' }
        function Remove-CimInstance { throw 'Preview invoked Windows profile removal' }
        Invoke-OpenClawClean -Apply $false -Dev $true -Models $false -Wsl $true -Extra @() -IsolatedProfiles @($path)
        Invoke-OpenClawClean -Apply $true -Dev $true -Models $false -Wsl $true -Extra @() -IsolatedProfiles @($path) -WhatIf
        Assert-Throws {
            Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -IsolatedProfiles @($path)
        } 'elevation required'
        Assert-True ($events.Count -eq 0) 'Elevation gate ran after native mutation.'
        $script:distroPaths['UnrelatedDistro'] = "$path\wsl"
        try {
            Assert-Throws {
                Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -IsolatedProfiles @($path)
            } 'Preserved WSL storage overlaps'
        } finally { $script:distroPaths.Clear() }
    }
    Test-Case 'default cleanup blocks before mutation when a preserved WSL disk is inside a profile' {
        $script:distroInventory = @('OpenClawGateway', 'UnrelatedDistro')
        $script:distroPaths['OpenClawGateway'] = "$env:LOCALAPPDATA\OpenClawTray\wsl\OpenClawGateway"
        New-Item -ItemType Directory -Path $script:distroPaths['OpenClawGateway'] -Force | Out-Null
        $disk = Join-Path $script:distroPaths['OpenClawGateway'] 'ext4.vhdx'
        Set-Content -LiteralPath $disk -Value 'fixture disk, not a real VHDX'
        Assert-Throws { Invoke-OpenClawClean -Apply $false -Dev $false -Models $false -Wsl $false -Extra @() } 'Preserved WSL storage overlaps'
        Assert-Throws { Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() } 'Preserved WSL storage overlaps'
        Assert-True ($events.Count -eq 0 -and (Test-Path -LiteralPath $disk)) 'Preserved WSL storage was mutated.'
        $script:distroPaths.Clear()
        $script:distroInventory = @('UnrelatedDistro')
    }
    Test-Case 'default preview and WhatIf never mutate targets or create reports' {
        $report = Join-Path $fixture 'dry-reports'
        Invoke-OpenClawClean -Apply $false -Dev $false -Models $false -Wsl $false -Extra @() -Reports $report
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports $report -WhatIf
        Assert-True ($events.Count -eq 0) 'Preview invoked a mutator.'
        Assert-True (-not (Test-Path $report)) 'Preview created reports.'
        Assert-True (Test-Path "$env:APPDATA\OpenClawTray\settings.json") 'Preview removed state.'
    }
    Test-Case 'path overrides block cleanup before native mutation' {
        $env:OPENCLAW_STATE_DIR = $fixture
        try {
            Assert-Throws { Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() } 'Clear path overrides'
        } finally { $env:OPENCLAW_STATE_DIR = $null }
        Assert-True ($events.Count -eq 0) 'Override rejection invoked a mutator.'
    }
    Test-Case 'failed native teardown preserves packages and profile' {
        $script:teardownFails = $true
        Assert-Throws { Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports "$fixture\failed-reports" } 'TEST native teardown failed'
        Assert-True ($script:registered) 'Failed teardown removed package.'
        Assert-True (Test-Path "$env:APPDATA\OpenClawTray\settings.json") 'Failed teardown removed profile.'
        $script:teardownFails = $false
        $events.Clear()
    }
    Test-Case 'successful cleanup orders teardown before package/state removal and retains unrelated data' {
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports "$fixture\success-reports"
        Assert-True (($events -join ',') -eq 'teardown,package') 'Unexpected lifecycle order.'
        Assert-True (-not (Test-Path "$env:APPDATA\OpenClawTray")) 'Profile remains.'
        Assert-True (Test-Path "$fixture\outside\keep.txt") 'Unrelated data was deleted.'
        Assert-True (Test-Path "$fixture\success-reports\plan.json") 'Plan not persisted.'
    }
    Test-Case 'repeat cleanup is idempotent on absent targets' {
        $events.Clear()
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports "$fixture\repeat-reports"
        Assert-True ($events.Count -eq 0) 'Absent package was acted on.'
    }
    Test-Case 'WSL deletion is opt-in and exact, with dev distro preserved by default' {
        $script:distroInventory = @('OpenClawGateway', 'OpenClawGateway-Dev', 'UnrelatedDistro', 'OpenClawGateway-extra')
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $true -Extra @() -Reports "$fixture\wsl-reports"
        Assert-True (($events -join ',') -eq 'unregister:OpenClawGateway') 'WSL scope widened.'
        Assert-True (($script:distroInventory -join ',') -eq 'OpenClawGateway-Dev,UnrelatedDistro,OpenClawGateway-extra') 'Preserved WSL registrations changed.'
    }
    Test-Case 'Inno removing an approved WSL distro does not abort residual cleanup' {
        $events.Clear()
        New-Profile
        $script:win32Registered = $true
        $script:distroInventory = @('OpenClawGateway', 'UnrelatedDistro')
        function Get-CleanWin32 {
            if ($script:win32Registered) {
                [pscustomobject]@{ Name = 'OpenClaw Companion'; Key = 'fake-inno-key'; Install = "$fixture\inno"; Exe = "$fixture\inno\unins000.exe" }
            }
        }
        function Invoke-CleanWin32 {
            $script:events.Add('inno')
            $script:win32Registered = $false
            $script:distroInventory = @('UnrelatedDistro')
        }
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $true -Extra @() -Reports "$fixture\inno-wsl-reports"
        Assert-True (($events -join ',') -eq 'inno') 'An already-removed distro was unregistered again.'
        Assert-True (-not (Test-Path "$env:APPDATA\OpenClawTray")) 'Inno WSL removal prevented profile cleanup.'
    }
    Test-Case 'still-registered selected Inno app blocks profile deletion' {
        $script:registered = $false
        New-Profile
        function Get-CleanWin32 {
            [pscustomobject]@{ Name = 'OpenClaw Companion'; Install = "$env:LOCALAPPDATA\OpenClawTray"; Exe = "$env:LOCALAPPDATA\OpenClawTray\unins000.exe" }
        }
        function Invoke-CleanWin32 { }
        Assert-Throws {
            Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports "$fixture\registered-inno-reports"
        } 'still registered'
        Assert-True (Test-Path -LiteralPath "$env:APPDATA\OpenClawTray\settings.json") 'Profile removed while Inno registration remained.'
    }
    Test-Case 'remaining package data blocks deletion instead of claiming a clean device' {
        $events.Clear()
        New-Profile
        $script:registered = $true
        $leftover = Join-Path $env:LOCALAPPDATA "Packages\$($gateway.PackageFamilyName)\LocalState"
        New-Item -ItemType Directory -Path $leftover -Force | Out-Null
        Set-Content -LiteralPath "$leftover\remaining.txt" -Value 'retained package data'
        Assert-Throws { Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -Reports "$fixture\package-remnant-reports" } 'Package data remains'
        Assert-True (Test-Path "$env:APPDATA\OpenClawTray\settings.json") 'Profiles removed despite incomplete package cleanup.'
        Assert-True (Test-Path "$leftover\remaining.txt") 'Package-managed data was deleted manually.'
    }
    Test-Case 'confirmed isolated profile removal records exact scope and preserves unselected profiles' {
        $script:registered = $false
        $root = "$fixture\windows-users"
        $path = "$root\selected-profile"
        $selected = [pscustomobject]@{ LocalPath = $path; SID = 'S-1-5-110-1-2-3-1001'; Loaded = $false; Special = $false }
        $unselected = [pscustomobject]@{ LocalPath = "$root\unselected-profile"; SID = 'S-1-5-110-1-2-3-1002'; Loaded = $false; Special = $false }
        $state = [pscustomobject]@{ Items = @($selected, $unselected); Calls = 0 }
        foreach ($directory in @($selected.LocalPath, $unselected.LocalPath)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Set-Content -LiteralPath "$directory\keep-or-remove.txt" -Value 'fixture profile data'
        }
        function Get-ItemProperty { [pscustomobject]@{ ProfilesDirectory = "$fixture\windows-users" } }
        function Get-CleanWindowsProfiles { $state.Items }
        function Assert-CleanProfileRemovalAccess { }
        function Remove-CimInstance {
            param($InputObject, $Confirm, $ErrorAction)
            Assert-True ($InputObject.SID -eq $selected.SID) 'Unselected profile reached deletion.'
            $state.Calls++
            Remove-Item -LiteralPath $selected.LocalPath -Recurse -Force
            $state.Items = @($unselected)
        }
        Assert-Throws {
            Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -IsolatedProfiles @($path) -Reports "$path\report"
        } 'Report directory overlaps'
        Assert-True ($state.Calls -eq 0) 'Report overlap invoked profile removal.'
        Invoke-OpenClawClean -Apply $true -Dev $false -Models $false -Wsl $false -Extra @() -IsolatedProfiles @($path) -Reports "$fixture\profile-removal-report"
        $plan = Get-Content -LiteralPath "$fixture\profile-removal-report\plan.json" -Raw | ConvertFrom-Json
        Assert-True ($plan.isolatedWindowsProfiles.Count -eq 1 -and $plan.isolatedWindowsProfiles[0].SID -eq $selected.SID) 'Explicit SID missing from plan.'
        Assert-True ($state.Calls -eq 1 -and -not (Test-Path -LiteralPath $path)) 'Selected profile was not removed.'
        Assert-True (Test-Path -LiteralPath "$root\unselected-profile\keep-or-remove.txt") 'Unselected profile files were changed.'
    }
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
Write-Host "Clean uninstall regressions passed: $passed"
