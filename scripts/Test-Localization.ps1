<#
.SYNOPSIS
    Validates WinUI localization resources and selected localization tests.

.DESCRIPTION
    Checks source XAML for controls with x:Uid values that are missing matching
    Resources.resw entries. It also reports candidate hard-coded localizable XAML
    strings so localization gaps are visible during review.

    By default the script also runs the focused tray localization test filters.
    Use -SkipDotNetTests when you only want the static XAML/resource scan.

.PARAMETER StrictHardcodedXaml
    Treat candidate hard-coded XAML strings as errors instead of warnings.

.PARAMETER SkipDotNetTests
    Skip the focused dotnet test invocation and only run the static scan.

.EXAMPLE
    .\scripts\Test-Localization.ps1

.EXAMPLE
    .\scripts\Test-Localization.ps1 -StrictHardcodedXaml
#>
[CmdletBinding()]
param(
    [switch]$StrictHardcodedXaml,
    [switch]$SkipDotNetTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$winUiRoot = Join-Path $repoRoot 'src\OpenClaw.Tray.WinUI'
$stringsDir = Join-Path $winUiRoot 'Strings'
$enUsResw = Join-Path $stringsDir 'en-us\Resources.resw'
$localizableAttributes = @('Content', 'Description', 'Header', 'Message', 'PlaceholderText', 'Text', 'Title')

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $pathFullPath = [System.IO.Path]::GetFullPath($Path)
    $baseUri = [System.Uri]::new($baseFullPath)
    $pathUri = [System.Uri]::new($pathFullPath)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-ResourceKeys {
    [xml]$resources = Get-Content -LiteralPath $enUsResw -Raw -Encoding UTF8
    $keys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($data in $resources.root.data) {
        [void]$keys.Add([string]$data.name)
    }
    return $keys
}

function Test-IsLocalizableValue {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    if ($Value -notmatch '\p{L}') { return $false }

    $nonLocalizablePrefixes = @(
        '{Binding',
        '{x:Bind',
        '{StaticResource',
        '{ThemeResource',
        '{TemplateBinding',
        'ms-appx:///',
        'http://',
        'https://'
    )

    foreach ($prefix in $nonLocalizablePrefixes) {
        if ($Value.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-IsSourceXaml {
    param([System.IO.FileInfo]$File)

    $relativePath = Get-RelativePath $winUiRoot $File.FullName
    $segments = $relativePath -split '[\\/]'
    # Build output can contain generated/copied XAML; only source XAML should drive localization findings.
    return $segments -notcontains 'bin' -and $segments -notcontains 'obj'
}

function Get-XamlLocalizationFindings {
    $resourceKeys = Get-ResourceKeys
    $missingResources = [System.Collections.Generic.List[string]]::new()
    $hardcodedValues = [System.Collections.Generic.List[string]]::new()

    Get-ChildItem -LiteralPath $winUiRoot -Recurse -Filter *.xaml -File |
        Where-Object { Test-IsSourceXaml $_ } |
        Sort-Object FullName |
        ForEach-Object {
        $xamlPath = $_.FullName
        $relativePath = Get-RelativePath $repoRoot $xamlPath
        [xml]$xml = Get-Content -LiteralPath $xamlPath -Raw -Encoding UTF8

        $navigator = $xml.CreateNavigator()
        $namespaceManager = [System.Xml.XmlNamespaceManager]::new($navigator.NameTable)
        $namespaceManager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')

        foreach ($node in $xml.SelectNodes('//*[@x:Uid]', $namespaceManager)) {
            $uid = $node.Attributes['Uid', 'http://schemas.microsoft.com/winfx/2006/xaml'].Value
            foreach ($attributeName in $localizableAttributes) {
                $attribute = $node.Attributes[$attributeName]
                if ($null -eq $attribute -or -not (Test-IsLocalizableValue $attribute.Value)) {
                    continue
                }

                $key = "$uid.$attributeName"
                if (-not $resourceKeys.Contains($key)) {
                    $missingResources.Add("${relativePath}: missing $key")
                }
            }
        }

        foreach ($node in $xml.SelectNodes('//*')) {
            if ($null -ne $node.Attributes['Uid', 'http://schemas.microsoft.com/winfx/2006/xaml']) {
                continue
            }

            foreach ($attributeName in $localizableAttributes) {
                $attribute = $node.Attributes[$attributeName]
                if ($null -ne $attribute -and (Test-IsLocalizableValue $attribute.Value)) {
                    $hardcodedValues.Add("${relativePath}: <$($node.Name)> $attributeName=`"$($attribute.Value)`"")
                }
            }
        }
    }

    [pscustomobject]@{
        MissingResources = $missingResources
        HardcodedValues = $hardcodedValues
    }
}

if (-not $SkipDotNetTests) {
    $env:OPENCLAW_REPO_ROOT = $repoRoot
    dotnet test (Join-Path $repoRoot 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj') --filter 'FullyQualifiedName~LocalizationValidationTests|FullyQualifiedName~CapabilitiesPageLocalizationCoverageTests' --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$findings = Get-XamlLocalizationFindings

if ($findings.MissingResources.Count -gt 0) {
    Write-Error ("Missing Resources.resw entries for x:Uid controls:`n" + ($findings.MissingResources -join "`n"))
    exit 1
}

if ($findings.HardcodedValues.Count -gt 0) {
    $message = "Found $($findings.HardcodedValues.Count) candidate hard-coded XAML string(s)."
    if ($StrictHardcodedXaml) {
        Write-Error ($message + "`n" + ($findings.HardcodedValues | Select-Object -First 200 | Out-String))
        exit 1
    }

    Write-Warning $message
    $findings.HardcodedValues | Select-Object -First 200 | ForEach-Object { Write-Warning $_ }
    Write-Warning "Re-run with -StrictHardcodedXaml to fail on these candidates."
}

Write-Host "Localization check completed."
