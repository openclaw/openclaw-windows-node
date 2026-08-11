<#
.SYNOPSIS
    Validates maintained Markdown and Excalidraw-backed documentation diagrams.

.DESCRIPTION
    Checks local Markdown links, local Markdown anchors, the repository Mermaid
    policy, diagram source/render pairing, Excalidraw text invariants, SVG
    accessibility metadata, and source/render label synchronization.
#>

param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError([string]$message) {
    $script:errors.Add($message)
}

function Get-RelativePath([string]$path) {
    $root = $repoRootPath.TrimEnd("\") + "\"
    $full = [System.IO.Path]::GetFullPath($path)
    if ($full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length).Replace("\", "/")
    }
    return $full.Replace("\", "/")
}

function Test-IsMaintainedMarkdown([System.IO.FileInfo]$file) {
    $path = $file.FullName
    if ($path -match "[\\/](bin|obj|node_modules|\.git|\.squad|\.agents)[\\/]") {
        return $false
    }
    if ($file.Name -in @("THIRD_PARTY_NOTICES.md", "VENDORED.md")) {
        return $false
    }
    return $true
}

function Normalize-VisibleText([string]$value) {
    if ($null -eq $value) {
        return ""
    }
    return (($value -replace "[\r\n]+", " ") -replace "\s+", " ").Trim()
}

function ConvertTo-GitHubAnchor([string]$heading) {
    $value = $heading.ToLowerInvariant()
    $value = [regex]::Replace($value, "<[^>]+>", "")
    $value = $value -replace "[`*_~]", ""
    $value = [regex]::Replace($value, "[^\p{L}\p{Nd}\s\-_]", "")
    $value = [regex]::Replace($value, "\s", "-")
    return $value
}

function Get-MarkdownAnchors([string]$path) {
    $anchors = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $counts = @{}

    foreach ($line in [System.IO.File]::ReadLines($path)) {
        $match = [regex]::Match($line, "^\s{0,3}#{1,6}\s+(?<heading>.+?)\s*#*\s*$")
        if (-not $match.Success) {
            continue
        }

        $baseAnchor = ConvertTo-GitHubAnchor $match.Groups["heading"].Value
        if ([string]::IsNullOrWhiteSpace($baseAnchor)) {
            continue
        }

        $count = if ($counts.ContainsKey($baseAnchor)) { [int]$counts[$baseAnchor] } else { 0 }
        $anchor = if ($count -eq 0) { $baseAnchor } else { "$baseAnchor-$count" }
        $counts[$baseAnchor] = $count + 1
        [void]$anchors.Add($anchor)
    }

    return $anchors
}

$gitMarkdownPaths = @(
    & git -C $repoRootPath ls-files --cached --others --exclude-standard -- "*.md"
)
$gitMarkdownSucceeded = $LASTEXITCODE -eq 0
$global:LASTEXITCODE = 0

$markdownFiles = if ($gitMarkdownSucceeded) {
    @(
        $gitMarkdownPaths |
            Where-Object { Test-Path -LiteralPath (Join-Path $repoRootPath $_) } |
            ForEach-Object { Get-Item -LiteralPath (Join-Path $repoRootPath $_) } |
            Where-Object { Test-IsMaintainedMarkdown $_ }
    )
} else {
    @(
        Get-ChildItem -LiteralPath $repoRootPath -Filter "*.md" -Recurse -File -Force |
            Where-Object { Test-IsMaintainedMarkdown $_ }
    )
}

$anchorCache = @{}
$linkPattern = [regex]'!?\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^)]*["''])?\)'

foreach ($file in $markdownFiles) {
    $relativeFile = Get-RelativePath $file.FullName
    $content = [System.IO.File]::ReadAllText($file.FullName)

    if ($content.Contains([string][char]0x2014)) {
        Add-ValidationError "${relativeFile}: user-facing documentation must not use em dashes"
    }

    if ($content -match '(?m)^\s{0,3}>?\s*```+\s*mermaid\b') {
        Add-ValidationError "${relativeFile}: maintained Mermaid diagrams are not allowed; use paired .excalidraw and .svg files"
    }

    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups["target"].Value.Trim().Trim("<", ">")
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith("/") -or
            $target -match "^[a-z][a-z0-9+.-]*:" -or
            $target.Contains("{") -or
            $target.Contains("}")) {
            continue
        }

        $decoded = [System.Uri]::UnescapeDataString($target)
        $fragment = ""
        if ($decoded.StartsWith("#")) {
            $fragment = $decoded.Substring(1)
            $decoded = ""
        }
        $hashIndex = $decoded.IndexOf("#")
        if ($hashIndex -ge 0) {
            $fragment = $decoded.Substring($hashIndex + 1)
            $decoded = $decoded.Substring(0, $hashIndex)
        }
        $queryIndex = $decoded.IndexOf("?")
        if ($queryIndex -ge 0) {
            $decoded = $decoded.Substring(0, $queryIndex)
        }
        $resolved = if ([string]::IsNullOrWhiteSpace($decoded)) {
            $file.FullName
        } else {
            [System.IO.Path]::GetFullPath(
                (Join-Path -Path $file.DirectoryName -ChildPath ($decoded -replace "/", "\")))
        }
        if (-not (Test-Path -LiteralPath $resolved)) {
            Add-ValidationError "${relativeFile}: local link target does not exist: $target"
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($fragment) -and
            [System.IO.Path]::GetExtension($resolved).Equals(
                ".md",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            if (-not $anchorCache.ContainsKey($resolved)) {
                $anchorCache[$resolved] = Get-MarkdownAnchors $resolved
            }
            if (-not $anchorCache[$resolved].Contains(
                    [System.Uri]::UnescapeDataString($fragment))) {
                Add-ValidationError "${relativeFile}: Markdown anchor does not exist: $target"
            }
        }
    }
}

$diagramDirectory = Join-Path $repoRootPath "docs\diagrams"
if (Test-Path -LiteralPath $diagramDirectory) {
    $excalidrawFiles = @(Get-ChildItem -LiteralPath $diagramDirectory -Filter "*.excalidraw" -File)
    $svgFiles = @(Get-ChildItem -LiteralPath $diagramDirectory -Filter "*.svg" -File)
    $allMarkdown = ($markdownFiles | ForEach-Object {
        [System.IO.File]::ReadAllText($_.FullName)
    }) -join "`n"

    foreach ($source in $excalidrawFiles) {
        $relativeSource = Get-RelativePath $source.FullName
        $svgPath = [System.IO.Path]::ChangeExtension($source.FullName, ".svg")
        if (-not (Test-Path -LiteralPath $svgPath)) {
            Add-ValidationError "${relativeSource}: rendered SVG pair is missing"
            continue
        }

        try {
            $diagram = [System.IO.File]::ReadAllText($source.FullName) | ConvertFrom-Json
        } catch {
            Add-ValidationError "${relativeSource}: invalid Excalidraw JSON: $($_.Exception.Message)"
            continue
        }

        if ($diagram.type -ne "excalidraw" -or $diagram.version -ne 2) {
            Add-ValidationError "${relativeSource}: expected Excalidraw type with version 2"
        }

        $sourceTexts = [System.Collections.Generic.List[string]]::new()
        foreach ($element in @($diagram.elements)) {
            if ($element.type -ne "text") {
                continue
            }
            if ($null -eq $element.width -or $null -eq $element.height) {
                Add-ValidationError "${relativeSource}: text element '$($element.id)' needs explicit width and height"
            }
            if ($element.strokeColor -ne "#000000") {
                Add-ValidationError "${relativeSource}: text element '$($element.id)' must use black text"
            }
            $sourceTexts.Add((Normalize-VisibleText ([string]$element.text)))
        }

        $relativeSvg = Get-RelativePath $svgPath
        try {
            [xml]$svg = [System.IO.File]::ReadAllText($svgPath)
        } catch {
            Add-ValidationError "${relativeSvg}: invalid SVG XML: $($_.Exception.Message)"
            continue
        }

        $svgRoot = $svg.DocumentElement
        $titleNode = @($svgRoot.ChildNodes | Where-Object { $_.LocalName -eq "title" })[0]
        $descNode = @($svgRoot.ChildNodes | Where-Object { $_.LocalName -eq "desc" })[0]
        if ($null -eq $titleNode -or [string]::IsNullOrWhiteSpace($titleNode.InnerText)) {
            Add-ValidationError "${relativeSvg}: accessible <title> is required"
        }
        if ($null -eq $descNode -or [string]::IsNullOrWhiteSpace($descNode.InnerText)) {
            Add-ValidationError "${relativeSvg}: accessible <desc> is required"
        }

        $svgTexts = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($textNode in @($svg.GetElementsByTagName("text"))) {
            $tspanTexts = @(
                $textNode.ChildNodes |
                    Where-Object { $_.LocalName -eq "tspan" } |
                    ForEach-Object { $_.InnerText }
            )
            $visibleText = if ($tspanTexts.Count -gt 0) {
                $tspanTexts -join " "
            } else {
                $textNode.InnerText
            }
            [void]$svgTexts.Add((Normalize-VisibleText $visibleText))
        }
        foreach ($sourceText in $sourceTexts) {
            if (-not $svgTexts.Contains($sourceText)) {
                Add-ValidationError "${relativeSvg}: rendered labels are out of sync with $relativeSource ('$sourceText')"
            }
        }
        $sourceTextSet = [System.Collections.Generic.HashSet[string]]::new(
            $sourceTexts,
            [System.StringComparer]::Ordinal)
        foreach ($svgText in $svgTexts) {
            if (-not [string]::IsNullOrWhiteSpace($svgText) -and
                -not $sourceTextSet.Contains($svgText)) {
                Add-ValidationError "${relativeSvg}: rendered SVG contains a label absent from $relativeSource ('$svgText')"
            }
        }

        if (-not $allMarkdown.Contains($source.Name)) {
            Add-ValidationError "${relativeSource}: source is not linked from maintained Markdown"
        }
        if (-not $allMarkdown.Contains([System.IO.Path]::GetFileName($svgPath))) {
            Add-ValidationError "${relativeSvg}: rendered diagram is not embedded in maintained Markdown"
        }
    }

    foreach ($svgFile in $svgFiles) {
        $sourcePath = [System.IO.Path]::ChangeExtension($svgFile.FullName, ".excalidraw")
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            Add-ValidationError "$(Get-RelativePath $svgFile.FullName): editable Excalidraw source is missing"
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "`nDocumentation validation failed:" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host "  - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Documentation validation passed: $($markdownFiles.Count) Markdown files checked." -ForegroundColor Green
exit 0
