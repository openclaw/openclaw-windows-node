<#
.SYNOPSIS
Downloads the OFL typefaces used by the Gateway's built-in themes into the Companion's assets.

.DESCRIPTION
The Control UI's built-in themes name nine OFL families (ui/public/themes/*.css and base.css).
This script fetches the roman TTF build of each family from google/fonts at -GoogleFontsRef into
src/OpenClaw.Tray.WinUI/Assets/Fonts with bracket-free names, verifies each file is a TrueType font,
and prints each family's copyright line for THIRD_PARTY_NOTICES.md. It needs an authenticated gh.
Italic files are not bundled: a XAML FontFamily source loads one file, so italics are synthesized.

.EXAMPLE
.\scripts\Update-ThemeFonts.ps1 -GoogleFontsRef 0fb1bdc740dd9d495e639a160778db34c28807b7
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$GoogleFontsRef,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\OpenClaw.Tray.WinUI\Assets\Fonts')
)

$ErrorActionPreference = 'Stop'

# Keep in sync with ThemeFontMapper.BundledFamilies in src/OpenClaw.Tray.WinUI/Theming.
$families = [ordered]@{
    'InstrumentSans'           = 'instrumentsans/InstrumentSans[wdth,wght].ttf'
    'Geist'                    = 'geist/Geist[wght].ttf'
    'DMSans'                   = 'dmsans/DMSans[opsz,wght].ttf'
    'IBMPlexSans'              = 'ibmplexsans/IBMPlexSans[wdth,wght].ttf'
    'SpaceGrotesk'             = 'spacegrotesk/SpaceGrotesk[wght].ttf'
    'AtkinsonHyperlegibleNext' = 'atkinsonhyperlegiblenext/AtkinsonHyperlegibleNext[wght].ttf'
    'Fraunces'                 = 'fraunces/Fraunces[SOFT,WONK,opsz,wght].ttf'
    'Lora'                     = 'lora/Lora[wght].ttf'
    'JetBrainsMono'            = 'jetbrainsmono/JetBrainsMono[wght].ttf'
}

function Save-UpstreamFile([string]$path, [string]$destination) {
    $raw = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
    try {
        $escaped = ($path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/google/fonts/$GoogleFontsRef/$escaped" -OutFile $raw -UseBasicParsing
        $bytes = [System.IO.File]::ReadAllBytes($raw)
        if ($bytes.Length -lt 4 -or -not ($bytes[0] -eq 0 -and $bytes[1] -eq 1 -and $bytes[2] -eq 0 -and $bytes[3] -eq 0)) {
            throw "$path is not a TrueType font."
        }
        Move-Item -LiteralPath $raw -Destination $destination -Force
    } finally {
        Remove-Item -LiteralPath $raw -ErrorAction SilentlyContinue
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
foreach ($entry in $families.GetEnumerator()) {
    Save-UpstreamFile "ofl/$($entry.Value)" (Join-Path $resolvedOutput "$($entry.Key).ttf")
    $directory = ($entry.Value -split '/')[0]
    $license = gh api -H 'Accept: application/vnd.github.raw' "repos/google/fonts/contents/ofl/$directory/OFL.txt?ref=$GoogleFontsRef"
    $copyright = @($license | Where-Object { $_ -match '^Copyright (\d|©)' })
    Write-Host "$($entry.Key): $($copyright -join ' ')"
}
$total = (Get-ChildItem -LiteralPath $resolvedOutput -Filter *.ttf | Measure-Object Length -Sum).Sum
Write-Host ("Wrote {0:N0} bytes of fonts from google/fonts@{1} to {2}" -f $total, $GoogleFontsRef, $resolvedOutput)
