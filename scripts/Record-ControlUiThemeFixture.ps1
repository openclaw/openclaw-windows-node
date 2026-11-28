<#
.SYNOPSIS
Records a running Gateway's Control UI theme stylesheets and its dashboard's own resolved theme tokens as a test fixture.

.DESCRIPTION
Runtime theme resolution reads the stylesheets a Gateway serves and resolves them with
src/OpenClaw.Shared/Themes/ThemeProbe.js. Its tests compare that result with what the Gateway's real dashboard
computes. This script records both from a live Gateway:

- the files the Companion's reader fetches (index.html, the stylesheets it links under assets/, and
  themes/<id>.css for each built-in), exactly as served;
- dashboard.json: for each built-in and mode, the dashboard page (loaded in headless Microsoft Edge, with the theme
  chosen through its own saved settings) is probed in place with ThemeProbe.js, and its painted page background is
  read too.

It writes one zip under tests/OpenClaw.TestSupport/Themes/ControlUiRecordings. It needs Microsoft Edge and network access to
the Gateway only; it sends no credentials, because the Gateway serves the Control UI without request authentication.

.EXAMPLE
.\scripts\Record-ControlUiThemeFixture.ps1 -GatewayUrl http://172.27.102.110:19084/ -Name gateway-2026.11.4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [uri]$GatewayUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9._-]*$')]
    [string]$Name,

    [string[]]$ThemeIds = @('claw', 'knot', 'dash', 'absolutely', 'tide', 'beacon', 'phosphor', 'crt', 'manuscript', 'rose', 'miami'),

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\tests\OpenClaw.TestSupport\Themes\ControlUiRecordings')
)

$ErrorActionPreference = 'Stop'
$root = if ($GatewayUrl.AbsoluteUri.EndsWith('/')) { $GatewayUrl } else { [uri]($GatewayUrl.AbsoluteUri + '/') }
$probeScript = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\OpenClaw.Shared\Themes\ThemeProbe.js'))

function Find-Edge {
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if (-not $base) { continue }
        $candidate = Join-Path $base 'Microsoft\Edge\Application\msedge.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Microsoft Edge was not found.'
}

