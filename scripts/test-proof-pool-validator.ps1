<#
.SYNOPSIS
    Exercises proof-pool validator regressions across supported PowerShell paths.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$validatorPath = Join-Path $repoRootPath "scripts\validate-proof-pools.ps1"
$inventoryPath = Join-Path $repoRootPath ".github\proof-pools.json"
$schemaPath = Join-Path $repoRootPath ".github\proof-pools.schema.json"
$pwshPath = (Get-Command pwsh.exe -ErrorAction Stop).Source
$windowsPowerShellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
$validationModes = @(
    @{
        Name = "PowerShell 7 built-in"
        Executable = $pwshPath
        ExtraArguments = @()
    },
    @{
        Name = "PowerShell 7 forced fallback"
        Executable = $pwshPath
        ExtraArguments = @("-ForceFallback")
    },
    @{
        Name = "Windows PowerShell 5.1 fallback"
        Executable = $windowsPowerShellPath
        ExtraArguments = @()
    }
)

$tempRoot = Join-Path $env:TEMP (
    "openclaw-proof-pool-validator-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    ConvertTo-Json -InputObject $Value -Depth 100 |
        Set-Content -LiteralPath $Path -Encoding UTF8
}

function Assert-RejectedByAllModes {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$TestInventoryPath,
        [Parameter(Mandatory = $true)][string]$TestSchemaPath,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    foreach ($mode in $validationModes) {
        $arguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $validatorPath,
            "-RepoRoot", $repoRootPath,
            "-InventoryPath", $TestInventoryPath,
            "-SchemaPath", $TestSchemaPath
        ) + $mode.ExtraArguments

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $output = (& $mode.Executable @arguments 2>&1 | Out-String)
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($exitCode -eq 0) {
            throw "'$Name' unexpectedly passed in $($mode.Name)."
        }
        if ($output -notmatch [regex]::Escape($ExpectedMessage)) {
            throw "'$Name' failed for the wrong reason in $($mode.Name): $output"
        }
    }
}

try {
    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    $schema.definitions.identifier.PSObject.Properties.Remove("type")
    $missingTypeSchemaPath = Join-Path $tempRoot "missing-assertion-type.schema.json"
    Write-JsonFile -Value $schema -Path $missingTypeSchemaPath
    Assert-RejectedByAllModes `
        -Name "pattern without explicit type" `
        -TestInventoryPath $inventoryPath `
        -TestSchemaPath $missingTypeSchemaPath `
        -ExpectedMessage "requires explicit type 'string'"

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    $schema.definitions.identifier | Add-Member `
        -NotePropertyName '$ref' `
        -NotePropertyValue "#/definitions/nonEmptyString"
    $refSiblingSchemaPath = Join-Path $tempRoot "ref-assertion-sibling.schema.json"
    Write-JsonFile -Value $schema -Path $refSiblingSchemaPath
    Assert-RejectedByAllModes `
        -Name "pattern beside ref" `
        -TestInventoryPath $inventoryPath `
        -TestSchemaPath $refSiblingSchemaPath `
        -ExpectedMessage "cannot have sibling keywords"

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    $schema.properties.schemaVersion | Add-Member `
        -NotePropertyName '$ref' `
        -NotePropertyValue "#/definitions/identifier"
    $constRefSiblingSchemaPath = Join-Path $tempRoot "const-ref-sibling.schema.json"
    Write-JsonFile -Value $schema -Path $constRefSiblingSchemaPath
    Assert-RejectedByAllModes `
        -Name "const beside ref" `
        -TestInventoryPath $inventoryPath `
        -TestSchemaPath $constRefSiblingSchemaPath `
        -ExpectedMessage "cannot have sibling keywords"

    $rawDotnetCommands = @(
        "pwsh -NoProfile -Command '  dotnet test .\tests\Example.Tests.csproj'",
        "pwsh -NoProfile -Command `"& 'dotnet' test .\tests\Example.Tests.csproj`"",
        "cmd.exe /d /c dotnet.exe test .\tests\Example.Tests.csproj",
        "pwsh -NoProfile -Command 'dotnet --% test .\tests\Example.Tests.csproj'",
        "Start-Process dotnet -ArgumentList 'test','.\tests\Example.Tests.csproj'"
    )
    for ($index = 0; $index -lt $rawDotnetCommands.Count; $index++) {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $inventory.pools[0].authoritativeCommands[0].command =
            $rawDotnetCommands[$index]
        $wrappedDotnetPath = Join-Path $tempRoot "wrapped-dotnet-test-$index.json"
        Write-JsonFile -Value $inventory -Path $wrappedDotnetPath
        Assert-RejectedByAllModes `
            -Name "wrapped raw dotnet test $index" `
            -TestInventoryPath $wrappedDotnetPath `
            -TestSchemaPath $schemaPath `
            -ExpectedMessage "must use kind 'proof-test'"
    }

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[0].authoritativeCommands[0].kind = "repository"
    $inventory.pools[0].authoritativeCommands[0] | Add-Member `
        -NotePropertyName "path" `
        -NotePropertyValue "scripts\run-proof-tests.ps1"
    $inventory.pools[0].authoritativeCommands[0].command =
        ".\scripts\run-proof-tests.ps1 -Project 'tests\Example.Tests.csproj' -Filter 'Category=Proof' -ResultName 'example' -ResultsDirectory C:\temp"
    $misclassifiedRunnerPath = Join-Path $tempRoot "misclassified-proof-runner.json"
    Write-JsonFile -Value $inventory -Path $misclassifiedRunnerPath
    Assert-RejectedByAllModes `
        -Name "misclassified proof runner" `
        -TestInventoryPath $misclassifiedRunnerPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "must use kind 'proof-test'"
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Proof-pool validator regressions passed: 9 invalid contracts rejected by 3 validation modes." -ForegroundColor Green
