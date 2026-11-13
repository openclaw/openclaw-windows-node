<#
.SYNOPSIS
Maps changed source lines to the xUnit tests they touch, for the CI new-test soak.
.DESCRIPTION
Pure helpers with no git or process access, so scripts\test-ci-changed-tests.ps1 can test them.

- ConvertTo-CiSanitizedSource blanks strings, characters and comments (newlines kept), so braces,
  attributes and declarations can be found by position.
- Get-CiTestMethods returns each [*Fact]/[*Theory] method with its type, namespace and line span
  (from its first attribute line to its closing brace).
- Get-CiChangedLineMap reads `git diff --unified=0` output into new-side line numbers per path. A pure
  deletion counts as the lines on both sides of where it was. Lines of a hunk that removed code are
  also listed as Forced (Test-CiRemovedCode); code inside a removed test does not count.
- Select-CiChangedTests picks tests whose span contains a changed line, including comment and string
  edits. A changed line outside every test falls back to the tests of the nearest enclosing type that
  contains tests in that file, or of the whole file (for example a using directive or a helper type).
  Blank and comment-only lines outside tests are ignored unless they are Forced.

Example:
    . .\scripts\CiChangedTests.ps1
    $methods = Get-CiTestMethods -Source (Get-Content -Raw tests\X.Tests\FooTests.cs)
    $changed = (Get-CiChangedLineMap -DiffText $diff)['tests/X.Tests/FooTests.cs']
    $selected = Select-CiChangedTests -Methods $methods -Source $source -ChangedLines $changed.Lines -ForcedLines @($changed.Forced)
#>

function ConvertTo-CiSanitizedSource {
    # -CommentsOnly keeps string and character content, so only blank and comment-only lines read as empty.
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Source, [switch]$CommentsOnly)
    $text = $Source.ToCharArray()
    $length = $text.Length
    $blank = { param([int]$from, [int]$to) for ($k = $from; $k -lt $to -and $k -lt $length; $k++) { if ($text[$k] -ne "`n" -and $text[$k] -ne "`r") { $text[$k] = ' ' } } }
    $i = 0
    # Skips a regular, verbatim, interpolated or raw string starting at $start and returns the index after it.
    $skipString = $null
    $skipString = {
        param([int]$start)
        $j = $start
        $interpolated = $false
        $verbatim = $false
        while ($j -lt $length -and ($text[$j] -eq '$' -or $text[$j] -eq '@')) {
            if ($text[$j] -eq '$') { $interpolated = $true } else { $verbatim = $true }
            $j++
        }
        $quotes = 0
        while ($j + $quotes -lt $length -and $text[$j + $quotes] -eq '"') { $quotes++ }
        if ($quotes -ge 3 -and -not $verbatim) {
            $j += $quotes
            while ($j -lt $length) {
                $run = 0
                while ($j + $run -lt $length -and $text[$j + $run] -eq '"') { $run++ }
                if ($run -ge $quotes) { return $j + $run }
                $j += [Math]::Max(1, $run)
            }
            return $length
        }
        $j++
        $depth = 0
        while ($j -lt $length) {
            $c = $text[$j]
            if ($depth -gt 0) {
                if ($c -eq '"' -or (($c -eq '$' -or $c -eq '@') -and $j + 1 -lt $length -and ($text[$j + 1] -eq '"' -or $text[$j + 1] -eq '$' -or $text[$j + 1] -eq '@'))) { $j = & $skipString $j; continue }
                if ($c -eq '{') { $depth++ } elseif ($c -eq '}') { $depth-- }
                $j++
                continue
            }
            if (-not $verbatim -and $c -eq '\') { $j += 2; continue }
            if ($c -eq '"') {
                if ($verbatim -and $j + 1 -lt $length -and $text[$j + 1] -eq '"') { $j += 2; continue }
                return $j + 1
            }
            if ($interpolated -and $c -eq '{') {
                if ($j + 1 -lt $length -and $text[$j + 1] -eq '{') { $j += 2; continue }
                $depth = 1
            }
            if (-not $verbatim -and $c -eq "`n") { return $j }
            $j++
        }
        return $length
    }
    while ($i -lt $length) {
        $c = $text[$i]
        $next = if ($i + 1 -lt $length) { $text[$i + 1] } else { [char]0 }
        if ($c -eq '/' -and $next -eq '/') {
            $end = $i
            while ($end -lt $length -and $text[$end] -ne "`n") { $end++ }
            & $blank $i $end
            $i = $end
            continue
        }
        if ($c -eq '/' -and $next -eq '*') {
            $end = $Source.IndexOf('*/', $i + 2, [StringComparison]::Ordinal)
            $end = if ($end -lt 0) { $length } else { $end + 2 }
            & $blank $i $end
            $i = $end
            continue
        }
        if ($c -eq '"' -or (($c -eq '$' -or $c -eq '@') -and ($next -eq '"' -or $next -eq '$' -or $next -eq '@'))) {
            $end = & $skipString $i
            if (-not $CommentsOnly) { & $blank $i $end }
            $i = $end
            continue
        }
        if ($c -eq "'") {
            $end = $i + 1
            while ($end -lt $length -and $text[$end] -ne "'" -and $text[$end] -ne "`n") {
                if ($text[$end] -eq '\') { $end++ }
                $end++
            }
            if (-not $CommentsOnly) { & $blank $i ([Math]::Min($length, $end + 1)) }
            $i = $end + 1
            continue
        }
        $i++
    }
    return -join $text
}

function Get-CiLineStarts {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $starts = [Collections.Generic.List[int]]::new()
    $starts.Add(0)
    for ($k = 0; $k -lt $Text.Length; $k++) { if ($Text[$k] -eq "`n") { $starts.Add($k + 1) } }
    return , $starts.ToArray()
}

function Get-CiLineNumber {
    param([int[]]$LineStarts, [int]$Position)
    $index = [Array]::BinarySearch($LineStarts, $Position)
    if ($index -lt 0) { $index = (-bnot $index) - 1 }
    return $index + 1
}

function Get-CiMatchingBraces {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Sanitized)
    $pairs = @{}
    $stack = [Collections.Generic.Stack[int]]::new()
    for ($k = 0; $k -lt $Sanitized.Length; $k++) {
        if ($Sanitized[$k] -eq '{') { $stack.Push($k) }
        elseif ($Sanitized[$k] -eq '}' -and $stack.Count) { $pairs[$stack.Pop()] = $k }
    }
    return $pairs
}

