<#
.SYNOPSIS
Regenerates the Companion's built-in Gateway theme table from a pinned openclaw/openclaw ref.

.DESCRIPTION
The Gateway's themes.list returns built-in themes as descriptors only; their colors are a CSS
cascade in the Control UI (ui/src/styles/base.css plus ui/public/themes/<id>.css, with
color-mix() derivations). This script fetches those stylesheets at -UpstreamRef, resolves every
token the Companion uses (colors plus the --font-body, --font-chat and --mono stacks) in headless
Microsoft Edge for each theme and mode, and writes src/OpenClaw.Shared/Themes/BuiltinGatewayThemes.g.cs.
Each theme's mascot, brand icon and working indicator come from BUILTIN_THEMES, resolved with upstream's
resolveThemeBranding defaults. The script fails when any token does not resolve or a branding value is unknown.

It needs gh (authenticated for github.com) and Microsoft Edge. It writes only the output file and a
temporary directory it deletes afterwards.

.EXAMPLE
.\scripts\Update-GatewayThemeTable.ps1 -UpstreamRef 220ca12e0732ab599c5a9b6434ac699f6750728f
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$UpstreamRef,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\OpenClaw.Shared\Themes\BuiltinGatewayThemes.g.cs')
)

$ErrorActionPreference = 'Stop'
$repo = 'openclaw/openclaw'

function Get-UpstreamText([string]$path) {
    $text = gh api -H 'Accept: application/vnd.github.raw' "repos/$repo/contents/$path`?ref=$UpstreamRef"
    if ($LASTEXITCODE -ne 0) { throw "Failed to fetch $path at $UpstreamRef." }
    return ($text -join "`n")
}

function Find-Edge {
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Microsoft\Edge\Application\msedge.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Microsoft Edge was not found.'
}

function ConvertTo-CSharpString([string]$value) {
    return '"' + ($value -replace '\\', '\\' -replace '"', '\"') + '"'
}

# Theme descriptors and their order come from the protocol's BUILTIN_THEMES.
$themeSource = Get-UpstreamText 'packages/gateway-protocol/src/theme.ts'
$descriptorPattern = 'id:\s*"(?<id>[a-z0-9-]+)",\s*name:\s*"(?<name>[^"]+)",(?<branding>[\s\S]*?)description:\s*"(?<description>(?:[^"\\]|\\.)*)"'

# Branding resolved with upstream resolveThemeBranding defaults; an unknown value fails the run so a new
# upstream option is handled deliberately rather than shipped as a fallback.
function Resolve-Branding([string]$id, [string]$fields) {
    # $fields is the whole entry, so a key after description is still seen; a non-literal value fails.
    function Get-Field([string]$name) {
        $key = [regex]::Match($fields, "(?m)^\s*$name\s*:\s*(?<value>[^\r\n]*?)\s*,?\s*$")
        if (-not $key.Success) { return $null }
        $literal = [regex]::Match($key.Groups['value'].Value, '\A"(?<value>[^"]*)"\z')
        if (-not $literal.Success) { throw "Built-in theme $id sets $name to a non-literal value: $($key.Groups['value'].Value)" }
        return $literal.Groups['value'].Value
    }
    $mascot = Get-Field 'mascot'
    if (-not $mascot) { $mascot = 'claw' }
    $brandIcon = Get-Field 'brandIcon'
    if (-not $brandIcon) { $brandIcon = if ($mascot -eq 'claw') { 'claw' } else { 'mark' } }
    $working = Get-Field 'workingIndicator'
    if (-not $working) { $working = if ($mascot -eq 'claw') { 'claw' } else { 'dots' } }
    if ($mascot -notin 'claw', 'none') { throw "Built-in theme $id has an unknown mascot '$mascot'." }
    if ($brandIcon -notin 'claw', 'mark') { throw "Built-in theme $id has an unknown brand icon '$brandIcon'." }
    if ($working -notin 'claw', 'dots', 'brand', 'none') { throw "Built-in theme $id has an unknown working indicator '$working'." }
    return [pscustomobject]@{ Mascot = $mascot; BrandIcon = $brandIcon; WorkingIndicator = $working }
}

$descriptors = @([regex]::Matches($themeSource, $descriptorPattern))
$themes = @(for ($index = 0; $index -lt $descriptors.Count; $index++) {
    $descriptor = $descriptors[$index]
    # The entry runs to the next descriptor, or to the closing brace after the last description.
    $end = if ($index + 1 -lt $descriptors.Count) { $descriptors[$index + 1].Index } else {
        $themeSource.IndexOf('}', $descriptor.Index + $descriptor.Length)
    }
    [pscustomobject]@{
        Id = $descriptor.Groups['id'].Value; Name = $descriptor.Groups['name'].Value
        Description = $descriptor.Groups['description'].Value
        Branding = Resolve-Branding $descriptor.Groups['id'].Value $themeSource.Substring($descriptor.Index, $end - $descriptor.Index)
    }
})
if ($themes.Count -lt 1) { throw 'No BUILTIN_THEMES entries were found.' }

