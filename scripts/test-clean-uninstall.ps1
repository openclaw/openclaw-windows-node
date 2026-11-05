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
        Test-Case 'isolated script routing cannot accidentally invoke ordinary cleanup' {
            function Invoke-CleanPatchLocked { param([scriptblock]$Action) & $Action }
            function Invoke-OpenClawClean { throw 'Ordinary cleanup was selected.' }
            function Invoke-OpenClawIsolatedClean {
                param($Apply, $Patch, $FullName, $Layout, $Profiles, $Companion, $ExpectedSha256)
                [pscustomobject]@{ Apply=$Apply; Patch=$Patch; FullName=$FullName; Layout=$Layout; Profiles=$Profiles; Companion=$Companion; Digest=$ExpectedSha256; WhatIf=$WhatIfPreference }
            }
            $scope = @{
                IsolatedOnly=$true; GatewayPatch='pr225-test'; GatewayPackageFullName='exact-registration'
                GatewayInstallLocation="$fixture\layout"; CompanionExecutablePath="$fixture\owned-build\OpenClaw.Tray.WinUI.exe"
                DisposableProfilePath=@("$fixture\one","$fixture\two")
            }
            $preview = & $entryPoint @scope -ExcludeCachedModels
            Assert-True (-not $preview.Apply -and $preview.Patch -eq 'pr225-test' -and $preview.Profiles.Count -eq 2) 'Isolated preview scope changed.'
            $apply = & $entryPoint @scope -ConfirmDestructive -ExpectedPreviewSha256 ('a' * 64) -WhatIf
            Assert-True ($apply.Apply -and $apply.WhatIf -and $apply.Digest -eq ('a' * 64) -and
                $apply.FullName -eq $scope.GatewayPackageFullName -and $apply.Companion -eq $scope.CompanionExecutablePath) 'Explicit isolated identity or confirmation lost.'
            foreach ($option in @('All','IncludeDev','RemoveCachedModels','RemoveWslGateway')) {
                $conflict = @{}; $conflict[$option]=$true
                Assert-Throws { & $entryPoint @scope @conflict } 'cannot be combined'
            }
            Assert-Throws { & $entryPoint @scope -AdditionalProfilePath "$fixture\extra" } 'cannot be combined'
            Assert-Throws { & $entryPoint @scope -RemoveIsolatedProfilePath "$fixture\user" } 'cannot be combined'
            Assert-Throws { & $entryPoint @scope -ReportDirectory "$fixture\reports" } 'cannot be combined'
            Assert-Throws { & $entryPoint -GatewayPatch 'pr225-test' } 'require -IsolatedOnly'
        }
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
    Test-Case 'isolated-only cleanup exercises owned patch lifecycle without ordinary cleanup' {
        $patch = 'pr225-test'
        $layout = "$fixture\patch-layout"
        $profile = "$fixture\disposable-companion"
        $companion = "$fixture\owned-build\OpenClaw.Tray.WinUI.exe"
        $fullName = 'OpenClawFoundation.OpenClawGateway-pr225-test_1.2.3.4_x64__123456789abcd'
        $package = [pscustomobject]@{
            Name = 'OpenClawFoundation.OpenClawGateway-pr225-test'
            PackageFullName = $fullName
            PackageFamilyName = 'OpenClawFoundation.OpenClawGateway-pr225-test_123456789abcd'
            PublisherId = '123456789abcd'
            Publisher = 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC'
            IsDevelopmentMode = $true; InstallLocation = $layout
        }
        $normalPackage = [pscustomobject]@{
            Name = 'OpenClawFoundation.OpenClawGateway'; InstallLocation = "$fixture\store"
        }
        $otherPatch = [pscustomobject]@{
            Name = 'OpenClawFoundation.OpenClawGateway-other'; InstallLocation = "$fixture\other-patch"
        }
        $state = [pscustomobject]@{
            Packages = @($package, $normalPackage, $otherPatch); Processes = @(); Distros = @()
            Events = [Collections.Generic.List[string]]::new()
            Json = '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"removed"}}'
            Exit = 0; DuringTeardown = $null; LeaveSession = $false
            SelectedProfiles = @($profile); FailDeletePath = $null
        }
        $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($package.PackageFamilyName)\clawctl-$patch.exe"
        $sessionPath = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\OpenClawGatewayMSIX\session.json"
        $appId = "PFN:$($package.PackageFamilyName)"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"appId":"' + $appId + '","id":"opaque-fixture"}')).TrimEnd('=').Replace('+','-').Replace('/','_')
        $record = [ordered]@{ schemaVersion = 2; applicationId = $appId; generation = 'fixture-generation'; sandboxId = "iso:$encoded" }
        $manifest = '<Package><Identity Version="1.2.3.4" ProcessorArchitecture="x64" Name="' + $package.Name + '" Publisher="' + $package.Publisher +
            '"/><Applications><ExecutionAlias Alias="clawctl-pr225-test.exe"/><ExecutionAlias Alias="openclaw-pr225-test.exe"/></Applications></Package>'
        foreach ($directory in @($layout, $profile, (Split-Path $companion), (Split-Path $alias), (Split-Path $sessionPath),
            "$env:APPDATA\OpenClawTray", "$fixture\shared-cache", "$fixture\other-profile")) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        Set-Content -LiteralPath $companion -Value 'fixture executable, never launched'
        Set-Content -LiteralPath $alias -Value 'fixture alias, never launched'
        Set-Content -LiteralPath "$layout\AppxManifest.xml" -Value $manifest
        Set-Content -LiteralPath "$env:APPDATA\OpenClawTray\preserve.txt" -Value 'ordinary profile fixture'
        Set-Content -LiteralPath "$fixture\shared-cache\weights.gguf" -Value 'shared model fixture'
        Set-Content -LiteralPath "$fixture\other-profile\preserve.txt" -Value 'other session fixture'
        function Reset-IsolatedFixture {
            $state.Packages = @($package, $normalPackage, $otherPatch)
            $state.Processes = @(); $state.Distros = @(); $state.Events.Clear()
            $state.Exit = 0; $state.DuringTeardown = $null; $state.LeaveSession = $false
            $state.SelectedProfiles = @($profile)
            $state.FailDeletePath = $null
            $state.Json = '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"removed"}}'
            New-Item -ItemType Directory -Path $profile -Force | Out-Null
            Set-Content -LiteralPath "$profile\disposable.txt" -Value 'disposable fixture'
            $record | ConvertTo-Json | Set-Content -LiteralPath $sessionPath
        }
        function Get-AppxPackage { $state.Packages }
        function Get-CleanWin32 { }
        function Get-CimInstance { param($ClassName) Assert-True ($ClassName -eq 'Win32_Process') 'Unexpected live inventory.'; $state.Processes }
        function Get-CleanDistroRecords { $state.Distros }
        function Stop-Process { throw 'Isolated mode must not stop any process.' }
        function Remove-Item {
            [CmdletBinding()]
            param([string[]]$LiteralPath, [switch]$Recurse, [switch]$Force)
            if ($state.FailDeletePath -and $LiteralPath.Count -eq 1 -and
                $LiteralPath[0].Equals($state.FailDeletePath, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'TEST profile deletion blocked by fixture.'
            }
            Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
        }
        function Invoke-CleanNativeCommand {
            param($FilePath, $Arguments)
            Assert-True ($FilePath -eq $alias -and $Arguments -eq 'teardown --force --json') 'Wrong patch CLI or command.'
            $state.Events.Add('teardown')
            if ($state.DuringTeardown) { & $state.DuringTeardown }
            if (-not $state.LeaveSession -and $state.Exit -eq 0 -and $state.Json -match '"ok":true') {
                Remove-Item -LiteralPath $sessionPath -ErrorAction SilentlyContinue
            }
            [pscustomobject]@{ ExitCode = $state.Exit; Stdout = $state.Json; Stderr = 'fixture stderr' }
        }
        function Remove-AppxPackage {
            param($Package)
            Assert-True ($Package -ceq $fullName) 'Removed unrelated package.'
            Assert-True ($state.Events.Count -ge 1 -and $state.Events[$state.Events.Count - 1] -eq 'teardown') 'Unregistered before teardown.'
            Assert-True (@($state.SelectedProfiles | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0) 'Unregistered before disposable profiles were removed.'
            $state.Events.Add('unregister')
            $state.Packages = @($normalPackage, $otherPatch)
        }
        function Get-IsolatedPlan([string[]]$Profiles = @($profile)) {
            $state.SelectedProfiles = $Profiles
            Get-CleanIsolatedPlan $patch $fullName $layout $Profiles $companion
        }
        function Invoke-IsolatedApply([string]$Digest, [string[]]$Profiles = @($profile)) {
            $state.SelectedProfiles = $Profiles
            Invoke-OpenClawIsolatedClean -Apply $true -Patch $patch -FullName $fullName -Layout $layout `
                -Profiles $Profiles -Companion $companion -ExpectedSha256 $Digest
        }
        Reset-IsolatedFixture
        Test-Case 'isolated preview and WhatIf leave packages, profiles, processes and reports untouched' {
            $plan = Get-IsolatedPlan
            Invoke-OpenClawIsolatedClean -Apply $false -Patch $patch -FullName $fullName -Layout $layout -Profiles @($profile) -Companion $companion
            Invoke-OpenClawIsolatedClean -Apply $true -Patch $patch -FullName $fullName -Layout $layout -Profiles @($profile) -Companion $companion -ExpectedSha256 $plan.Sha256 -WhatIf
            Assert-True ($state.Events.Count -eq 0 -and $state.Packages.Count -eq 3 -and (Test-Path $profile)) 'Preview mutated targets.'
        }
        Test-Case 'isolated mode preserves concurrent unrelated tray and model instances' {
            $state.Processes = @(
                [pscustomobject]@{ Name = 'OpenClaw.Tray.WinUI.exe'; ExecutablePath = "$fixture\other-build\OpenClaw.Tray.WinUI.exe"; ProcessId = 123; CommandLine = 'unrelated instance' },
                [pscustomobject]@{ Name = 'llama-server.exe'; ExecutablePath = "$fixture\shared-cache\llama-server.exe"; ProcessId = 124; CommandLine = 'shared model server' })
            $null = Get-IsolatedPlan
            $state.Processes = @()
        }
        Test-Case 'isolated cleanup ignores unrelated processes with UNC or unusual executable paths' {
            foreach ($path in @('\\server\share\team[1]\helper.exe', "$fixture\strange[1]\helper.exe")) {
                $state.Processes = @([pscustomobject]@{
                    Name = 'unrelated.exe'; ExecutablePath = $path; ProcessId = 125; CommandLine = 'unrelated process'
                })
                $null = Get-IsolatedPlan
            }
            $state.Processes = @()
        }
        Test-Case 'selected running executables and profile users are rejected without stopping them' {
            foreach ($process in @(
                [pscustomobject]@{ Name = 'OpenClaw.Tray.WinUI.exe'; ExecutablePath = $companion; ProcessId = 1; CommandLine = '' },
                [pscustomobject]@{ Name = 'other.exe'; ExecutablePath = "$profile\other.exe"; ProcessId = 2; CommandLine = '' },
                [pscustomobject]@{ Name = 'OpenClaw.Tray.WinUI.exe'; ExecutablePath = "$fixture\other\OpenClaw.Tray.WinUI.exe"; ProcessId = 3; CommandLine = "--data-dir $profile" },
                [pscustomobject]@{ Name = 'OpenClaw.Tray.WinUI.exe'; ExecutablePath = $null; ProcessId = 4; CommandLine = '' })) {
                $state.Processes = @($process)
                Assert-Throws { Get-IsolatedPlan } 'running|references|ownership'
            }
            $state.Processes = @()
            Assert-True ($state.Events.Count -eq 0) 'Process guard mutated runtime.'
        }
        Test-Case 'protected profile junctions cannot alias a disposable target' {
            $protected = Join-Path $env:APPDATA 'OpenClawTray'
            $backup = Join-Path $fixture 'ordinary-profile-backup'
            Move-Item -LiteralPath $protected -Destination $backup
            try {
                $created = & $env:ComSpec /d /c "mklink /J `"$protected`" `"$profile`""
                Assert-True ($LASTEXITCODE -eq 0 -and (Get-Item -LiteralPath $protected -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) "Could not create fixture junction: $created"
                Assert-Throws { Get-IsolatedPlan } 'reparse point'
            } finally {
                if (Test-Path -LiteralPath $protected) {
                    & $env:ComSpec /d /c "rmdir `"$protected`"" | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'Could not remove the temporary protected-root junction.' }
                }
                Move-Item -LiteralPath $backup -Destination $protected
            }
        }
        Test-Case 'isolated scope requires one exact development registration and registered layout' {
            foreach ($items in @(@(), @($normalPackage), @($package,$package))) {
                $state.Packages = $items
                Assert-Throws { Get-IsolatedPlan } 'exactly one'
            }
            $state.Packages = @($package,$normalPackage,$otherPatch)
            $package.IsDevelopmentMode = $false
            Assert-Throws { Get-IsolatedPlan } 'identity or owned layout'
            $package.IsDevelopmentMode = $true
            $package.InstallLocation = "$fixture\other-layout"
            Assert-Throws { Get-IsolatedPlan } 'identity or owned layout'
            $package.InstallLocation = $layout
            $package.PublisherId = 'invalid'
            Assert-Throws { Get-IsolatedPlan } 'identity or owned layout'
            $package.PublisherId = '123456789abcd'
            Assert-Throws { Get-CleanPatchPackage '*' $fullName $layout } 'exact lowercase'
            Assert-Throws { Get-CleanPatchPackage $patch 'wrong_full_name' $layout } 'identity or owned layout'
        }
        Test-Case 'patched lifecycle alias and manifest must agree with exact identity' {
            Set-Content -LiteralPath "$layout\AppxManifest.xml" -Value ($manifest.Replace('clawctl-pr225-test.exe','clawctl.exe'))
            Assert-Throws { Get-IsolatedPlan } 'manifest'
            Set-Content -LiteralPath "$layout\AppxManifest.xml" -Value $manifest
            Remove-Item -LiteralPath $alias
            Assert-Throws { Get-IsolatedPlan } 'Missing package-qualified'
            Set-Content -LiteralPath $alias -Value 'fixture alias'
        }
        Test-Case 'session record and opaque sandbox both prove package ownership' {
            foreach ($change in @(
                @{schemaVersion=99}, @{applicationId='PFN:foreign'}, @{generation=''}, @{sandboxId='iso:eyJhcHBJZCI6IkZPUkVJR04ifQ'})) {
                $bad = @{}; foreach ($key in $record.Keys) { $bad[$key] = $record[$key] }
                foreach ($key in $change.Keys) { $bad[$key] = $change[$key] }
                $bad | ConvertTo-Json | Set-Content -LiteralPath $sessionPath
                Assert-Throws { Get-IsolatedPlan } 'ownership|another application'
            }
            $record | ConvertTo-Json | Set-Content -LiteralPath $sessionPath
            Remove-Item -LiteralPath $sessionPath
            Set-Content -LiteralPath (Join-Path (Split-Path $sessionPath) 'setup.json') -Value '{}'
            Assert-Throws { Get-IsolatedPlan } 'missing while setup'
            Remove-Item -LiteralPath (Join-Path (Split-Path $sessionPath) 'setup.json')
            $null = Get-IsolatedPlan
            Reset-IsolatedFixture
        }
        Test-Case 'disposable profiles cannot select normal installs, caches, checkout, roots or overlapping paths' {
            foreach ($path in @("$env:APPDATA\OpenClawTray", "$env:LOCALAPPDATA\OpenClawTray-Dev",
                "$env:LOCALAPPDATA\Packages\other", $layout, "$fixture\other-patch", "$env:USERPROFILE\.cache\child",
                $env:TEMP, "$fixture\*", '\\server\share')) {
                Assert-Throws { Get-CleanIsolatedPlan $patch $fullName $layout @($path) $companion } 'Refusing|overlaps|absolute local'
            }
            Assert-Throws { Get-CleanIsolatedPlan $patch $fullName $layout @($profile,"$profile\child") $companion } 'must not overlap'
            Assert-Throws { Get-CleanIsolatedPlan $patch $fullName $layout @() $companion } 'explicitly named'
            New-Item -ItemType Directory -Path "$profile\.git" | Out-Null
            Assert-Throws { Get-IsolatedPlan } 'source checkout'
            Remove-Item -LiteralPath "$profile\.git"
            $oldCache = $env:HF_HUB_CACHE
            try {
                $env:HF_HUB_CACHE = $profile
                Assert-Throws { Get-IsolatedPlan } 'shared cache'
            } finally { $env:HF_HUB_CACHE = $oldCache }
            $state.Distros = @([pscustomobject]@{ Name = 'UnrelatedDistro'; BasePath = "$profile\wsl" })
            Assert-Throws { Get-IsolatedPlan } 'Preserved WSL storage overlaps'
            $state.Distros = @()
        }
        Test-Case 'isolated cleanup rejects disposable profiles beneath Git checkout and worktree markers' {
            $checkout = Join-Path $fixture 'checkout-ancestor'
            $selectedProfile = Join-Path $checkout 'disposable-profile'
            New-Item -ItemType Directory -Path $selectedProfile -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $checkout '.git') | Out-Null
            Assert-Throws { Get-CleanIsolatedPlan $patch $fullName $layout @($selectedProfile) $companion } 'source checkout'
            Remove-Item -LiteralPath (Join-Path $checkout '.git') -Force
            Set-Content -LiteralPath (Join-Path $checkout '.git') -Value 'gitdir: ..\worktree-metadata'
            Assert-Throws { Get-CleanIsolatedPlan $patch $fullName $layout @($selectedProfile) $companion } 'source checkout'
            Remove-Item -LiteralPath (Join-Path $checkout '.git') -Force
            $null = Get-IsolatedPlan
        }
        Test-Case 'changed previews and package/session/profile drift fail before teardown' {
            $original = (Get-IsolatedPlan).Sha256
            Assert-Throws { Invoke-IsolatedApply '' } 'Missing or stale'
            Set-Content -LiteralPath "$profile\disposable.txt" -Value 'changed after preview'
            Assert-Throws { Invoke-IsolatedApply $original } 'Missing or stale'
            $original = (Get-IsolatedPlan).Sha256
            $record.generation = 'changed'
            $record | ConvertTo-Json | Set-Content -LiteralPath $sessionPath
            Assert-Throws { Invoke-IsolatedApply $original } 'Missing or stale'
            $package.PackageFullName = $fullName.Replace('1.2.3.4','1.2.3.5')
            Assert-Throws { Invoke-IsolatedApply $original } 'identity or owned layout'
            $package.PackageFullName = $fullName
            Assert-True ($state.Events.Count -eq 0) 'Stale preview reached teardown.'
            Reset-IsolatedFixture
        }
        Test-Case 'teardown exit, JSON success, exact command, state and record absence all gate unregister' {
            foreach ($json in @('{"ok":false}', '{"ok":"true"}', '{bad-json',
                '{"ok":true,"command":"setup","gateway":{"state":"records-removed"},"session":{"state":"removed"}}',
                '{"ok":true,"command":"teardown","gateway":{"state":"running"},"session":{"state":"removed"}}',
                '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"not-configured"}}')) {
                Reset-IsolatedFixture
                $digest = (Get-IsolatedPlan).Sha256
                $state.Json = $json
                Assert-Throws { Invoke-IsolatedApply $digest } 'structured success|not valid JSON'
                Assert-True ($state.Events.Count -eq 1 -and $state.Packages.Count -eq 3 -and (Test-Path $profile)) 'Failed teardown deleted registration or profile.'
            }
            Reset-IsolatedFixture
            $state.Exit = 7
            Assert-Throws { Invoke-IsolatedApply (Get-IsolatedPlan).Sha256 } 'structured success'
            Reset-IsolatedFixture
            $state.LeaveSession = $true
            Assert-Throws { Invoke-IsolatedApply (Get-IsolatedPlan).Sha256 } 'structured success'
            Reset-IsolatedFixture
        }
        Test-Case 'profile changes during teardown preserve registration and remaining data' {
            $digest = (Get-IsolatedPlan).Sha256
            $state.DuringTeardown = { Set-Content -LiteralPath "$profile\disposable.txt" -Value 'concurrent writer' }
            Assert-Throws { Invoke-IsolatedApply $digest } 'changed after teardown'
            Assert-True ($state.Events.Count -eq 1 -and $state.Packages.Count -eq 3 -and (Test-Path $profile)) 'Drift reached unregister.'
            Reset-IsolatedFixture
        }
        Test-Case 'profile deletion failure leaves patch registered and can be retried after a fresh preview' {
            Reset-IsolatedFixture
            $secondProfile = Join-Path $fixture 'disposable-companion-second'
            New-Item -ItemType Directory -Path $secondProfile -Force | Out-Null
            $profiles = @($profile, $secondProfile)
            $digest = (Get-IsolatedPlan $profiles).Sha256
            $state.FailDeletePath = $secondProfile
            Assert-Throws { Invoke-IsolatedApply $digest $profiles } 'TEST profile deletion blocked'
            $state.FailDeletePath = $null
            Assert-True ($state.Events.Count -eq 1 -and $state.Events[0] -eq 'teardown' -and
                $state.Packages.Count -eq 3 -and -not (Test-Path -LiteralPath $profile) -and
                (Test-Path -LiteralPath $secondProfile)) 'Failed profile deletion unregistered the package or changed the wrong profile set.'
            $state.Json = '{"ok":true,"command":"teardown","gateway":{"state":"records-removed"},"session":{"state":"not-configured"}}'
            $retryDigest = (Get-IsolatedPlan $profiles).Sha256
            Invoke-IsolatedApply $retryDigest $profiles
            Assert-True (($state.Events -join ',') -eq 'teardown,teardown,unregister' -and
                $state.Packages.Count -eq 2 -and -not (Test-Path -LiteralPath $profile) -and
                -not (Test-Path -LiteralPath $secondProfile)) 'Retry did not finish only the selected cleanup.'
            Reset-IsolatedFixture
        }
        Test-Case 'successful isolated apply preserves normal install, other patches, profiles and external models' {
            New-Item -ItemType Directory -Path "$profile\LocalAI\models" -Force | Out-Null
            Set-Content -LiteralPath "$profile\LocalAI\models\in-profile.gguf" -Value 'disposable model'
            Invoke-IsolatedApply (Get-IsolatedPlan).Sha256
            Assert-True (($state.Events -join ',') -eq 'teardown,unregister') 'Wrong lifecycle order.'
            Assert-True ($state.Packages.Count -eq 2 -and $state.Packages[0] -eq $normalPackage -and $state.Packages[1] -eq $otherPatch) 'Preserved registrations changed.'
            Assert-True (-not (Test-Path $profile) -and (Test-Path "$fixture\shared-cache\weights.gguf") -and
                (Test-Path "$env:APPDATA\OpenClawTray\preserve.txt") -and (Test-Path "$fixture\other-profile\preserve.txt") -and
                (Test-Path "$layout\AppxManifest.xml")) 'Profile/cache/payload preservation wrong.'
        }
        Test-Case 'already absent patch fails closed rather than selecting normal package' {
            Assert-Throws { Get-IsolatedPlan } 'exactly one'
        }
    }
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
