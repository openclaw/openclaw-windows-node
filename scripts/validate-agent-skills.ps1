<#
.SYNOPSIS
    Validates repository-owned agent skill documentation.
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
$skillsRoot = Join-Path $repoRootPath ".agents\skills"
$errors = [System.Collections.Generic.List[string]]::new()
. (Join-Path $scriptRoot "MarkdownValidation.ps1")
[void](Assert-RepositoryPath -Path $skillsRoot -Root $repoRootPath)

function Read-SkillMetadata([string]$Content) {
    $header = [regex]::Match($Content, '\A---\r?\n(?<body>[\s\S]*?)\r?\n---(?:\r?\n|\z)')
    if (-not $header.Success) { throw "expected name and description scalar YAML front matter" }
    $metadata = @{}
    foreach ($line in ($header.Groups['body'].Value -split '\r?\n')) {
        if ($line -match '^\s*(?:#.*)?$') { continue }
        if ($line -cnotmatch '^(?<key>name|description):[ \t]*(?<value>.*)$') {
            throw "unsupported metadata: only single-line name and description scalars are supported"
        }
        $key = $Matches['key']
        $value = $Matches['value'].Trim()
        if ($metadata.ContainsKey($key)) { throw "duplicate metadata key: $key" }
        if ($value.StartsWith("'")) {
            if ($value -notmatch "^'(?<text>(?:[^']|'')*)'\s*(?:#.*)?$") { throw "invalid quoted scalar: $key" }
            $value = $Matches['text'].Replace("''", "'")
        } elseif ($value.StartsWith('"')) {
            if ($value -notmatch '^(?<text>"(?:[^"\\]|\\.)*")\s*(?:#.*)?$') { throw "invalid quoted scalar: $key" }
            try { $value = ConvertFrom-Json $Matches['text'] -ErrorAction Stop }
            catch { throw "invalid double-quoted scalar (use JSON-compatible escapes): $key" }
        } else {
            if ($value.StartsWith('#')) { $value = '' }
            $value = ($value -replace '\s+#.*$', '').Trim()
            if ($value -match '^[\[\]{}&*!|>@`%]' -or $value -match ':\s|^(?:null|~|true|false)$|^[+-]?\d+(?:\.\d+)?$') {
                throw "unsupported plain scalar: $key (quote string values)"
            }
        }
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]') { throw "$key must be a nonempty single-line string" }
        $metadata[$key] = $value
    }
    foreach ($key in @('name', 'description')) {
        if (-not $metadata.ContainsKey($key)) { throw "required metadata key is missing: $key" }
    }
    return $metadata
}

if (-not (Test-Path -LiteralPath $skillsRoot -PathType Container)) {
    throw "Agent skills directory is missing: $skillsRoot"
}

$skillsRootPrefix = $skillsRoot.TrimEnd("\") + "\"
$skillDirectories = @(Get-ChildItem -LiteralPath $skillsRoot -Directory)
if ($skillDirectories.Count -eq 0) {
    throw "Agent skills directory contains no skills."
}

foreach ($skillDirectory in $skillDirectories) {
    $skillFile = Join-Path $skillDirectory.FullName "SKILL.md"
    try { [void](Assert-RepositoryPath -Path $skillFile -Root $repoRootPath) }
    catch { $errors.Add($_.Exception.Message); continue }
    if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) {
        $errors.Add(".agents/skills/$($skillDirectory.Name): SKILL.md is required")
        continue
    }

    $content = [System.IO.File]::ReadAllText($skillFile)
    try { $metadata = Read-SkillMetadata $content }
    catch {
        $errors.Add(".agents/skills/$($skillDirectory.Name)/SKILL.md: $($_.Exception.Message)")
        continue
    }

    $name = $metadata.name
    if ($name -cne $skillDirectory.Name) {
        $errors.Add(".agents/skills/$($skillDirectory.Name)/SKILL.md: name '$name' must match the directory name")
    }
}

$linkPattern = [regex]'!?\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^)]*["''])?\)'
$markdownFiles = [System.Collections.Generic.List[IO.FileInfo]]::new()
$directories = [System.Collections.Generic.Queue[string]]::new()
$directories.Enqueue($skillsRoot)
while ($directories.Count -gt 0) {
    foreach ($item in Get-ChildItem -LiteralPath $directories.Dequeue() -Force) {
        try { [void](Assert-RepositoryPath -Path $item.FullName -Root $repoRootPath) }
        catch { $errors.Add($_.Exception.Message); continue }
        if ($item.PSIsContainer) { $directories.Enqueue($item.FullName) }
        elseif ($item.Extension -eq '.md') { $markdownFiles.Add($item) }
    }
}
$anchorCache = @{}
foreach ($markdownFile in $markdownFiles) {
    $relativePath = $markdownFile.FullName.Substring($skillsRootPrefix.Length).Replace("\", "/")
    $content = [System.IO.File]::ReadAllText($markdownFile.FullName)
    if ($content.Contains([string][char]0x2014)) {
        $errors.Add(".agents/skills/${relativePath}: agent-facing content must not use em dashes")
    }

    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups["target"].Value.Trim().Trim("<", ">")
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target -match "^(?:https?|mailto):" -or
            $target.Contains("{") -or
            $target.Contains("}")) {
            continue
        }

        $parts = $target.Split('#', 2)
        $decoded = [Uri]::UnescapeDataString($parts[0].Split('?', 2)[0])
        $fragment = if ($parts.Count -gt 1) { [Uri]::UnescapeDataString($parts[1]) } else { '' }
        try {
            if ($decoded -match '^[a-z][a-z0-9+.-]*:' -or [IO.Path]::IsPathRooted($decoded)) {
                throw "absolute filesystem links are not supported: $target"
            }
            $resolvedPath = if ($decoded) {
                Assert-RepositoryPath -Path (Join-Path $markdownFile.DirectoryName ($decoded -replace '/', '\')) -Root $repoRootPath
            } else { $markdownFile.FullName }
        } catch {
            $errors.Add(".agents/skills/${relativePath}: $($_.Exception.Message)")
            continue
        }
        if (-not (Test-Path -LiteralPath $resolvedPath)) {
            $errors.Add(".agents/skills/${relativePath}: local link target does not exist: $target")
        } elseif ($fragment -and [IO.Path]::GetExtension($resolvedPath) -eq '.md') {
            if (-not $anchorCache.ContainsKey($resolvedPath)) { $anchorCache[$resolvedPath] = Get-MarkdownAnchors $resolvedPath }
            if (-not $anchorCache[$resolvedPath].Contains($fragment)) {
                $errors.Add(".agents/skills/${relativePath}: Markdown anchor does not exist: $target")
            }
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "`nAgent skill validation failed:" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host "  - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Agent skill validation passed: $($skillDirectories.Count) skills and $($markdownFiles.Count) Markdown files checked." -ForegroundColor Green
exit 0