function Get-CiTypeRanges {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Sanitized, [Parameter(Mandatory)][hashtable]$Braces)
    $types = [Collections.Generic.List[object]]::new()
    # Constraints such as 'where T : class' are not declarations.
    foreach ($match in [regex]::Matches($Sanitized, '(?<![:,]\s*)\b(?:class|struct|interface|record(?!\s+(?:class|struct)\b))\s+(\w+)')) {
        $k = $match.Index + $match.Length
        while ($k -lt $Sanitized.Length -and $Sanitized[$k] -ne '{' -and $Sanitized[$k] -ne ';') { $k++ }
        if ($k -lt $Sanitized.Length -and $Sanitized[$k] -eq '{' -and $Braces.ContainsKey($k)) {
            $types.Add([pscustomobject]@{ Name = $match.Groups[1].Value; Start = $k; End = $Braces[$k] })
        }
    }
    return , $types.ToArray()
}

function Get-CiTestMethods {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Source)
    $sanitized = ConvertTo-CiSanitizedSource -Source $Source
    $lines = Get-CiLineStarts -Text $sanitized
    $braces = Get-CiMatchingBraces -Sanitized $sanitized

    $namespaces = [Collections.Generic.List[object]]::new()
    $fileScoped = [regex]::Match($sanitized, '(?m)^\s*namespace\s+([\w.]+)\s*;')
    if ($fileScoped.Success) { $namespaces.Add([pscustomobject]@{ Name = $fileScoped.Groups[1].Value; Start = 0; End = $sanitized.Length }) }
    foreach ($match in [regex]::Matches($sanitized, '\bnamespace\s+([\w.]+)\s*\{')) {
        $open = $match.Index + $match.Length - 1
        if ($braces.ContainsKey($open)) { $namespaces.Add([pscustomobject]@{ Name = $match.Groups[1].Value; Start = $open; End = $braces[$open] }) }
    }

    $types = Get-CiTypeRanges -Sanitized $sanitized -Braces $braces

    $methods = [Collections.Generic.List[object]]::new()
    $seen = @{}
    # A test attribute may follow others in the same list, for example [Trait(...), Fact].
    foreach ($attribute in [regex]::Matches($sanitized, '\[\s*(?:[^\[\]]*?,\s*)?(?:[\w.]+\.)?\w*(?:Fact|Theory)(?:Attribute)?\s*[(\],]')) {
        $k = $attribute.Index
        $depth = 0
        while ($k -lt $sanitized.Length) {
            if ($sanitized[$k] -eq '[') { $depth++ } elseif ($sanitized[$k] -eq ']') { $depth--; if ($depth -eq 0) { break } }
            $k++
        }
        # Skip any further attribute lists, then read the declaration up to its parameter list.
        $k++
        while ($true) {
            while ($k -lt $sanitized.Length -and [char]::IsWhiteSpace($sanitized[$k])) { $k++ }
            if ($k -ge $sanitized.Length -or $sanitized[$k] -ne '[') { break }
            $depth = 0
            while ($k -lt $sanitized.Length) {
                if ($sanitized[$k] -eq '[') { $depth++ } elseif ($sanitized[$k] -eq ']') { $depth--; if ($depth -eq 0) { $k++; break } }
                $k++
            }
        }
        $paren = $sanitized.IndexOf('(', $k)
        if ($paren -lt 0) { continue }
        $declaration = $sanitized.Substring($k, $paren - $k)
        if ($declaration -match '[{};]') { continue }
        $name = [regex]::Match($declaration, '(\w+)\s*(?:<[^<>]*>)?\s*$').Groups[1].Value
        if (-not $name) { continue }
        $depth = 0
        $close = $paren
        while ($close -lt $sanitized.Length) {
            if ($sanitized[$close] -eq '(') { $depth++ } elseif ($sanitized[$close] -eq ')') { $depth--; if ($depth -eq 0) { break } }
            $close++
        }
        $bodyStart = $close + 1
        # The body follows any generic constraints: a block, an expression body, or no body at all.
        $end = -1
        $depth = 0
        $expression = $false
        for ($scan = $bodyStart; $scan -lt $sanitized.Length; $scan++) {
            $c = $sanitized[$scan]
            if (-not $expression -and $depth -eq 0 -and $c -eq '{') {
                if ($braces.ContainsKey($scan)) { $end = $braces[$scan] }
                break
            }
            if (-not $expression -and $depth -eq 0 -and $c -eq '=' -and $scan + 1 -lt $sanitized.Length -and $sanitized[$scan + 1] -eq '>') {
                # An expression body ends at the first semicolon outside its own lambdas, calls and indexers.
                $expression = $true
                $scan++
                continue
            }
            if ($c -eq '(' -or $c -eq '[' -or ($expression -and $c -eq '{')) { $depth++ }
            elseif ($c -eq ')' -or $c -eq ']' -or ($expression -and $c -eq '}')) { $depth-- }
            elseif ($c -eq ';' -and $depth -le 0) { $end = $scan; break }
        }
        if ($end -lt 0) { continue }
        $startLine = Get-CiLineNumber -LineStarts $lines -Position $attribute.Index
        # Attribute lists directly above (for example [Trait]) belong to the test.
        while ($startLine -gt 1) {
            $previous = $sanitized.Substring($lines[$startLine - 2], $lines[$startLine - 1] - $lines[$startLine - 2]).Trim()
            if (-not $previous.StartsWith('[', [StringComparison]::Ordinal)) { break }
            $startLine--
        }
        if ($seen.ContainsKey($paren)) { continue }
        $seen[$paren] = $true
        $enclosing = @($types | Where-Object { $_.Start -lt $paren -and $_.End -gt $paren } | Sort-Object Start)
        if (-not $enclosing.Count) { continue }
        $namespace = @($namespaces | Where-Object { $_.Start -le $paren -and $_.End -ge $paren } | Sort-Object Start | ForEach-Object Name) -join '.'
        $typeName = ($enclosing | ForEach-Object Name) -join '+'
        $methods.Add([pscustomobject]@{
                Namespace = $namespace
                Type = $typeName
                TypeStart = $enclosing[-1].Start
                TypeEnd = $enclosing[-1].End
                Method = $name
                FullyQualifiedName = (@($namespace, $typeName, $name) | Where-Object { $_ }) -join '.'
                StartLine = $startLine
                EndLine = Get-CiLineNumber -LineStarts $lines -Position $end
            })
    }
    return , $methods.ToArray()
}