function Get-Served([string]$path, [string]$mediaType) {
    $response = Invoke-WebRequest -Uri ([uri]::new($root, $path)) -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 60
    if ($response.StatusCode -ne 200 -or -not $response.Headers['Content-Type'].StartsWith($mediaType)) {
        throw "$path is not served as $mediaType."
    }
    return $response.Content
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("openclaw-controlui-fixture-" + [guid]::NewGuid().ToString('N'))
$files = Join-Path $work 'files'
New-Item -ItemType Directory -Path $files | Out-Null
$edgeProcess = $null
try {
    # The files the Companion's reader fetches, as served.
    $index = Get-Served '' 'text/html'
    [System.IO.File]::WriteAllText((Join-Path $files 'index.html'), $index, [System.Text.UTF8Encoding]::new($false))
    $links = [regex]::Matches($index, '<link\b[^>]*>') | ForEach-Object { $_.Value } | Where-Object {
        $_ -match 'rel="?stylesheet' -or ($_ -match 'rel="?preload' -and $_ -match 'as="?style')
    } | ForEach-Object { [regex]::Match($_, 'href="([^"]+)"').Groups[1].Value } |
        Where-Object { $_ -match '^(?:\./|(?:/[A-Za-z0-9._~-]+)*/)?assets/[A-Za-z0-9._-]+\.css$' } | Select-Object -Unique
    foreach ($href in $links) {
        $relative = $href -replace '^.*?(assets/[^/]+)$', '$1'
        $target = Join-Path $files $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        [System.IO.File]::WriteAllText($target, (Get-Served $href 'text/css'), [System.Text.UTF8Encoding]::new($false))
    }
    New-Item -ItemType Directory -Path (Join-Path $files 'themes') | Out-Null
    foreach ($id in $ThemeIds | Where-Object { $_ -ne 'claw' }) {
        [System.IO.File]::WriteAllText((Join-Path $files "themes\$id.css"), (Get-Served "themes/$id.css" 'text/css'),
            [System.Text.UTF8Encoding]::new($false))
    }

    # The dashboard's own values, read in place through the Chrome DevTools Protocol.
    $edgeProfile = Join-Path $work 'profile'
    $edgeProcess = Start-Process -FilePath (Find-Edge) -PassThru -WindowStyle Hidden -ArgumentList @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-sync',
        '--disable-extensions', '--remote-debugging-port=0', "--user-data-dir=`"$edgeProfile`"", 'about:blank')
    $portFile = Join-Path $edgeProfile 'DevToolsActivePort'
    for ($i = 0; $i -lt 100 -and -not (Test-Path $portFile); $i++) { Start-Sleep -Milliseconds 200 }
    $port = (Get-Content $portFile -TotalCount 1).Trim()
    $page = (Invoke-RestMethod "http://127.0.0.1:$port/json/list") | Where-Object type -eq 'page' | Select-Object -First 1
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.ConnectAsync([uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:messageId = 0

    function Invoke-Cdp([string]$method, [hashtable]$params = @{}) {
        $id = ++$script:messageId
        $payload = [System.Text.Encoding]::UTF8.GetBytes((@{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 8 -Compress))
        $socket.SendAsync([ArraySegment[byte]]::new($payload), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $buffer = [byte[]]::new(1MB)
        while ($true) {
            $text = [System.Text.StringBuilder]::new()
            do {
                $result = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
                [void]$text.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
            } while (-not $result.EndOfMessage)
            $message = $text.ToString() | ConvertFrom-Json
            if ($message.id -eq $id) {
                if ($message.error) { throw "$method failed: $($message.error.message)" }
                return $message.result
            }
        }
    }

    function Invoke-PageScript([string]$expression) {
        $result = Invoke-Cdp 'Runtime.evaluate' @{ expression = $expression; awaitPromise = $true; returnByValue = $true }
        if ($result.exceptionDetails) { throw "Page script failed: $($result.exceptionDetails.text)" }
        return $result.result.value
    }

    function Open-Dashboard {
        [void](Invoke-Cdp 'Page.navigate' @{ url = $root.AbsoluteUri })
        for ($i = 0; $i -lt 150; $i++) {
            Start-Sleep -Milliseconds 200
            if ((Invoke-PageScript 'document.readyState') -eq 'complete') { return }
        }
        throw 'The dashboard did not load.'
    }

    Open-Dashboard
    $recorded = [ordered]@{}
    foreach ($id in $ThemeIds) {
        $recorded[$id] = [ordered]@{ light = $null; dark = $null; painted = [ordered]@{} }
        foreach ($mode in @('light', 'dark')) {
            # The dashboard keeps its theme in saved settings, read before first paint; set every settings key it uses.
            $settings = @{ theme = $id; themeMode = $mode } | ConvertTo-Json -Compress
            [void](Invoke-PageScript @"
(() => {
  const patch = $settings;
  const keys = Object.keys(localStorage).filter(k => k.startsWith('openclaw.control.settings.v1'));
  if (keys.length === 0) keys.push('openclaw.control.settings.v1');
  for (const k of keys) localStorage.setItem(k, JSON.stringify(Object.assign(JSON.parse(localStorage.getItem(k) || '{}'), patch)));
  return keys.length;
})()
"@)
            Open-Dashboard
            $sheet = "openclaw-theme-palette-$id"
            for ($i = 0; $i -lt 50; $i++) {
                if (Invoke-PageScript "(() => { const l = document.getElementById('$sheet'); return !l || !!l.sheet; })()") { break }
                Start-Sleep -Milliseconds 200
            }
            Start-Sleep -Milliseconds 750
            [void](Invoke-PageScript $probeScript)
            $json = Invoke-PageScript @"
(() => {
  const root = document.documentElement;
  const painted = openclawThemeProbe.toHex(getComputedStyle(root).backgroundColor);
  const active = root.getAttribute('data-theme');
  const probed = openclawThemeProbe.resolve(['$id']);
  return JSON.stringify({ painted, active, palette: probed.result['$id']['$mode'], failures: probed.failures });
})()
"@
            $value = $json | ConvertFrom-Json
            $expectedAlias = (Invoke-PageScript "openclawThemeProbe.dataTheme('$id', '$mode')")
            if ($value.active -ne $expectedAlias) { throw "The dashboard rendered data-theme=$($value.active) for $id/$mode." }
            if (@($value.failures).Count -gt 0) { throw "Unresolved tokens in the dashboard for ${id}: $($value.failures -join '; ')" }
            $recorded[$id][$mode] = $value.palette
            $recorded[$id].painted[$mode] = $value.painted
            Write-Host "$id/$mode painted $($value.painted)"
        }
    }
    $buildId = [regex]::Match($index, 'data-openclaw-control-ui-build-id="([^"]+)"').Groups[1].Value
    $dashboard = [ordered]@{
        recordedFrom = [ordered]@{ buildId = $(if ($buildId) { $buildId } else { $null }); recordedAt = (Get-Date).ToUniversalTime().ToString('o') }
        # ThemeProbe.ScriptHash of the probe that read the dashboard.
        probeSha256 = -join ([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($probeScript)) | ForEach-Object { $_.ToString('x2') })
        themes = $recorded
    }
    [System.IO.File]::WriteAllText((Join-Path $files 'dashboard.json'), ($dashboard | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))

    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $zip = Join-Path $output "$Name.zip"
    Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $files '*') -DestinationPath $zip
    Write-Host "Wrote $zip ($((Get-Item $zip).Length) bytes)"
} finally {
    if ($edgeProcess -and -not $edgeProcess.HasExited) { Stop-Process -Id $edgeProcess.Id -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
