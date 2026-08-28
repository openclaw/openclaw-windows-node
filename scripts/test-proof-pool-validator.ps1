<#
.SYNOPSIS
    Exercises proof-pool validator regressions across supported PowerShell paths.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$SkipPowerShell7
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
$windowsPowerShellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
$validationModes = @()
$pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if (-not $SkipPowerShell7 -and $null -ne $pwshCommand) {
    $validationModes += @(
        @{
            Name = "PowerShell 7 built-in"
            Executable = $pwshCommand.Source
            ExtraArguments = @()
        },
        @{
            Name = "PowerShell 7 forced fallback"
            Executable = $pwshCommand.Source
            ExtraArguments = @("-ForceFallback")
        }
    )
} else {
    Write-Warning "PowerShell 7 is unavailable or intentionally skipped; running Windows PowerShell 5.1 regressions only."
}
$validationModes += @(
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
            $global:LASTEXITCODE = 0
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($exitCode -eq 0) {
            throw "'$Name' unexpectedly passed in $($mode.Name)."
        }
        $normalizedOutput = $output -replace "\s+", " "
        $normalizedExpectedMessage = $ExpectedMessage -replace "\s+", " "
        if ($normalizedOutput -notmatch [regex]::Escape($normalizedExpectedMessage)) {
            throw "'$Name' failed for the wrong reason in $($mode.Name): $output"
        }
    }
}

