<#
.SYNOPSIS
    Temporary native testhost diagnosis. Raw logs and dumps stay in RUNNER_TEMP.
.EXAMPLE
    . .\scripts\Invoke-CiNativeCrashProbe.ps1
    Invoke-CiNativeCrashProbe -TestArguments $arguments -ResultsDirectory $results
#>

function ConvertTo-CiNativeCrashSummary {
    param([Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Lines)

    $inside = $false
    $captured = $false
    $thread = 'exception'
    $codes = [Collections.Generic.List[string]]::new()
    $frames = [Collections.Generic.List[object]]::new()
    foreach ($line in $Lines) {
        if ($line.Trim() -ceq 'OPENCLAW_CRASH_BEGIN') {
            $inside = $true
            $captured = $true
            continue
        }
        if ($line.Trim() -ceq 'OPENCLAW_CRASH_END') { $inside = $false; continue }
        if (-not $inside) { continue }
        if ($line -match '^\s*ExceptionCode:\s+([0-9a-fA-F]{8})\b') {
            $codes.Add($Matches[1].ToLowerInvariant())
        } elseif ($line -match '^\s*[.#]?\s*\d+\s+Id:\s+[0-9a-fA-F]+\.([0-9a-fA-F]+)\s') {
            $thread = $Matches[1].ToLowerInvariant()
        } elseif ($frames.Count -lt 256 -and $line -match '^\s*([0-9a-fA-F]{1,3})\s+[0-9a-fA-F`]+\s+[0-9a-fA-F`]+\s+([A-Za-z0-9_.-]+![A-Za-z0-9_$?@<>:~+.,`\[\]/=-]+)') {
            $frames.Add([pscustomobject]@{
                thread = $thread
                frame = $Matches[1]
                symbol = $Matches[2]
            })
        }
    }
    [pscustomobject]@{
        crashCaptured = $captured
        exceptionCodes = @($codes | Select-Object -Unique)
        frames = $frames.ToArray()
    }
}

function Get-CiNativeDebugger {
    param([Parameter(Mandatory)][string]$PrivateDirectory)

    $installed = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Debuggers\x64\cdb.exe'
    if (Test-Path -LiteralPath $installed -PathType Leaf) { return $installed }

    # Pinned official debugger distribution, extracted without package registration.
    $bundle = Join-Path $PrivateDirectory 'windbg.msixbundle'
    Invoke-WebRequest -Uri 'https://windbg.download.prss.microsoft.com/dbazure/prod/1-2611-4001-0/windbg.msixbundle' -OutFile $bundle
    $signature = Get-AuthenticodeSignature -LiteralPath $bundle
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'Official debugger bundle signature verification failed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($bundle)
    try {
        $packages = @($archive.Entries | Where-Object FullName -ceq 'windbg_win-x64.msix')
        if ($packages.Count -ne 1) { throw 'Expected exactly one x64 debugger package.' }
        $package = Join-Path $PrivateDirectory 'windbg-x64.msix'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($packages[0], $package)
    } finally { $archive.Dispose() }
    $expanded = Join-Path $PrivateDirectory 'debugger'
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $expanded)
    $debugger = Join-Path $expanded 'amd64\cdb.exe'
    if (-not (Test-Path -LiteralPath $debugger -PathType Leaf)) { throw 'The debugger package has no amd64 cdb.exe.' }
    return $debugger
}

function Invoke-CiNativeCrashProbe {
    param(
        [Parameter(Mandatory)][string[]]$TestArguments,
        [Parameter(Mandatory)][string]$ResultsDirectory
    )

    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'Native CI capture requires RUNNER_TEMP.' }
    $private = [IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP ("openclaw-native-crash-" + [guid]::NewGuid().ToString('N'))))
    $workspace = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\') + '\'
    $public = [IO.Path]::GetFullPath($ResultsDirectory).TrimEnd('\') + '\'
    if ($private.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase) -or
        $private.StartsWith($public, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Private native diagnostics must be outside the checkout and artifact directory.'
    }
    New-Item -ItemType Directory -Path $private -ErrorAction Stop | Out-Null
    $debugger = Get-CiNativeDebugger -PrivateDirectory $private
    $signature = Get-AuthenticodeSignature -LiteralPath $debugger
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'Native debugger signature verification failed.'
    }
    $dump = Join-Path $private 'testhost.dmp'
    $commands = Join-Path $private 'capture.cdb'
    $log = Join-Path $private 'debugger.log'
    $escapedDump = $dump.Replace('\', '\\').Replace('"', '\"')
    $capture = ".echo OPENCLAW_CRASH_BEGIN; .exr -1; .ecxr; kn 40; ~* kn 12; .echo OPENCLAW_CRASH_END; .dump /ma \`"$escapedDump\`"; q"
    $fatalFilters = @(
        'av', 'asrt', 'aph', 'bpe', 'eh', 'clr', 'dm', 'gp', 'ii', 'ip',
        'dz', 'iov', 'ch', 'lsq', 'isc', '3c', 'svh', 'sse', 'sbo', 'sov',
        'vs', 'wkd', 'rto', 'rtt', '*'
    )
    @('sxi ibp') + @($fatalFilters | ForEach-Object { "sxd -c2 `"$capture`" $_" }) + @(
        "sxe -c `"$capture`" 0xc000027b",
        "sxe -c `"$capture`" 0xc0000374",
        "sxe -c `"$capture`" 0xc0000409",
        "sxe -c `"$capture`" 0xc0000602",
        'g'
    ) | Set-Content -LiteralPath $commands -Encoding ascii
    $oldSymbols = $env:_NT_SYMBOL_PATH
    $oldDebugHeap = $env:_NO_DEBUG_HEAP
    try {
        $env:_NT_SYMBOL_PATH = "srv*$(Join-Path $private 'symbols')*https://msdl.microsoft.com/download/symbols"
        $env:_NO_DEBUG_HEAP = '1'
        $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
        # Debug only this runner and its children. Never attach by process name.
        & $debugger -hd -o -G -cf $commands $dotnet @TestArguments *> $log
        $exitCode = $LASTEXITCODE
    } finally {
        $env:_NT_SYMBOL_PATH = $oldSymbols
        $env:_NO_DEBUG_HEAP = $oldDebugHeap
    }

    $summary = ConvertTo-CiNativeCrashSummary -Lines (Get-Content -LiteralPath $log)
    $summary | Add-Member -NotePropertyName debuggerVersion -NotePropertyValue ([Diagnostics.FileVersionInfo]::GetVersionInfo($debugger).FileVersion)
    $summary | Add-Member -NotePropertyName dumpCreated -NotePropertyValue (Test-Path -LiteralPath $dump -PathType Leaf)
    $summary | Add-Member -NotePropertyName runnerExitCode -NotePropertyValue $exitCode
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'native-crash-summary.json') -Encoding utf8
    Write-Host ($summary | ConvertTo-Json -Depth 5)
    Write-Host 'Raw debugger output and process memory were not copied to the artifact directory.'
    if ($summary.crashCaptured -or $summary.dumpCreated) { return 1 }
    return $exitCode
}
