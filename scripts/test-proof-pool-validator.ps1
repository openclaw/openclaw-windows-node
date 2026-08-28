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
$script:invalidContractCount = 0

$tempRoot = Join-Path $env:TEMP (
    "ocppv-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$inputRoot = Join-Path $tempRoot "i"
$processStateRoot = Join-Path $tempRoot "t"
New-Item -ItemType Directory -Path $inputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $processStateRoot -Force | Out-Null

function Write-BytesAtomically {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $stagingPath = "$Destination.new"
    try {
        [System.IO.File]::WriteAllBytes($stagingPath, $Bytes)
        [System.IO.File]::Move($stagingPath, $Destination)
    } catch {
        Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
        throw "Harness failed to stage $Context atomically at '$Destination': $($_.Exception.Message)"
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = ConvertTo-Json -InputObject $Value -Depth 100
    Write-BytesAtomically `
        -Bytes ([System.Text.Encoding]::UTF8.GetBytes($json)) `
        -Destination $Path `
        -Context "generated JSON"
}

function Copy-FileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Context
    )

    try {
        $bytes = [System.IO.File]::ReadAllBytes($Source)
    } catch {
        throw "Harness failed to read $Context source '$Source': $($_.Exception.Message)"
    }
    Write-BytesAtomically `
        -Bytes $bytes `
        -Destination $Destination `
        -Context $Context
}

$script:validationInvocationCount = 0
$script:activeValidatorChildCount = 0
$script:harnessCompleted = $false

function Invoke-ValidatorMode {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Mode,
        [Parameter(Mandatory = $true)][string]$TestInventoryPath,
        [Parameter(Mandatory = $true)][string]$TestSchemaPath
    )

    $script:validationInvocationCount++
    $invocationId = "{0:D4}" -f $script:validationInvocationCount
    $invocationInputRoot = Join-Path $inputRoot $invocationId
    $processTemp = Join-Path $processStateRoot $invocationId
    New-Item -ItemType Directory -Path $invocationInputRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $processTemp -Force | Out-Null
    $isolatedInventoryPath = Join-Path $invocationInputRoot "inventory.json"
    $isolatedSchemaPath = Join-Path $invocationInputRoot "schema.json"
    $context = "case='$Name'; mode='$($Mode.Name)'; sourceInventory='$TestInventoryPath'; sourceSchema='$TestSchemaPath'; inventory='$isolatedInventoryPath'; schema='$isolatedSchemaPath'; temp='$processTemp'"
    Copy-FileAtomically `
        -Source $TestInventoryPath `
        -Destination $isolatedInventoryPath `
        -Context "$context inventory"
    Copy-FileAtomically `
        -Source $TestSchemaPath `
        -Destination $isolatedSchemaPath `
        -Context "$context schema"

    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $validatorPath,
        "-RepoRoot", $repoRootPath,
        "-InventoryPath", $isolatedInventoryPath,
        "-SchemaPath", $isolatedSchemaPath
    ) + $Mode.ExtraArguments

    $previousErrorActionPreference = $ErrorActionPreference
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $ErrorActionPreference = "Continue"
        $env:TEMP = $processTemp
        $env:TMP = $processTemp
        foreach ($inputPath in @($isolatedInventoryPath, $isolatedSchemaPath)) {
            if (-not [System.IO.File]::Exists($inputPath) -or
                ([System.IO.FileInfo]$inputPath).Length -eq 0) {
                throw "Harness input is missing or empty immediately before child launch. $context"
            }
        }
        $script:activeValidatorChildCount++
        try {
            $output = (& $Mode.Executable @arguments 2>&1 | Out-String)
            $exitCode = $LASTEXITCODE
            $global:LASTEXITCODE = 0
        } finally {
            $script:activeValidatorChildCount--
        }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
        Context = $context
    }
}

function Assert-RejectedByAllModes {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$TestInventoryPath,
        [Parameter(Mandatory = $true)][string]$TestSchemaPath,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $script:invalidContractCount++
    foreach ($mode in $validationModes) {
        $result = Invoke-ValidatorMode `
            -Name $Name `
            -Mode $mode `
            -TestInventoryPath $TestInventoryPath `
            -TestSchemaPath $TestSchemaPath
        if ($result.ExitCode -eq 0) {
            throw "'$Name' unexpectedly passed. $($result.Context)"
        }
        $normalizedOutput = $result.Output -replace "\s+", " "
        $normalizedExpectedMessage = $ExpectedMessage -replace "\s+", " "
        if ($normalizedOutput -notmatch [regex]::Escape($normalizedExpectedMessage)) {
            throw "'$Name' failed for the wrong reason. $($result.Context)`n$($result.Output)"
        }
    }
}

