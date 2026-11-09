# Pure preparation-witness controls. Native result assignments below simulate the
# external-command boundary; they do not claim native command/UI execution.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VoicePreparationWitness.ps1')
$previousExit = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadPreviousExit = $null -ne $previousExit
$previousExitValue = if ($hadPreviousExit) { $previousExit.Value } else { $null }
$root = Join-Path $PSScriptRoot ('.witness-control-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$script:controls = 0
function Require([bool]$Value) { if (-not $Value) { throw 'Preparation witness pure control failed.' } }
function Control([scriptblock]$Body) { & $Body; $script:controls++ }
try {
    Start-VoicePreparation $root
    Control {
        Enter-VoicePreparation 'default-restore' 'main'
        $global:LASTEXITCODE = 999
        $result = @(Invoke-VoicePreparationNative { $global:LASTEXITCODE = 0 })
        Require ($result.Count -eq 0 -and $script:VoicePreparation.nativeExitCode -ceq 0 -and $script:VoicePreparation.state -ceq 'returned')
    }
    Control {
        Enter-VoicePreparation 'default-build' 'main'
        $failed = $false
        try { Invoke-VoicePreparationNative { $global:LASTEXITCODE = 17 } } catch { $failed = $true }
        $safe = Get-VoicePreparationFailure
        Require ($failed -and $safe.nativeExitCode -ceq 17 -and $safe.nativeAttempted -and $safe.nativeReturned)
    }
    Control {
        Enter-VoicePreparation 'default-build' 'main'
        $global:LASTEXITCODE = 0
        $original = [IO.IOException]::new('SECRET-launch-path')
        $caught = $null
        try { Invoke-VoicePreparationNative { throw $original } } catch { $caught = $_.Exception }
        $safe = Get-VoicePreparationFailure
        Require ([object]::ReferenceEquals($caught, $original) -and $null -eq $safe.nativeExitCode -and -not $safe.nativeReturned -and $safe.category -ceq 'native-no-result')
    }
    Control {
        Enter-VoicePreparation 'sdk-query'; Invoke-VoicePreparationNative { $global:LASTEXITCODE = 0 }
        Enter-VoicePreparation 'target-contract' 'approval' 'target-query-contract'
        $safe = Get-VoicePreparationFailure
        Require ($null -eq $safe.nativeExitCode -and -not $safe.nativeAttempted -and $safe.category -ceq 'target-query-contract')
    }
    Control {
        Enter-VoicePreparation 'required-files' 'approval' 'file-io'
        $safe = Get-VoicePreparationFailure
        Require ($safe.phase -ceq 'required-files' -and $safe.arm -ceq 'approval' -and $safe.elapsedMs -ge 0 -and $safe.elapsedMs -le [int]::MaxValue)
        Require (-not (($safe | ConvertTo-Json -Depth 5).Contains($root)))
    }
    Control {
        $original = [InvalidOperationException]::new('SECRET-primary')
        $caught = $null; $receipt = [ordered]@{ rejectedAt = 'Prepare' }
        try {
            try { throw $original }
            catch { Save-VoicePreparationFailure $receipt { throw [IO.IOException]::new('SECRET-secondary') }; throw }
        } catch { $caught = $_.Exception }
        Require ([object]::ReferenceEquals($caught, $original) -and $receipt.preparation.phase -ceq 'required-files')
        Require (-not (($receipt | ConvertTo-Json -Depth 5).Contains('SECRET')))
    }
    Control {
        $text = @('C:\SECRET\file.cs(1,2): error CS1001: SECRET', 'MSBUILD : error MSB1009: SECRET',
            'fixture.csproj : error NU1301: SECRET', 'error NETSDK1045: SECRET', 'page.xaml(3,4): error WMC1006: SECRET') -join [Environment]::NewLine
        $safe = ConvertTo-VoiceCompilerCodes $text
        Require ($safe.status -ceq 'found' -and ($safe.codes -join ',') -ceq 'CS1001,MSB1009,NETSDK1045,NU1301,WMC1006')
        Require (-not (($safe | ConvertTo-Json).Contains('SECRET')))
    }
    Control {
        $safe = ConvertTo-VoiceCompilerCodes 'STDERR embedded C:\private\a.cs(1,2): error CS1001: SECRET'
        Require ($safe.status -ceq 'none' -and $safe.codes.Count -eq 0)
    }
    Control {
        $safe = ConvertTo-VoiceCompilerCodes (@('error FAKE1001: SECRET','a.cs(1): warning CS1001: SECRET','error CS12: SECRET','error CS12345: SECRET') -join [Environment]::NewLine)
        Require ($safe.status -ceq 'none')
    }
    Control { Require ((ConvertTo-VoiceCompilerCodes ('x' * (2MB + 1))).status -ceq 'oversized') }
    Control { Require ((ConvertTo-VoiceCompilerCodes ('x' * 8193)).status -ceq 'oversized-line') }
    Control {
        $text = (1..10 | ForEach-Object { 'error CS{0:D4}: SECRET' -f $_ }) -join [Environment]::NewLine
        $safe = ConvertTo-VoiceCompilerCodes $text
        Require ($safe.codes.Count -eq 8 -and $safe.truncated -and ($safe.codes -join ',') -ceq 'CS0001,CS0002,CS0003,CS0004,CS0005,CS0006,CS0007,CS0008')
    }
    Control {
        $safe = ConvertTo-VoiceCompilerCodes ('error CS1001: SECRET' + [Environment]::NewLine + 'error CS1001: SECRET')
        Require ($safe.codes.Count -eq 1 -and -not $safe.truncated)
    }
    Control { Enter-VoicePreparation 'default-restore' 'approval'; Require ((Read-VoiceCompilerCodes).status -ceq 'unavailable') }
    Control {
        [IO.File]::WriteAllBytes((Join-Path $root 'main-build.log'), [byte[]]@(0xff,0xfe,0xff))
        Enter-VoicePreparation 'default-build' 'main'
        Require ((Read-VoiceCompilerCodes).status -ceq 'invalid')
    }
    Control {
        [IO.File]::WriteAllText((Join-Path $root 'main-build.log'), 'error CS0001: SECRET-main')
        [IO.File]::WriteAllText((Join-Path $root 'approval-build.log'), 'error CS0002: SECRET-other-arm')
        Enter-VoicePreparation 'default-build' 'main'
        try { Invoke-VoicePreparationNative { $global:LASTEXITCODE = 1 } } catch { }
        [IO.File]::Delete((Join-Path $root 'main-build.log'))
        $safe = Get-VoicePreparationFailure
        Require ($safe.nativeExitCode -ceq 1 -and ($safe.compiler.codes -join ',') -ceq 'CS0001')
    }
    Control { Enter-VoicePreparation 'target-query' 'approval'; Require ((Read-VoiceCompilerCodes).status -ceq 'not-applicable') }
    Control {
        [IO.File]::WriteAllText((Join-Path $root 'main-restore.log'), '')
        Enter-VoicePreparation 'default-restore' 'main'
        Require ((Read-VoiceCompilerCodes).status -ceq 'none')
    }
    Control {
        Enter-VoicePreparation 'default-build' 'main'
        try { Invoke-VoicePreparationNative { throw [Exception]::new('SECRET-error-content') } } catch { }
        $json = Get-VoicePreparationFailure | ConvertTo-Json -Depth 5
        Require (-not $json.Contains('SECRET') -and -not $json.Contains('Exception') -and -not $json.Contains($root))
    }
    Control {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-VoicePair.ps1'), [ref]$tokens, [ref]$errors)
        Require ($errors.Count -eq 0)
        $build = @($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Build-Arm'}, $true))
        Require ($build.Count -eq 1 -and -not $build[0].Extent.Text.Contains('$LASTEXITCODE') -and $build[0].Extent.Text.Contains('Invoke-VoicePreparationNative'))
        Require ($ast.Extent.Text.Contains("Save-VoicePreparationFailure $" + "receipt"))
    }
    Control {
        $rejected = $false
        try { Enter-VoicePreparation -Phase 'SECRET-invalid-phase' } catch { $rejected = $true }
        Require $rejected
    }
    Control {
        Enter-VoicePreparation 'sdk-query'
        $original = [Exception]::new('SECRET-command-wrapper')
        $caught = $null
        try { Invoke-VoicePreparationNative { $global:LASTEXITCODE = 0; throw $original } } catch { $caught = $_.Exception }
        $safe = Get-VoicePreparationFailure
        Require ([object]::ReferenceEquals($caught, $original) -and $safe.nativeExitCode -ceq 0 -and -not $safe.nativeReturned -and $safe.state -ceq 'failed')
    }
    Require ($script:controls -eq 22)
    Write-Host 'Preparation witness controls: 22 passed (pure boundary/data controls; no native app execution).'
} finally {
    if (-not $hadPreviousExit) { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    else { $global:LASTEXITCODE = $previousExitValue }
    Remove-Item -LiteralPath $root -Recurse -Force
}
