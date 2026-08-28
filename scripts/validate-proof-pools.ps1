<#
.SYNOPSIS
    Validates the named custom Windows proof-pool inventory.

.DESCRIPTION
    Uses PowerShell's built-in JSON Schema support, then applies repository
    invariants that JSON Schema cannot express: unique command IDs, existing
    repository entry points, nonzero test guards, and cross-file documentation
    parity.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$InventoryPath,
    [string]$SchemaPath,
    [switch]$ForceFallback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path $repoRootPath ".github\proof-pools.json"
}
if ([string]::IsNullOrWhiteSpace($SchemaPath)) {
    $SchemaPath = Join-Path $repoRootPath ".github\proof-pools.schema.json"
}

foreach ($path in @($InventoryPath, $SchemaPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Proof-pool validation input does not exist: $path"
    }
}

$testJson = Get-Command Test-Json -ErrorAction SilentlyContinue
$supportedSchemaKeywords = @(
    '$ref',
    '$schema',
    '$id',
    'title',
    'description',
    'type',
    'const',
    'enum',
    'additionalProperties',
    'required',
    'properties',
    'items',
    'minItems',
    'uniqueItems',
    'minLength',
    'pattern',
    'definitions'
)

function Get-JsonProperty {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }
    return @($Value.PSObject.Properties | Where-Object {
        [string]::Equals(
            $_.Name,
            $Name,
            [System.StringComparison]::Ordinal)
    }) | Select-Object -First 1
}

function Compare-JsonValue {
    param(
        [AllowNull()][object]$Left,
        [AllowNull()][object]$Right
    )

    $leftJson = ConvertTo-Json -InputObject $Left -Depth 100 -Compress
    $rightJson = ConvertTo-Json -InputObject $Right -Depth 100 -Compress
    return $leftJson -ceq $rightJson
}

