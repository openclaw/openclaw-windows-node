# Shared by the maintained-doc and skill validators. No network access.
function ConvertTo-GitHubAnchor([string]$Heading) {
    $value = [regex]::Replace($Heading.ToLowerInvariant(), "<[^>]+>", "")
    $value = $value -replace '[`*_~]', ''
    $value = [regex]::Replace($value, "[^\p{L}\p{Nd}\s\-_]", "")
    return [regex]::Replace($value, "\s", "-")
}

function Get-MarkdownAnchors([string]$Path) {
    $anchors = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $counts = @{}
    $fence = ""
    $previous = ""
    $firstLine = $true
    $frontMatter = $false
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ($firstLine) {
            $firstLine = $false
            if ($line -ceq '---') { $frontMatter = $true; continue }
        }
        if ($frontMatter) {
            if ($line -ceq '---') { $frontMatter = $false }
            continue
        }
        if ($line -match '^\s{0,3}(`{3,}|~{3,})') {
            $marker = $Matches[1]
            if (-not $fence) { $fence = $marker }
            elseif ($marker[0] -eq $fence[0] -and $marker.Length -ge $fence.Length) { $fence = "" }
            $previous = ""
            continue
        }
        if ($fence) { continue }
        foreach ($explicit in [regex]::Matches($line, '<(?:a|[a-z][a-z0-9]*)\b[^>]*\b(?:id|name)=["''](?<id>[^"'']+)["'']', 'IgnoreCase')) {
            [void]$anchors.Add($explicit.Groups['id'].Value)
        }
        $heading = $null
        if ($line -match '^\s{0,3}#{1,6}\s+(?<heading>.+?)(?:\s+#+)?\s*$') {
            $heading = $Matches['heading']
        } elseif ($previous.Trim() -and $line -match '^\s{0,3}(?:=+|-+)\s*$') {
            $heading = $previous.Trim()
        }
        $previous = $line
        if ($null -eq $heading) { continue }
        $base = ConvertTo-GitHubAnchor $heading
        $count = if ($counts.ContainsKey($base)) { [int]$counts[$base] } else { 0 }
        $anchor = if ($count -eq 0) { $base } else { "$base-$count" }
        while ($anchors.Contains($anchor)) { $count++; $anchor = "$base-$count" }
        $counts[$base] = $count + 1
        [void]$anchors.Add($anchor)
    }
    return ,$anchors
}

function Assert-RepositoryPath {
    param([string]$Path, [string]$Root)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    if ($fullPath -ne $rootPath -and -not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "local link leaves the repository: $Path"
    }
    # Refuse indirection before reading or enumerating it. This also covers a
    # dangling junction, which Test-Path alone cannot establish is safe.
    $current = $fullPath
    while ($current -and $current.Length -ge $rootPath.Length) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "repository path contains a reparse point (not followed): $Path"
        }
        if ($current -eq $rootPath) { break }
        $current = Split-Path -Parent $current
    }
    return $fullPath
}