function Assert-AcceptedByAllModes {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$TestInventoryPath
    )

    foreach ($mode in $validationModes) {
        $arguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $validatorPath,
            "-RepoRoot", $repoRootPath,
            "-InventoryPath", $TestInventoryPath,
            "-SchemaPath", $schemaPath
        ) + $mode.ExtraArguments

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $output = (& $mode.Executable @arguments 2>&1 | Out-String)
            $exitCode = $LASTEXITCODE
            $global:LASTEXITCODE = 0
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($exitCode -ne 0) {
            throw "'$Name' unexpectedly failed in $($mode.Name): $output"
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

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    $schema.additionalProperties = [pscustomobject]@{ type = "string" }
    $objectAdditionalPropertiesPath =
        Join-Path $tempRoot "object-additional-properties.schema.json"
    Write-JsonFile -Value $schema -Path $objectAdditionalPropertiesPath
    Assert-RejectedByAllModes `
        -Name "object-valued additionalProperties" `
        -TestInventoryPath $inventoryPath `
        -TestSchemaPath $objectAdditionalPropertiesPath `
        -ExpectedMessage "Only additionalProperties=false is supported"

    foreach ($primitiveAdditionalProperties in @("false", 0)) {
        $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
        $schema.additionalProperties = $primitiveAdditionalProperties
        $primitiveName = $primitiveAdditionalProperties.GetType().Name.ToLowerInvariant()
        $primitiveAdditionalPropertiesPath =
            Join-Path $tempRoot "$primitiveName-additional-properties.schema.json"
        Write-JsonFile -Value $schema -Path $primitiveAdditionalPropertiesPath
        Assert-RejectedByAllModes `
            -Name "$primitiveName-valued additionalProperties" `
            -TestInventoryPath $inventoryPath `
            -TestSchemaPath $primitiveAdditionalPropertiesPath `
            -ExpectedMessage "Only additionalProperties=false is supported"
    }

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    $schema.definitions.stringList.items = @(
        [pscustomobject]@{ '$ref' = "#/definitions/nonEmptyString" })
    $tupleItemsPath = Join-Path $tempRoot "tuple-items.schema.json"
    Write-JsonFile -Value $schema -Path $tupleItemsPath
    Assert-RejectedByAllModes `
        -Name "tuple items" `
        -TestInventoryPath $inventoryPath `
        -TestSchemaPath $tupleItemsPath `
        -ExpectedMessage "Tuple or non-object items is unsupported"

    $rawDotnetCommands = @(
        "pwsh -NoProfile -Command '  dotnet test .\tests\Example.Tests.csproj'",
        "pwsh -NoProfile -Command `"& 'dotnet' test .\tests\Example.Tests.csproj`"",
        "cmd.exe /d /c dotnet.exe test .\tests\Example.Tests.csproj",
        "pwsh -NoProfile -Command 'dotnet --% test .\tests\Example.Tests.csproj'",
        "Start-Process dotnet -ArgumentList 'test','.\tests\Example.Tests.csproj'",
        "Start-Process -ArgumentList 'test','.\tests\Example.Tests.csproj' -FilePath dotnet",
        "start dotnet -ArgumentList 'test','.\tests\Example.Tests.csproj'",
        "saps dotnet -ArgumentList 'test','.\tests\Example.Tests.csproj'",
        "dotnet vstest .\artifacts\Example.Tests.dll /Tests:Proof",
        "dotnet msbuild .\tests\Example.Tests.csproj -t:VSTest",
        "dotnet msbuild .\tests\Example.Tests.csproj /t:VSTest",
        "dotnet msbuild .\tests\Example.Tests.csproj -target:VSTest",
        "dotnet build .\tests\Example.Tests.csproj -t:VSTest",
        "msbuild.exe .\tests\Example.Tests.csproj -t:VSTest",
        "msbuild.exe .\tests\Example.Tests.csproj -t:Restore;VSTest",
        "dotnet exec .\artifacts\vstest.console.dll .\artifacts\Example.Tests.dll",
        "vstest.console.exe .\artifacts\Example.Tests.dll /Tests:Proof",
        ".\tools\vstest.console.exe .\artifacts\Example.Tests.dll /Tests:Proof",
        '& "C:\Program Files\MSVS\vstest.console.exe" .\artifacts\Example.Tests.dll',
        "Start-Process -FilePath vstest.console.exe -ArgumentList '.\artifacts\Example.Tests.dll'",
        "Start-Process .\tools\vstest.console.exe -ArgumentList '.\artifacts\Example.Tests.dll'",
        "pwsh -NoProfile -Command `"Start-Process -FilePath vstest.console.exe -ArgumentList '.\artifacts\Example.Tests.dll'`""
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
    $inventory.pools[1].authoritativeCommands[0].command =
        ".\scripts\validate-mxc-e2e.ps1; dotnet test .\tests\Example.Tests.csproj"
    $chainedTestRunnerPath = Join-Path $tempRoot "chained-test-runner.json"
    Write-JsonFile -Value $inventory -Path $chainedTestRunnerPath
    Assert-RejectedByAllModes `
        -Name "test runner chained after repository script" `
        -TestInventoryPath $chainedTestRunnerPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "must use kind 'proof-test'"

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[1].authoritativeCommands[0].command =
        ".\scripts\validate-mxc-e2e.ps1 && msbuild .\tests\Example.Tests.csproj -t:Restore;VSTest"
    $chainedMsBuildPath = Join-Path $tempRoot "chained-msbuild-runner.json"
    Write-JsonFile -Value $inventory -Path $chainedMsBuildPath
    Assert-RejectedByAllModes `
        -Name "MSBuild runner chained after repository script" `
        -TestInventoryPath $chainedMsBuildPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "must use kind 'proof-test'"

    $nonTestVstestCommands = @(
        @{
            Command = "dotnet build .\src\OpenClaw.Shared\OpenClaw.Shared.csproj -o .\artifacts\vstest"
            Path = "src\OpenClaw.Shared\OpenClaw.Shared.csproj"
        },
        @{
            Command = ".\scripts\validate-docs.ps1 -OutputDirectory '.\artifacts\vstest'"
            Path = "scripts\validate-docs.ps1"
        }
    )
    for ($index = 0; $index -lt $nonTestVstestCommands.Count; $index++) {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $inventory.pools[1].authoritativeCommands[0].command =
            $nonTestVstestCommands[$index].Command
        $inventory.pools[1].authoritativeCommands[0].path =
            $nonTestVstestCommands[$index].Path
        $nonTestVstestPath = Join-Path $tempRoot "non-test-vstest-$index.json"
        Write-JsonFile -Value $inventory -Path $nonTestVstestPath
        Assert-AcceptedByAllModes `
            -Name "non-test VSTest token $index" `
            -TestInventoryPath $nonTestVstestPath
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

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[3].authoritativeCommands[1].command =
        ".\scripts\run-proof-tests.ps1 -Project 'tests\DoesNotExist\Nope.csproj' -Filter 'FullyQualifiedName~LocalAiGpu' -ResultName 'local-ai-gpu'"
    $missingProjectPath = Join-Path $tempRoot "missing-proof-project.json"
    Write-JsonFile -Value $inventory -Path $missingProjectPath
    Assert-RejectedByAllModes `
        -Name "missing proof project" `
        -TestInventoryPath $missingProjectPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "is not a repository file"

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[5].authoritativeCommands[0].command =
        "`$env:OPENCLAW_RUN_E2E = '1'; .\scripts\run-proof-tests.ps1 -Project 'tests\DoesNotExist\Nope.csproj' -Filter 'FullyQualifiedName~SetupAndConnectTests' -ResultName 'missing-e2e' -RuntimeIdentifier win-x64"
    $missingPrefixedProjectPath =
        Join-Path $tempRoot "missing-prefixed-proof-project.json"
    Write-JsonFile -Value $inventory -Path $missingPrefixedProjectPath
    Assert-RejectedByAllModes `
        -Name "missing environment-prefixed proof project" `
        -TestInventoryPath $missingPrefixedProjectPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "is not a repository file"
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Proof-pool validator regressions passed: 34 invalid contracts rejected by $($validationModes.Count) validation modes." -ForegroundColor Green
$global:LASTEXITCODE = 0