function Resolve-RepositoryFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $candidate = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRootPath $RelativePath))
    $repoPrefix = $repoRootPath.TrimEnd("\") + "\"
    if (-not $candidate.StartsWith(
            $repoPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Context is not a repository file: $RelativePath"
    }

    $currentPath = $repoRootPath
    $relativeCandidate = $candidate.Substring($repoPrefix.Length)
    foreach ($segment in $relativeCandidate.Split("\")) {
        $currentPath = Join-Path $currentPath $segment
        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Context traverses a reparse point: $RelativePath"
        }
    }

    return $candidate
}

function Test-SafeRepositoryCommandElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.CommandElementAst]$Element
    )

    if ($Element -is [System.Management.Automation.Language.CommandParameterAst]) {
        if ($null -eq $Element.Argument) {
            return $true
        }
        return Test-SafeRepositoryCommandElement -Element $Element.Argument
    }
    if ($Element -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
        $Element -is [System.Management.Automation.Language.ConstantExpressionAst]) {
        return $true
    }
    if ($Element -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
        return @($Element.NestedExpressions).Count -eq 0
    }
    if ($Element -is [System.Management.Automation.Language.VariableExpressionAst]) {
        return $Element.VariablePath.UserPath.StartsWith(
            "env:",
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    return $false
}

function Test-DirectRepositoryScriptCommand {
    param(
        [Parameter(Mandatory = $true)][string]$CommandText,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $tokens = $null
    $parseErrors = $null
    $scriptAst = [System.Management.Automation.Language.Parser]::ParseInput(
        $CommandText,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        return $false
    }

    $requirementsProperty = $scriptAst.PSObject.Properties["ScriptRequirements"]
    if ($null -ne $requirementsProperty -and
        $null -ne $requirementsProperty.Value) {
        return $false
    }
    if ($null -ne $scriptAst.ParamBlock -or
        $null -ne $scriptAst.DynamicParamBlock -or
        $null -ne $scriptAst.BeginBlock -or
        $null -ne $scriptAst.ProcessBlock -or
        @($scriptAst.UsingStatements).Count -ne 0 -or
        $null -ne $scriptAst.EndBlock.Traps) {
        return $false
    }
    $cleanBlockProperty = $scriptAst.PSObject.Properties["CleanBlock"]
    if ($null -ne $cleanBlockProperty -and
        $null -ne $cleanBlockProperty.Value) {
        return $false
    }

    $statements = @($scriptAst.EndBlock.Statements)
    if ($statements.Count -ne 1 -or
        $statements[0] -isnot [System.Management.Automation.Language.PipelineAst]) {
        return $false
    }

    $pipeline = $statements[0]
    $backgroundProperty = $pipeline.PSObject.Properties["Background"]
    if ($null -ne $backgroundProperty -and
        [bool]$backgroundProperty.Value) {
        return $false
    }

    $pipelineElements = @($pipeline.PipelineElements)
    if ($pipelineElements.Count -ne 1 -or
        $pipelineElements[0] -isnot [System.Management.Automation.Language.CommandAst]) {
        return $false
    }

    $directCommand = $pipelineElements[0]
    if ($directCommand.InvocationOperator -ne
        [System.Management.Automation.Language.TokenKind]::Unknown) {
        return $false
    }
    if (@($directCommand.Redirections).Count -ne 0) {
        return $false
    }

    $commandElements = @($directCommand.CommandElements)
    if ($commandElements.Count -eq 0) {
        return $false
    }

    if (-not [string]::Equals(
            $commandElements[0].Extent.Text,
            $ExpectedPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    foreach ($commandElement in $commandElements | Select-Object -Skip 1) {
        if (-not (Test-SafeRepositoryCommandElement -Element $commandElement)) {
            return $false
        }
    }
    return $true
}

function Resolve-LocalSchemaReference {
    param(
        [Parameter(Mandatory = $true)][string]$Reference,
        [Parameter(Mandatory = $true)][object]$RootSchema
    )

    if (-not $Reference.StartsWith("#/")) {
        throw "Only local proof-pool schema references are supported: $Reference"
    }

    $resolved = $RootSchema
    foreach ($encodedSegment in $Reference.Substring(2).Split("/")) {
        $segment = $encodedSegment.Replace("~1", "/").Replace("~0", "~")
        $property = Get-JsonProperty -Value $resolved -Name $segment
        if ($null -eq $property) {
            throw "Proof-pool schema reference does not resolve: $Reference"
        }
        $resolved = $property.Value
    }
    return $resolved
}

function Assert-SupportedSchemaKeywords {
    param(
        [Parameter(Mandatory = $true)][object]$ValueSchema,
        [Parameter(Mandatory = $true)][string]$SchemaPath
    )

    foreach ($schemaProperty in @($ValueSchema.PSObject.Properties)) {
        if ($schemaProperty.Name -cnotin $supportedSchemaKeywords) {
            throw "Unsupported proof-pool schema keyword '$($schemaProperty.Name)' at $SchemaPath."
        }
    }

    $reference = Get-JsonProperty -Value $ValueSchema -Name '$ref'
    if ($null -ne $reference -and
        @($ValueSchema.PSObject.Properties).Count -ne 1) {
        throw "Schema `$ref at $SchemaPath cannot have sibling keywords under Draft-07."
    }
    $additionalProperties =
        Get-JsonProperty -Value $ValueSchema -Name "additionalProperties"
    if ($null -ne $additionalProperties -and
        ($additionalProperties.Value -isnot [bool] -or
            [bool]$additionalProperties.Value)) {
        throw "Only additionalProperties=false is supported at $SchemaPath."
    }
    $typeProperty = Get-JsonProperty -Value $ValueSchema -Name "type"
    $typeSpecificKeywords = @(
        @{ Type = "object"; Keywords = @("additionalProperties", "required", "properties") },
        @{ Type = "array"; Keywords = @("items", "minItems", "uniqueItems") },
        @{ Type = "string"; Keywords = @("minLength", "pattern") }
    )
    foreach ($requirement in $typeSpecificKeywords) {
        foreach ($keyword in $requirement.Keywords) {
            if ($null -eq (Get-JsonProperty -Value $ValueSchema -Name $keyword)) {
                continue
            }
            if ($null -eq $typeProperty -or
                [string]$typeProperty.Value -cne $requirement.Type) {
                throw "Schema keyword '$keyword' at $SchemaPath requires explicit type '$($requirement.Type)'."
            }
        }
    }

    foreach ($containerName in @("properties", "definitions")) {
        $container = Get-JsonProperty -Value $ValueSchema -Name $containerName
        if ($null -ne $container) {
            foreach ($child in @($container.Value.PSObject.Properties)) {
                Assert-SupportedSchemaKeywords `
                    -ValueSchema $child.Value `
                    -SchemaPath "$SchemaPath/$containerName/$($child.Name)"
            }
        }
    }

    $items = Get-JsonProperty -Value $ValueSchema -Name "items"
    if ($null -ne $items) {
        if ($items.Value -isnot [System.Management.Automation.PSCustomObject]) {
            throw "Tuple or non-object items is unsupported at $SchemaPath."
        }
        Assert-SupportedSchemaKeywords `
            -ValueSchema $items.Value `
            -SchemaPath "$SchemaPath/items"
    }
}

function Assert-JsonSchemaValue {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][object]$ValueSchema,
        [Parameter(Mandatory = $true)][object]$RootSchema,
        [Parameter(Mandatory = $true)][string]$JsonPath
    )

    $reference = Get-JsonProperty -Value $ValueSchema -Name '$ref'
    if ($null -ne $reference) {
        $resolved = Resolve-LocalSchemaReference `
            -Reference ([string]$reference.Value) `
            -RootSchema $RootSchema
        Assert-JsonSchemaValue `
            -Value $Value `
            -ValueSchema $resolved `
            -RootSchema $RootSchema `
            -JsonPath $JsonPath
        return
    }

    $constant = Get-JsonProperty -Value $ValueSchema -Name "const"
    if ($null -ne $constant -and -not (Compare-JsonValue $Value $constant.Value)) {
        throw "Proof-pool schema mismatch at ${JsonPath}: expected constant '$($constant.Value)'."
    }

    $enum = Get-JsonProperty -Value $ValueSchema -Name "enum"
    if ($null -ne $enum) {
        $enumMatch = $false
        foreach ($candidate in @($enum.Value)) {
            if (Compare-JsonValue $Value $candidate) {
                $enumMatch = $true
                break
            }
        }
        if (-not $enumMatch) {
            throw "Proof-pool schema mismatch at ${JsonPath}: value is not in the allowed set."
        }
    }

    $typeProperty = Get-JsonProperty -Value $ValueSchema -Name "type"
    if ($null -eq $typeProperty) {
        return
    }

    $schemaType = [string]$typeProperty.Value
    switch ($schemaType) {
        "object" {
            if ($null -eq $Value -or $Value -isnot [System.Management.Automation.PSCustomObject]) {
                throw "Proof-pool schema mismatch at ${JsonPath}: expected object."
            }

            $required = Get-JsonProperty -Value $ValueSchema -Name "required"
            if ($null -ne $required) {
                foreach ($requiredName in @($required.Value)) {
                    if ($null -eq (Get-JsonProperty -Value $Value -Name ([string]$requiredName))) {
                        throw "Proof-pool schema mismatch at ${JsonPath}: missing '$requiredName'."
                    }
                }
            }

            $properties = Get-JsonProperty -Value $ValueSchema -Name "properties"
            $additional = Get-JsonProperty -Value $ValueSchema -Name "additionalProperties"
            foreach ($property in @($Value.PSObject.Properties)) {
                $propertySchema = if ($null -eq $properties) {
                    $null
                } else {
                    Get-JsonProperty -Value $properties.Value -Name $property.Name
                }
                if ($null -eq $propertySchema) {
                    if ($null -ne $additional -and $additional.Value -eq $false) {
                        throw "Proof-pool schema mismatch at ${JsonPath}: unexpected '$($property.Name)'."
                    }
                    continue
                }
                Assert-JsonSchemaValue `
                    -Value $property.Value `
                    -ValueSchema $propertySchema.Value `
                    -RootSchema $RootSchema `
                    -JsonPath "$JsonPath/$($property.Name)"
            }
        }
        "array" {
            if ($Value -isnot [System.Array]) {
                throw "Proof-pool schema mismatch at ${JsonPath}: expected array."
            }

            $values = @($Value)
            $minimumItems = Get-JsonProperty -Value $ValueSchema -Name "minItems"
            if ($null -ne $minimumItems -and $values.Count -lt [int]$minimumItems.Value) {
                throw "Proof-pool schema mismatch at ${JsonPath}: too few items."
            }

            $uniqueItems = Get-JsonProperty -Value $ValueSchema -Name "uniqueItems"
            if ($null -ne $uniqueItems -and $uniqueItems.Value -eq $true) {
                $seen = @{}
                foreach ($item in $values) {
                    $key = ConvertTo-Json -InputObject $item -Depth 100 -Compress
                    if ($seen.ContainsKey($key)) {
                        throw "Proof-pool schema mismatch at ${JsonPath}: duplicate array item."
                    }
                    $seen[$key] = $true
                }
            }

            $items = Get-JsonProperty -Value $ValueSchema -Name "items"
            if ($null -ne $items) {
                for ($index = 0; $index -lt $values.Count; $index++) {
                    Assert-JsonSchemaValue `
                        -Value $values[$index] `
                        -ValueSchema $items.Value `
                        -RootSchema $RootSchema `
                        -JsonPath "$JsonPath/$index"
                }
            }
        }
        "string" {
            if ($Value -isnot [string]) {
                throw "Proof-pool schema mismatch at ${JsonPath}: expected string."
            }

            $minimumLength = Get-JsonProperty -Value $ValueSchema -Name "minLength"
            if ($null -ne $minimumLength -and $Value.Length -lt [int]$minimumLength.Value) {
                throw "Proof-pool schema mismatch at ${JsonPath}: string is too short."
            }

            $pattern = Get-JsonProperty -Value $ValueSchema -Name "pattern"
            if ($null -ne $pattern -and $Value -cnotmatch [string]$pattern.Value) {
                throw "Proof-pool schema mismatch at ${JsonPath}: string does not match its pattern."
            }
        }
        "integer" {
            $integerTypes = @(
                [System.Byte],
                [System.SByte],
                [System.Int16],
                [System.UInt16],
                [System.Int32],
                [System.UInt32],
                [System.Int64],
                [System.UInt64]
            )
            if ($null -eq $Value -or $Value.GetType() -notin $integerTypes) {
                throw "Proof-pool schema mismatch at ${JsonPath}: expected integer."
            }
        }
        "boolean" {
            if ($Value -isnot [bool]) {
                throw "Proof-pool schema mismatch at ${JsonPath}: expected boolean."
            }
        }
        default {
            throw "Unsupported proof-pool schema type '$schemaType' at $JsonPath."
        }
    }
}

$schemaText = [System.IO.File]::ReadAllText($SchemaPath)
$inventoryText = [System.IO.File]::ReadAllText($InventoryPath)
if ($schemaText.Contains([string][char]0x2014) -or $inventoryText.Contains([string][char]0x2014)) {
    throw "Proof-pool schema and inventory must not contain em dashes."
}

try {
    $schema = $schemaText | ConvertFrom-Json
    $inventory = $inventoryText | ConvertFrom-Json
} catch {
    throw "Proof-pool JSON could not be parsed: $($_.Exception.Message)"
}

if ($schema.'$schema' -ne "http://json-schema.org/draft-07/schema#" -or
    $schema.type -ne "object" -or
    $null -eq $schema.definitions) {
    throw "Proof-pool schema must be a draft-07 object schema with definitions."
}
Assert-SupportedSchemaKeywords -ValueSchema $schema -SchemaPath '$'

if (-not $ForceFallback -and $testJson -and $testJson.Parameters.ContainsKey("SchemaFile")) {
    $schemaErrors = @()
    $schemaValid = $inventoryText | Test-Json `
        -SchemaFile $SchemaPath `
        -ErrorAction SilentlyContinue `
        -ErrorVariable schemaErrors
    if (-not $schemaValid) {
        $details = ($schemaErrors | ForEach-Object { $_.Exception.Message }) -join [Environment]::NewLine
        throw "Proof-pool inventory does not match its schema.$([Environment]::NewLine)$details"
    }
} else {
    Assert-JsonSchemaValue `
        -Value $inventory `
        -ValueSchema $schema `
        -RootSchema $schema `
        -JsonPath '$'
}

$poolIds = @($inventory.pools | ForEach-Object { $_.id })
$duplicatePoolIds = @($poolIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicatePoolIds.Count -gt 0) {
    throw "Proof-pool IDs must be unique: $($duplicatePoolIds -join ', ')"
}

foreach ($pool in $inventory.pools) {
    $commandIds = @($pool.authoritativeCommands | ForEach-Object { $_.id })
    $duplicateCommandIds = @(
        $commandIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name
    )
    if ($duplicateCommandIds.Count -gt 0) {
        throw "Proof pool '$($pool.id)' has duplicate command IDs: $($duplicateCommandIds -join ', ')"
    }

    foreach ($command in $pool.authoritativeCommands) {
        $normalizedCommand = ([string]$command.command).
            Replace([string][char]39, "").
            Replace([string][char]34, "")
        $proofTestPattern = "^(?:\`$env:OPENCLAW_RUN_E2E = '1'; )?\.\\scripts\\run-proof-tests\.ps1 -Project '(?<project>[^']+)' -Filter '[^']+' -ResultName '[a-z0-9]+(?:-[a-z0-9]+)*'(?: -RuntimeIdentifier (?:win-x64|win-arm64))?$"
        $commandPathProperty = Get-JsonProperty -Value $command -Name "path"
        $commandPath = if ($null -eq $commandPathProperty) {
            ""
        } else {
            [string]$commandPathProperty.Value
        }
        $invokesProofRunner =
            $commandPath -ieq "scripts\run-proof-tests.ps1" -or
            $normalizedCommand -match "(?i)\brun-proof-tests\.ps1\b"
        $mentionsRawTestTool =
            $normalizedCommand -match "(?i)\bdotnet(?:\.exe)?\b" -or
            $normalizedCommand -match "(?i)\bmsbuild(?:\.exe)?\b" -or
            $normalizedCommand -match "(?i)\bvstest\.console(?:\.(?:exe|dll))?\b" -or
            $normalizedCommand -match "(?i)\bvstest\.(?:exe|dll)\b" -or
            $normalizedCommand -match "(?i)[/-](?:t|target):[^\r\n;&|]*\bVSTest\b"
        if ($command.kind -eq "proof-test" -or $invokesProofRunner) {
            if ($command.kind -ne "proof-test") {
                throw "Proof runner command '$($pool.id)/$($command.id)' must use kind 'proof-test'."
            }
            $proofTestMatch = [regex]::Match(
                [string]$command.command,
                $proofTestPattern,
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
            if ($commandPath -cne "scripts\run-proof-tests.ps1" -or
                -not $proofTestMatch.Success) {
                throw "Proof test command '$($pool.id)/$($command.id)' must use the restricted scripts\run-proof-tests.ps1 contract."
            }
            [void](Resolve-RepositoryFile `
                -RelativePath $proofTestMatch.Groups["project"].Value `
                -Context "Proof test project for '$($pool.id)/$($command.id)'")
        } elseif ($command.kind -eq "repository" -and
            -not [string]::IsNullOrWhiteSpace($commandPath)) {
            $expectedRepositoryPath = ".\" + $commandPath
            $isDirectScriptCommand =
                $commandPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase) -and
                (Test-DirectRepositoryScriptCommand `
                    -CommandText ([string]$command.command) `
                    -ExpectedPath $expectedRepositoryPath)
            $safeProjectBuildPattern =
                "^dotnet(?:\.exe)?\s+build\s+" +
                [regex]::Escape($expectedRepositoryPath) +
                "(?:\s+(?:-c|--configuration|-r|--runtime|-o|--output)\s+[^\s;&|]+)*$"
            $isSafeProjectBuild =
                $commandPath.EndsWith(".csproj", [System.StringComparison]::OrdinalIgnoreCase) -and
                $normalizedCommand -match $safeProjectBuildPattern
            if ($mentionsRawTestTool -and -not $isSafeProjectBuild) {
                throw "Command '$($pool.id)/$($command.id)' must use kind 'proof-test' instead of invoking dotnet directly."
            }
            if (-not $isDirectScriptCommand -and -not $isSafeProjectBuild) {
                throw "Repository command '$($pool.id)/$($command.id)' must directly invoke its declared script or use the restricted project build contract."
            }
        } elseif ($mentionsRawTestTool) {
            throw "Command '$($pool.id)/$($command.id)' must use kind 'proof-test' instead of invoking dotnet directly."
        }
        if ($command.kind -in @("repository", "proof-test") -and
            -not $command.PSObject.Properties.Name.Contains("path")) {
            throw "Repository-backed command '$($pool.id)/$($command.id)' must declare path."
        }
        if ($command.PSObject.Properties.Name.Contains("path")) {
            [void](Resolve-RepositoryFile `
                -RelativePath ([string]$command.path) `
                -Context "Proof-pool entry point for '$($pool.id)/$($command.id)'")
        }
    }
}

$templatePath = Join-Path $repoRootPath ".github\pull_request_template.md"
$expectedHeading = "## $($inventory.declaration.prBodySection)"
$templateHeadings = @(
    [System.IO.File]::ReadAllLines($templatePath) |
        Where-Object { $_ -ceq $expectedHeading }
)
if ($templateHeadings.Count -ne 1) {
    throw "PR template must contain exactly one declaration heading: $expectedHeading"
}

$proofPoolDocsPath = Join-Path $repoRootPath "docs\PROOF_POOLS.md"
$documentedPoolIds = @(
    [System.IO.File]::ReadAllLines($proofPoolDocsPath) |
        ForEach-Object {
            $match = [regex]::Match($_, '^\|\s*`(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)`\s*\|')
            if ($match.Success) {
                $match.Groups["id"].Value
            }
        }
)
$duplicateDocumentedIds = @(
    $documentedPoolIds |
        Group-Object |
        Where-Object Count -gt 1 |
        ForEach-Object Name
)
if ($duplicateDocumentedIds.Count -gt 0) {
    throw "Proof-pool documentation contains duplicate IDs: $($duplicateDocumentedIds -join ', ')"
}

$poolIdSet = New-Object "System.Collections.Generic.HashSet[string]" (
    [System.StringComparer]::Ordinal)
foreach ($poolId in $poolIds) {
    [void]$poolIdSet.Add($poolId)
}
$documentedPoolIdSet = New-Object "System.Collections.Generic.HashSet[string]" (
    [System.StringComparer]::Ordinal)
foreach ($documentedPoolId in $documentedPoolIds) {
    [void]$documentedPoolIdSet.Add($documentedPoolId)
}
$missingDocumentedIds = @($poolIds | Where-Object { -not $documentedPoolIdSet.Contains($_) })
$unknownDocumentedIds = @($documentedPoolIds | Where-Object { -not $poolIdSet.Contains($_) })
if ($missingDocumentedIds.Count -gt 0 -or $unknownDocumentedIds.Count -gt 0) {
    throw "Proof-pool documentation ID drift. Missing: $($missingDocumentedIds -join ', '); unknown: $($unknownDocumentedIds -join ', ')."
}

Write-Host "Proof-pool validation passed: $($inventory.pools.Count) named pools checked." -ForegroundColor Green