$idSource = Get-UpstreamText 'packages/gateway-protocol/src/theme-ids.ts'
$idBlock = [regex]::Match($idSource, 'BUILTIN_THEME_IDS\s*=\s*\[(?<ids>[^\]]*)\]').Groups['ids'].Value
$builtinIds = @([regex]::Matches($idBlock, '"([a-z0-9-]+)"') | ForEach-Object { $_.Groups[1].Value })
$descriptorIds = @($themes | ForEach-Object Id)
if (($builtinIds -join ',') -ne ($descriptorIds -join ',')) {
    throw "BUILTIN_THEME_IDS ($($builtinIds -join ',')) and BUILTIN_THEMES ($($descriptorIds -join ',')) disagree."
}

# Theme ids map to data-theme aliases exactly as ui/src/app/theme.ts resolveTheme() does.
$css = [System.Text.StringBuilder]::new()
[void]$css.AppendLine((Get-UpstreamText 'ui/src/styles/base.css'))
foreach ($theme in $themes) {
    if ($theme.Id -eq 'claw') { continue }
    [void]$css.AppendLine((Get-UpstreamText "ui/public/themes/$($theme.Id).css"))
}

$tokens = [ordered]@{
    Canvas = 'bg'; Content = 'bg-content'; Surface = 'bg-accent'; Card = 'card'; Popover = 'popover'
    Hover = 'bg-hover'; Text = 'text'; TextStrong = 'text-strong'; Muted = 'muted'; Border = 'border'
    BorderStrong = 'border-strong'; Accent = 'accent'; AccentHover = 'accent-hover'
    AccentForeground = 'accent-foreground'; Primary = 'primary'; PrimaryForeground = 'primary-foreground'
    Ring = 'ring'; Destructive = 'destructive'; Danger = 'danger'; Ok = 'ok'; Warn = 'warn'; Info = 'info'
    ComposerSurface = 'chat-composer-surface'; InlineCode = 'inline-code-bg'
}
# Consumers read these through var() fallbacks (ui/src/styles/chat/text.css). Releases older than the root
# definitions of bg-content and chat-composer-surface read var(--bg-content, var(--bg)) and give the composer
# var(--popover) in chat/composer-surface.css, so the packaged Gateway's themes resolve the same way.
$fallbacks = @{ 'inline-code-bg' = 'bg-muted'; 'bg-content' = 'bg'; 'chat-composer-surface' = 'popover' }
$probeThemes = ($themes | ForEach-Object { "'$($_.Id)'" }) -join ','
$probeTokens = ($tokens.GetEnumerator() | ForEach-Object {
    $fallback = if ($fallbacks.ContainsKey($_.Value)) { "'$($fallbacks[$_.Value])'" } else { 'null' }
    "['$($_.Key)','$($_.Value)',$fallback]"
}) -join ','
$cssText = $css.ToString() -replace '</style', '<\/style'

