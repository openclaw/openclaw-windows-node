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
    if ($null -ne $items -and
        $items.Value -is [System.Management.Automation.PSCustomObject]) {
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
            foreach ($requiredName in @($required.Value)) {
                if ($null -eq (Get-JsonProperty -Value $Value -Name ([string]$requiredName))) {
                    throw "Proof-pool schema mismatch at ${JsonPath}: missing '$requiredName'."
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
        if ([string]$command.command -match "(?i)(^|[;&]\s*)dotnet\s+test\b") {
            throw "Proof test command '$($pool.id)/$($command.id)' must use scripts\run-proof-tests.ps1 so zero tests cannot pass."
        }
        if ($command.kind -eq "repository" -and
            -not $command.PSObject.Properties.Name.Contains("path")) {
            throw "Repository command '$($pool.id)/$($command.id)' must declare path."
        }
        if ($command.PSObject.Properties.Name.Contains("path")) {
            $entryPoint = [System.IO.Path]::GetFullPath(
                (Join-Path $repoRootPath ([string]$command.path)))
            $repoPrefix = $repoRootPath.TrimEnd("\") + "\"
            if (-not $entryPoint.StartsWith(
                    $repoPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Proof-pool entry point escapes the repository for '$($pool.id)/$($command.id)': $($command.path)"
            }
            if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
                throw "Proof-pool entry point is not a file for '$($pool.id)/$($command.id)': $($command.path)"
            }
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
exit 0