function Assert-AcceptedByAllModes {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$TestInventoryPath
    )

    foreach ($mode in $validationModes) {
        $result = Invoke-ValidatorMode `
            -Name $Name `
            -Mode $mode `
            -TestInventoryPath $TestInventoryPath `
            -TestSchemaPath $schemaPath
        if ($result.ExitCode -ne 0) {
            throw "'$Name' unexpectedly failed. $($result.Context)`n$($result.Output)"
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

    $malformedReferenceTargets = @(
        @{
            Name = "scalar ref target"
            Reference = "#/title"
            Mutate = { param($value) }
        },
        @{
            Name = "array ref target"
            Reference = "#/required"
            Mutate = { param($value) }
        },
        @{
            Name = "null ref target"
            Reference = "#/properties/schemaVersion/const"
            Mutate = { param($value) $value.properties.schemaVersion.const = $null }
        },
        @{
            Name = "singleton object array ref target"
            Reference = "#/definitions/command/properties/kind/enum"
            Mutate = {
                param($value)
                $value.definitions.command.properties.kind.enum = @(
                    [pscustomobject]@{ type = "string" })
            }
        }
    )
    for ($index = 0; $index -lt $malformedReferenceTargets.Count; $index++) {
        $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
        & $malformedReferenceTargets[$index].Mutate $schema
        $schema.properties.declaration.'$ref' =
            $malformedReferenceTargets[$index].Reference
        $malformedReferencePath =
            Join-Path $tempRoot "malformed-reference-target-$index.schema.json"
        Write-JsonFile -Value $schema -Path $malformedReferencePath
        Assert-RejectedByAllModes `
            -Name $malformedReferenceTargets[$index].Name `
            -TestInventoryPath $inventoryPath `
            -TestSchemaPath $malformedReferencePath `
            -ExpectedMessage "must resolve to a schema object"
    }

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
        -ExpectedMessage "must be an object"

    $malformedSchemaShapes = @(
        @{
            Name = "string-valued required"
            ExpectedMessage = "must be an array of strings"
            Mutate = { param($value) $value.required = "pools" }
        },
        @{
            Name = "string-valued enum"
            ExpectedMessage = "must be a non-empty array"
            Mutate = { param($value) $value.definitions.command.properties.kind.enum = "repository" }
        },
        @{
            Name = "string-valued uniqueItems"
            ExpectedMessage = "must be a Boolean"
            Mutate = { param($value) $value.definitions.stringList.uniqueItems = "true" }
        },
        @{
            Name = "string-valued minItems"
            ExpectedMessage = "must be a nonnegative integer"
            Mutate = { param($value) $value.properties.pools.minItems = "1" }
        },
        @{
            Name = "fractional minLength"
            ExpectedMessage = "must be a nonnegative integer"
            Mutate = { param($value) $value.definitions.nonEmptyString.minLength = 1.5 }
        },
        @{
            Name = "integer-valued pattern"
            ExpectedMessage = "must be a string"
            Mutate = { param($value) $value.definitions.identifier.pattern = 42 }
        },
        @{
            Name = "array-valued properties"
            ExpectedMessage = "must be an object"
            Mutate = { param($value) $value.properties = @("pools") }
        },
        @{
            Name = "array-valued definitions"
            ExpectedMessage = "must be an object"
            Mutate = { param($value) $value.definitions = @("identifier") }
        },
        @{
            Name = "string-valued items"
            ExpectedMessage = "must be an object"
            Mutate = { param($value) $value.definitions.stringList.items = "identifier" }
        },
        @{
            Name = "array-valued type"
            ExpectedMessage = "must be a supported type string"
            Mutate = { param($value) $value.definitions.stringList.type = @("array") }
        },
        @{
            Name = "integer-valued ref"
            ExpectedMessage = "must be a string"
            Mutate = { param($value) $value.properties.declaration.'$ref' = 42 }
        },
        @{
            Name = "integer-valued schema dialect"
            ExpectedMessage = "must be a string"
            Mutate = { param($value) $value.'$schema' = 42 }
        },
        @{
            Name = "Boolean-valued child schema"
            ExpectedMessage = "Schema node at $/definitions/identifier must be an object"
            Mutate = { param($value) $value.definitions.identifier = $true }
        },
        @{
            Name = "integer-valued child schema"
            ExpectedMessage = "Schema node at $/definitions/identifier must be an object"
            Mutate = { param($value) $value.definitions.identifier = 42 }
        },
        @{
            Name = "null-valued child schema"
            ExpectedMessage = "Schema node at $/definitions/identifier must be an object"
            Mutate = { param($value) $value.definitions.identifier = $null }
        },
        @{
            Name = "null-valued minItems"
            ExpectedMessage = "must be a nonnegative integer"
            Mutate = { param($value) $value.properties.pools.minItems = $null }
        }
    )
    for ($index = 0; $index -lt $malformedSchemaShapes.Count; $index++) {
        $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
        & $malformedSchemaShapes[$index].Mutate $schema
        $malformedSchemaPath = Join-Path $tempRoot "malformed-schema-shape-$index.json"
        Write-JsonFile -Value $schema -Path $malformedSchemaPath
        Assert-RejectedByAllModes `
            -Name $malformedSchemaShapes[$index].Name `
            -TestInventoryPath $inventoryPath `
            -TestSchemaPath $malformedSchemaPath `
            -ExpectedMessage $malformedSchemaShapes[$index].ExpectedMessage
    }

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

    $repositoryScriptBypasses = @(
        @{
            Name = "semicolon after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip; Get-Date"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "pipeline after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip | Out-Null"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "and-chain after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip && Get-Date"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "or-chain after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip || Get-Date"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "newline after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip`r`nGet-Date"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "background repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 -AllowSkip &"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "dot-sourced repository script"
            Command = ". .\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "call-operator repository script"
            Command = "& .\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "trap before repository script"
            Command = "trap { continue }; .\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "param block before repository script"
            Command = "param(`$value = `$(Get-Date))`r`n.\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "using statement before repository script"
            Command = "using module Microsoft.PowerShell.Management`r`n.\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "requires directive before repository script"
            Command = "#Requires -Version 999`r`n.\scripts\validate-mxc-e2e.ps1 -AllowSkip"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "subexpression repository argument"
            Command = ".\scripts\validate-mxc-e2e.ps1 -Label `$(Get-Date)"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "parenthesized repository argument"
            Command = ".\scripts\validate-mxc-e2e.ps1 -Label (Get-Date)"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "array repository argument"
            Command = ".\scripts\validate-mxc-e2e.ps1 -Label `@(Get-Date)"
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "expandable repository argument"
            Command = '.\scripts\validate-mxc-e2e.ps1 -Label "$(Get-Date)"'
            ExpectedMessage = "must directly invoke its declared script"
        },
        @{
            Name = "test runner chained after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1; dotnet test .\tests\Example.Tests.csproj"
            ExpectedMessage = "must use kind 'proof-test'"
        },
        @{
            Name = "MSBuild runner chained after repository script"
            Command = ".\scripts\validate-mxc-e2e.ps1 && msbuild .\tests\Example.Tests.csproj -t:Restore;VSTest"
            ExpectedMessage = "must use kind 'proof-test'"
        }
    )
    for ($index = 0; $index -lt $repositoryScriptBypasses.Count; $index++) {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $inventory.pools[1].authoritativeCommands[0].command =
            $repositoryScriptBypasses[$index].Command
        $repositoryScriptBypassPath =
            Join-Path $tempRoot "repository-script-bypass-$index.json"
        Write-JsonFile -Value $inventory -Path $repositoryScriptBypassPath
        Assert-RejectedByAllModes `
            -Name $repositoryScriptBypasses[$index].Name `
            -TestInventoryPath $repositoryScriptBypassPath `
            -TestSchemaPath $schemaPath `
            -ExpectedMessage $repositoryScriptBypasses[$index].ExpectedMessage
    }

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[2].authoritativeCommands[0].command =
        "dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -c Release -r win-arm64 -o `$(whoami)"
    $dynamicProjectBuildPath = Join-Path $tempRoot "dynamic-project-build.json"
    Write-JsonFile -Value $inventory -Path $dynamicProjectBuildPath
    Assert-RejectedByAllModes `
        -Name "dynamic project-build output" `
        -TestInventoryPath $dynamicProjectBuildPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "must use kind 'proof-test' instead of invoking dotnet directly"

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[2].authoritativeCommands[0].command =
        "dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj --OUTPUT .\artifacts\arm64"
    $invalidCaseProjectBuildPath = Join-Path $tempRoot "invalid-case-project-build.json"
    Write-JsonFile -Value $inventory -Path $invalidCaseProjectBuildPath
    Assert-RejectedByAllModes `
        -Name "invalid-case project-build output option" `
        -TestInventoryPath $invalidCaseProjectBuildPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "must use kind 'proof-test' instead of invoking dotnet directly"

    $acceptedRepositoryCommands = @(
        @{
            Command = "dotnet build .\src\OpenClaw.Shared\OpenClaw.Shared.csproj -o .\artifacts\vstest"
            Path = "src\OpenClaw.Shared\OpenClaw.Shared.csproj"
        },
        @{
            Command = "dotnet.exe build .\src\OpenClaw.Shared\OpenClaw.Shared.csproj --configuration Release --runtime win-x64 --output .\artifacts\vstest"
            Path = "src\OpenClaw.Shared\OpenClaw.Shared.csproj"
        },
        @{
            Command = ".\scripts\validate-docs.ps1 -OutputDirectory '.\artifacts\vstest'"
            Path = "scripts\validate-docs.ps1"
        },
        @{
            Command = ".\scripts\validate-mxc-e2e.ps1 -Label 'semicolon; pipe | and && or || newline-safe'"
            Path = "scripts\validate-mxc-e2e.ps1"
        }
    )
    for ($index = 0; $index -lt $acceptedRepositoryCommands.Count; $index++) {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $inventory.pools[1].authoritativeCommands[0].command =
            $acceptedRepositoryCommands[$index].Command
        $inventory.pools[1].authoritativeCommands[0].path =
            $acceptedRepositoryCommands[$index].Path
        $acceptedRepositoryCommandPath =
            Join-Path $tempRoot "accepted-repository-command-$index.json"
        Write-JsonFile -Value $inventory -Path $acceptedRepositoryCommandPath
        Assert-AcceptedByAllModes `
            -Name "accepted repository command $index" `
            -TestInventoryPath $acceptedRepositoryCommandPath
    }

    foreach ($commandIndex in @(0, 2)) {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
        $inventory.pools[0].authoritativeCommands[$commandIndex] | Add-Member `
            -NotePropertyName "path" `
            -NotePropertyValue "scripts\validate-docs.ps1"
        $nonRepositoryPath = Join-Path $tempRoot "non-repository-path-$commandIndex.json"
        Write-JsonFile -Value $inventory -Path $nonRepositoryPath
        Assert-RejectedByAllModes `
            -Name "path on $($inventory.pools[0].authoritativeCommands[$commandIndex].kind) command" `
            -TestInventoryPath $nonRepositoryPath `
            -TestSchemaPath $schemaPath `
            -ExpectedMessage "may declare path only for repository or proof-test kinds"
    }

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[0].requiredCapabilities += "WINDOWS-11"
    $caseDistinctUniqueItemsPath = Join-Path $tempRoot "case-distinct-unique-items.json"
    Write-JsonFile -Value $inventory -Path $caseDistinctUniqueItemsPath
    Assert-AcceptedByAllModes `
        -Name "case-distinct unique array items" `
        -TestInventoryPath $caseDistinctUniqueItemsPath

    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    $inventory.pools[0].requiredCapabilities += "windows-11"
    $duplicateUniqueItemPath = Join-Path $tempRoot "duplicate-unique-item.json"
    Write-JsonFile -Value $inventory -Path $duplicateUniqueItemPath
    Assert-RejectedByAllModes `
        -Name "exact duplicate array item" `
        -TestInventoryPath $duplicateUniqueItemPath `
        -TestSchemaPath $schemaPath `
        -ExpectedMessage "duplicate"

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
    $script:harnessCompleted = $true
} finally {
    if ($script:activeValidatorChildCount -ne 0) {
        throw "Harness cleanup refused while $script:activeValidatorChildCount validator child process(es) remain active. Temp root: '$tempRoot'."
    }
    if ($script:harnessCompleted) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop
    } else {
        Write-Warning "Proof-pool validator failure artifacts preserved at '$tempRoot'."
    }
}

Write-Host "Proof-pool validator regressions passed: $script:invalidContractCount invalid contracts rejected by $($validationModes.Count) validation modes." -ForegroundColor Green
$global:LASTEXITCODE = 0
