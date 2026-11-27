<#
.SYNOPSIS
Exports the Gateway's built-in theme background artwork into the Companion's assets.

.DESCRIPTION
The Control UI pairs each built-in theme with light and dark background artwork
(ui/src/assets/themes/<family>-<mode>.webp, bound in ui/src/styles/theme-backgrounds.css);
imported and plugin palettes use the neutral "custom" pair. This script fetches the lossless WebP
exports at -UpstreamRef and re-encodes them as PNG with straight alpha into
src/OpenClaw.Tray.WinUI/Assets/ThemeBackgrounds, because WinUI decodes WebP only where the optional
WebP codec is installed. The SVG sources are not used: they rely on SVG masks that WinUI's SVG
renderer does not draw, and upstream ships the raster exports for stable compositing.

It needs gh (authenticated for github.com) and the Windows WebP decoder (WebP Image Extensions).

.EXAMPLE
.\scripts\Update-ThemeBackgrounds.ps1 -UpstreamRef 4ffdd2047d192dd65554de3c8c46c0e0197c6e2a
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$UpstreamRef,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\OpenClaw.Tray.WinUI\Assets\ThemeBackgrounds')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

# Keep in sync with ThemeBackgrounds.ArtworkFamily in src/OpenClaw.Shared/Themes.
$families = @('claw', 'knot', 'dash', 'absolutely', 'tide', 'beacon', 'phosphor', 'crt', 'manuscript', 'rose', 'miami', 'custom')

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
foreach ($family in $families) {
    foreach ($mode in @('dark', 'light')) {
        $name = "$family-$mode"
        $content = gh api "repos/openclaw/openclaw/contents/ui/src/assets/themes/$name.webp?ref=$UpstreamRef" --jq .content
        if ($LASTEXITCODE -ne 0 -or -not $content) { throw "Could not fetch $name.webp at $UpstreamRef." }
        $stream = [System.IO.MemoryStream]::new([Convert]::FromBase64String(($content -join '')))
        try {
            $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream,
                [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        } catch {
            throw "Decoding $name.webp failed; install the WebP Image Extensions. $($_.Exception.Message)"
        } finally {
            $stream.Dispose()
        }
        # Bgra32 is straight alpha, which PNG stores; the artwork is faint line art with very low alpha.
        $pixels = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
            $decoder.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($pixels))
        $file = [System.IO.File]::Create((Join-Path $resolvedOutput "$name.png"))
        try {
            $encoder.Save($file)
        } finally {
            $file.Dispose()
        }
    }
}
$total = (Get-ChildItem -LiteralPath $resolvedOutput -Filter *.png | Measure-Object Length -Sum).Sum
Write-Host ("Wrote {0} artwork files ({1:N0} bytes) from openclaw/openclaw@{2} to {3}" -f ($families.Count * 2), $total, $UpstreamRef, $resolvedOutput)