function Get-CiChangedLineMap {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$DiffText)
    # Each path maps to its changed new-side Lines and the Forced subset: lines of a hunk that removed
    # code. Those count even when what replaced the code is blank or a comment. Removed code inside a
    # removed test does not count: a deleted test needs no soak.
    $map = @{}
    $path = $null
    $hunk = $null
    $finish = {
        if ($hunk -and $hunk.Removed.Count -and (Test-CiRemovedCode -Lines $hunk.Removed.ToArray())) {
            foreach ($number in $hunk.Lines) { [void]$map[$hunk.Path].Forced.Add($number) }
        }
    }
    foreach ($line in ($DiffText -split "`r?`n")) {
        if ($line.StartsWith('diff ', [StringComparison]::Ordinal)) { . $finish; $path = $null; $hunk = $null; continue }
        if (-not $hunk -and $line.StartsWith('+++ ', [StringComparison]::Ordinal)) {
            $target = $line.Substring(4).Trim()
            $path = if ($target -eq '/dev/null') { $null } else { $target -replace '^b/', '' }
            if ($path -and -not $map.ContainsKey($path)) {
                $map[$path] = [pscustomobject]@{ Lines = [Collections.Generic.List[int]]::new(); Forced = [Collections.Generic.HashSet[int]]::new() }
            }
            continue
        }
        if ($path -and $line -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
            . $finish
            $start = [int]$Matches[1]
            $count = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
            # A pure deletion has no new-side lines; mark the lines on both sides of where it was.
            $numbers = if ($count -eq 0) { @($start, ($start + 1) | Where-Object { $_ -ge 1 }) } else { @($start..($start + $count - 1)) }
            $hunk = [pscustomobject]@{ Path = $path; Lines = $numbers; Removed = [Collections.Generic.List[string]]::new() }
            foreach ($number in $numbers) { $map[$path].Lines.Add($number) }
            continue
        }
        if ($hunk -and $line.StartsWith('-', [StringComparison]::Ordinal)) { $hunk.Removed.Add($line.Substring(1)) }
    }
    . $finish
    return $map
}

