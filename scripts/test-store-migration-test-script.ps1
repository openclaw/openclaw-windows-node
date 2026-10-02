<#
.SYNOPSIS
    Regression tests for Enable-StoreMigrationTest.ps1.

.DESCRIPTION
    Covers the reparse-point guard on the -ResetMigrationState path. A junction
    anywhere at or above the store-migration folder would otherwise let the
    reset delete matching record names out of whatever the link targets.

    The completion receipt is the record that makes this destructive rather than
    merely surprising. Test-InnoMigration.ps1 treats an absent receipt with the
    Store package unregistered as permission to unregister the WSL distro, so
    deleting it from a redirected path hands a later gateway uninstall authority
    it should never have had.

    Creating a junction does not require elevation, so this runs anywhere.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$targetScript = Join-Path $PSScriptRoot 'Enable-StoreMigrationTest.ps1'
$pass = 0
$fail = 0

function Assert-True {
    param([string] $Name, [bool] $Condition)

    if ($Condition) {
        $script:pass++
        Write-Host "PASS $Name" -ForegroundColor Green
    }
    else {
        $script:fail++
        Write-Host "FAIL $Name" -ForegroundColor Red
    }
}

function New-Junction {
    param([string] $Link, [string] $Target)

    $output = cmd /c mklink /J "$Link" "$Target" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create junction '$Link' -> '$Target': $output"
    }
}

function Remove-Junction {
    param([string] $Link)

    cmd /c rmdir "$Link" 2>&1 | Out-Null
}

# Redirects the record directory by placing a junction at $LinkAt, then runs a
# reset and reports whether the records behind the link survived.
function Invoke-RedirectedReset {
    param(
        [ValidateSet('store-migration', 'OpenClawTray')]
        [string] $LinkAt
    )

    $originalAppData = $env:APPDATA
    $root = Join-Path ([IO.Path]::GetTempPath()) ('openclaw-migjunc-' + [guid]::NewGuid().ToString('N'))
    $decoyAppData = Join-Path $root 'appdata'
    $realRecords = Join-Path $root 'real'
    $link = $null

    try {
        if ($LinkAt -eq 'store-migration') {
            $linkParent = Join-Path $decoyAppData 'OpenClawTray'
            $link = Join-Path $linkParent 'store-migration'
            $recordHome = $realRecords
        }
        else {
            $linkParent = $decoyAppData
            $link = Join-Path $decoyAppData 'OpenClawTray'
            $recordHome = Join-Path $realRecords 'store-migration'
        }

        $null = New-Item -ItemType Directory -Path $linkParent -Force -Confirm:$false
        $null = New-Item -ItemType Directory -Path $recordHome -Force -Confirm:$false

        $receipt = Join-Path $recordHome 'completed.dpapi'
        $consent = Join-Path $recordHome 'consent.dpapi'
        Set-Content -LiteralPath $receipt -Value 'receipt' -NoNewline
        Set-Content -LiteralPath $consent -Value 'consent' -NoNewline

        New-Junction -Link $link -Target $realRecords

        $env:APPDATA = $decoyAppData
        $message = $null
        try {
            & $targetScript -ResetMigrationState -IncludeCompletionReceipt | Out-Null
        }
        catch {
            $message = $_.Exception.Message
        }

        return [pscustomobject]@{
            Message         = $message
            ReceiptSurvived = Test-Path -LiteralPath $receipt
            ConsentSurvived = Test-Path -LiteralPath $consent
        }
    }
    finally {
        $env:APPDATA = $originalAppData
        if ($link) { Remove-Junction -Link $link }
        Remove-Item -LiteralPath $root -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
}

Write-Host 'Enable-StoreMigrationTest.ps1 regressions' -ForegroundColor Cyan
Write-Host ''

$direct = Invoke-RedirectedReset -LinkAt 'store-migration'
Assert-True 'reset refuses when store-migration is a junction' ($direct.Message -like '*reparse point*')
Assert-True 'completion receipt behind the junction survives' $direct.ReceiptSurvived
Assert-True 'consent record behind the junction survives' $direct.ConsentSurvived

$ancestor = Invoke-RedirectedReset -LinkAt 'OpenClawTray'
Assert-True 'reset refuses when an ancestor is a junction' ($ancestor.Message -like '*reparse point*')
Assert-True 'records behind the ancestor junction survive' ($ancestor.ReceiptSurvived -and $ancestor.ConsentSurvived)

Write-Host ''
if ($fail -gt 0) {
    Write-Host "Store migration script regressions failed: $fail of $($pass + $fail) checks." -ForegroundColor Red
    exit 1
}

Write-Host "Store migration script regressions passed: $pass checks." -ForegroundColor Green
exit 0