$html = @"
<!doctype html>
<html><head><meta charset="utf-8"><style>
$cssText
</style></head><body><span id="probe"></span><pre id="out"></pre><script>
const themes = [$probeThemes];
const tokens = [$probeTokens];
const root = document.documentElement;
const probe = document.getElementById('probe');
const hex = (n) => Math.round(Math.min(1, Math.max(0, n)) * 255).toString(16).padStart(2, '0');
function toHex(value) {
  let m = value.match(/^rgba?\(\s*([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\s*\)$/);
  let r, g, b, a;
  if (m) { r = +m[1] / 255; g = +m[2] / 255; b = +m[3] / 255; a = m[4] === undefined ? 1 : +m[4]; }
  else {
    m = value.match(/^color\(srgb\s+([-\d.e]+)\s+([-\d.e]+)\s+([-\d.e]+)(?:\s*\/\s*([\d.e]+))?\)$/);
    if (!m) return null;
    r = +m[1]; g = +m[2]; b = +m[3]; a = m[4] === undefined ? 1 : +m[4];
  }
  return '#' + hex(r) + hex(g) + hex(b) + (a >= 1 ? '' : hex(a));
}
const result = {};
const failures = [];
for (const id of themes) {
  result[id] = {};
  for (const mode of ['light', 'dark']) {
    const family = id === 'claw' ? null : id === 'knot' ? 'openknot' : id;
    root.setAttribute('data-theme', family === null ? mode : mode === 'light' ? family + '-light' : family);
    root.setAttribute('data-theme-mode', mode);
    const palette = {};
    for (const [name, primary, fallback] of tokens) {
      const style = getComputedStyle(root);
      const variable = style.getPropertyValue('--' + primary).trim() || !fallback ? primary : fallback;
      const raw = style.getPropertyValue('--' + variable).trim();
      probe.style.color = '';
      probe.style.color = 'rgb(from var(--' + variable + ') r g b / alpha)';
      const value = raw ? toHex(getComputedStyle(probe).color) : null;
      if (!value) failures.push(id + '/' + mode + '/' + variable + '=' + raw);
      palette[name] = value;
    }
    for (const [name, variable] of [['Body', 'font-body'], ['Chat', 'font-chat'], ['Mono', 'mono']]) {
      palette[name] = getComputedStyle(root).getPropertyValue('--' + variable).trim().replace(/\s+/g, ' ');
      if (!palette[name]) failures.push(id + '/' + mode + '/' + variable);
    }
    result[id][mode] = palette;
  }
}
document.getElementById('out').textContent = JSON.stringify({ result, failures });
</script></body></html>
"@

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("openclaw-theme-table-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $page = Join-Path $work 'probe.html'
    [System.IO.File]::WriteAllText($page, $html, [System.Text.UTF8Encoding]::new($false))
    $edge = Find-Edge
    $profile = Join-Path $work 'profile'
    $stdout = Join-Path $work 'dom.html'
    # msedge.exe is a GUI-subsystem binary: --dump-dom output reaches only a redirected handle.
    $edgeArgs = @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
        '--disable-sync', '--disable-extensions', '--disable-background-networking',
        "--user-data-dir=`"$profile`"", '--virtual-time-budget=5000', '--dump-dom', ([uri]$page).AbsoluteUri)
    $process = Start-Process -FilePath $edge -ArgumentList $edgeArgs -RedirectStandardOutput $stdout `
        -RedirectStandardError (Join-Path $work 'edge.log') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Edge exited with $($process.ExitCode)." }
    $dom = [System.IO.File]::ReadAllText($stdout)
    $payload = [regex]::Match($dom, '<pre id="out">(?<json>[\s\S]*?)</pre>').Groups['json'].Value
    if (-not $payload) { throw 'Edge produced no probe output.' }
    $parsed = [System.Net.WebUtility]::HtmlDecode($payload) | ConvertFrom-Json
    if ($parsed.failures.Count -gt 0) { throw "Unresolved theme tokens: $($parsed.failures -join '; ')" }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$out = [System.Text.StringBuilder]::new()
[void]$out.AppendLine('// <auto-generated>')
[void]$out.AppendLine("// Generated by scripts/Update-GatewayThemeTable.ps1 from openclaw/openclaw@$UpstreamRef.")
[void]$out.AppendLine('// Do not edit by hand; rerun the script with a new -UpstreamRef instead.')
[void]$out.AppendLine('// </auto-generated>')
[void]$out.AppendLine()
[void]$out.AppendLine('namespace OpenClaw.Shared.Themes;')
[void]$out.AppendLine()
[void]$out.AppendLine('public static partial class BuiltinGatewayThemes')
[void]$out.AppendLine('{')
[void]$out.AppendLine("    /// <summary>The openclaw/openclaw commit these palettes were resolved from.</summary>")
[void]$out.AppendLine("    public const string UpstreamRef = `"$UpstreamRef`";")
[void]$out.AppendLine()
[void]$out.AppendLine('    private static readonly BuiltinTheme[] Data =')
[void]$out.AppendLine('    [')
foreach ($theme in $themes) {
    [void]$out.AppendLine("        new($(ConvertTo-CSharpString $theme.Id), $(ConvertTo-CSharpString $theme.Name),")
    [void]$out.AppendLine("            $(ConvertTo-CSharpString $theme.Description),")
    $branding = $theme.Branding
    [void]$out.AppendLine("            Mascot: $(ConvertTo-CSharpString $branding.Mascot), BrandIcon: $(ConvertTo-CSharpString $branding.BrandIcon), WorkingIndicator: $(ConvertTo-CSharpString $branding.WorkingIndicator),")
    foreach ($mode in @('light', 'dark')) {
        $palette = $parsed.result.($theme.Id).$mode
        $label = if ($mode -eq 'light') { 'Light' } else { 'Dark' }
        $values = @($tokens.Keys | ForEach-Object { "$($_): $(ConvertTo-CSharpString $palette.$_)" })
        $values += @('Body', 'Chat', 'Mono' | ForEach-Object { "$($_): $(ConvertTo-CSharpString $palette.$_)" })
        $terminator = if ($mode -eq 'light') { ',' } else { '),' }
        [void]$out.AppendLine("            $($label): new(")
        [void]$out.AppendLine("                " + ($values -join ",`n                ") + ")$terminator")
    }
}
[void]$out.AppendLine('    ];')
[void]$out.AppendLine('}')

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.File]::WriteAllText($resolvedOutput, $out.ToString().Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($themes.Count) built-in themes to $resolvedOutput"