# True when removed lines hold code or string data outside comments and whole removed tests. The
# lines are parsed inside a stand-in type; a fragment that does not parse cleanly counts every
# non-comment line, so an unclear removal still selects tests.
function Test-CiRemovedCode {
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines)
    $source = "class RemovedLines`n{`n" + ($Lines -join "`n") + "`n}`n"
    # String content can be fixture data, so only comments are blanked when judging a line.
    $contentLines = (ConvertTo-CiSanitizedSource -Source $source -CommentsOnly) -split "`n"
    $tests = Get-CiTestMethods -Source $source
    for ($k = 0; $k -lt $Lines.Count; $k++) {
        $number = $k + 3
        if (@($tests | Where-Object { $_.StartLine -le $number -and $_.EndLine -ge $number }).Count) { continue }
        if ($contentLines[$number - 1].Trim()) { return $true }
    }
    return $false
}

function Select-CiChangedTests {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Methods,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Source,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$ChangedLines,
        # Changed lines that replaced or sat next to removed code; blank or comment text does not excuse them.
        [int[]]$ForcedLines = @()
    )
    $sanitized = ConvertTo-CiSanitizedSource -Source $Source
    $codeAndStrings = ConvertTo-CiSanitizedSource -Source $Source -CommentsOnly
    $lineStarts = Get-CiLineStarts -Text $sanitized
    $types = Get-CiTypeRanges -Sanitized $sanitized -Braces (Get-CiMatchingBraces -Sanitized $sanitized)
    $selected = [ordered]@{}
    foreach ($line in ($ChangedLines | Sort-Object -Unique)) {
        if ($line -lt 1 -or $line -gt $lineStarts.Length) { continue }
        $from = $lineStarts[$line - 1]
        $to = if ($line -lt $lineStarts.Length) { $lineStarts[$line] } else { $sanitized.Length }
        # Any change inside a test, including comments and string content, selects that test.
        $owners = @($Methods | Where-Object { $_.StartLine -le $line -and $_.EndLine -ge $line })
        $kind = 'method'
        if (-not $owners.Count) {
            # Outside every test, blank and comment-only lines change nothing that runs, unless they replaced code.
            if ($line -notin $ForcedLines -and -not $codeAndStrings.Substring($from, $to - $from).Trim()) { continue }
            $text = $sanitized.Substring($from, $to - $from)
            # Probe at the first code character, so a line holding a type's opening brace is inside that type.
            $probe = $from + ($text.Length - $text.TrimStart().Length)
            # The nearest enclosing type that contains tests (including nested types' tests); a line
            # outside every such type, for example a using directive or a helper type, selects the file.
            $kind = 'file'
            $owners = $Methods
            foreach ($type in @($types | Where-Object { $_.Start -le $probe -and $_.End -ge $probe } | Sort-Object Start -Descending)) {
                $contained = @($Methods | Where-Object { $_.TypeStart -ge $type.Start -and $_.TypeEnd -le $type.End })
                if ($contained.Count) {
                    $kind = 'class'
                    $owners = $contained
                    break
                }
            }
        }
        foreach ($owner in $owners) {
            if (-not $selected.Contains($owner.FullyQualifiedName) -or $kind -eq 'method') {
                $selected[$owner.FullyQualifiedName] = [pscustomobject]@{ FullyQualifiedName = $owner.FullyQualifiedName; Kind = $kind }
            }
        }
    }
    return , @($selected.Values)
}
