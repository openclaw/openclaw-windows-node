#Requires -Version 5.1
# Screenshot-Helpers.ps1 — Window capture via PrintWindow + Node.js PNG encoding

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class ScreenCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO {
        public BITMAPINFOHEADER bmiHeader;
    }
}
"@ -ErrorAction SilentlyContinue

$script:NodeExePath = "C:\Program Files\nodejs\node.exe"

function Capture-WindowScreenshot {
    param(
        [Parameter(Mandatory)][IntPtr]$WindowHandle,
        [Parameter(Mandatory)][string]$OutputPath
    )

    # Get window dimensions
    $rect = New-Object ScreenCapture+RECT
    [ScreenCapture]::GetWindowRect($WindowHandle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    if ($width -le 0 -or $height -le 0) {
        throw "Invalid window dimensions: ${width}x${height}"
    }

    # Create compatible DC and bitmap
    $screenDC = [ScreenCapture]::GetDC([IntPtr]::Zero)
    $memDC = [ScreenCapture]::CreateCompatibleDC($screenDC)
    $bitmap = [ScreenCapture]::CreateCompatibleBitmap($screenDC, $width, $height)
    $oldBitmap = [ScreenCapture]::SelectObject($memDC, $bitmap)

    # Capture with PW_RENDERFULLCONTENT (flag 2)
    $captured = [ScreenCapture]::PrintWindow($WindowHandle, $memDC, 2)
    if (-not $captured) {
        Write-Warning "PrintWindow returned false — capture may be incomplete"
    }

    # Extract BGRA pixel data
    $bmi = New-Object ScreenCapture+BITMAPINFO
    $bmi.bmiHeader.biSize = [uint32][System.Runtime.InteropServices.Marshal]::SizeOf($bmi.bmiHeader)
    $bmi.bmiHeader.biWidth = $width
    $bmi.bmiHeader.biHeight = -$height  # Top-down
    $bmi.bmiHeader.biPlanes = 1
    $bmi.bmiHeader.biBitCount = 32
    $bmi.bmiHeader.biCompression = 0  # BI_RGB

    $pixelDataSize = $width * $height * 4
    $pixelData = New-Object byte[] $pixelDataSize
    [ScreenCapture]::GetDIBits($memDC, $bitmap, 0, [uint32]$height, $pixelData, [ref]$bmi, 0) | Out-Null

    # Cleanup GDI resources
    [ScreenCapture]::SelectObject($memDC, $oldBitmap) | Out-Null
    [ScreenCapture]::DeleteObject($bitmap) | Out-Null
    [ScreenCapture]::DeleteDC($memDC) | Out-Null
    [ScreenCapture]::ReleaseDC([IntPtr]::Zero, $screenDC) | Out-Null

    # Convert BGRA → RGBA in-place
    for ($i = 0; $i -lt $pixelDataSize; $i += 4) {
        $b = $pixelData[$i]
        $pixelData[$i] = $pixelData[$i + 2]      # R
        $pixelData[$i + 2] = $b                    # B
        # G and A stay in place
    }

    # Write raw RGBA to temp file
    $rawFile = [System.IO.Path]::Combine($PSScriptRoot, "..", "screenshots", "capture_raw.bin")
    [System.IO.File]::WriteAllBytes($rawFile, $pixelData)

    # Use Node.js to encode as PNG
    $nodeScript = @"
const fs = require('fs');
const zlib = require('zlib');

const width = $width;
const height = $height;
const rawFile = process.argv[1];
const outFile = process.argv[2];

const rgba = fs.readFileSync(rawFile);

// Build raw scanlines: filter byte (0) + RGBA row data
const rowSize = width * 4;
const filtered = Buffer.alloc(height * (1 + rowSize));
for (let y = 0; y < height; y++) {
    filtered[y * (1 + rowSize)] = 0; // filter: None
    rgba.copy(filtered, y * (1 + rowSize) + 1, y * rowSize, (y + 1) * rowSize);
}

const compressed = zlib.deflateSync(filtered);

function crc32(buf) {
    let crc = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) {
        crc ^= buf[i];
        for (let j = 0; j < 8; j++) {
            crc = (crc >>> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
        }
    }
    return (crc ^ 0xFFFFFFFF) >>> 0;
}

function makeChunk(type, data) {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const typeAndData = Buffer.concat([Buffer.from(type), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typeAndData));
    return Buffer.concat([len, typeAndData, crc]);
}

// IHDR
const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(width, 0);
ihdr.writeUInt32BE(height, 4);
ihdr[8] = 8;  // bit depth
ihdr[9] = 6;  // color type: RGBA
ihdr[10] = 0; // compression
ihdr[11] = 0; // filter
ihdr[12] = 0; // interlace

const png = Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), // PNG signature
    makeChunk('IHDR', ihdr),
    makeChunk('IDAT', compressed),
    makeChunk('IEND', Buffer.alloc(0))
]);

fs.writeFileSync(outFile, png);
fs.unlinkSync(rawFile); // cleanup raw
console.log('PNG written: ' + outFile + ' (' + png.length + ' bytes)');
"@

    $nodeScriptFile = [System.IO.Path]::Combine($PSScriptRoot, "..", "screenshots", "png_encode.js")
    [System.IO.File]::WriteAllText($nodeScriptFile, $nodeScript)

    $outputDir = [System.IO.Path]::GetDirectoryName($OutputPath)
    if (-not (Test-Path $outputDir)) {
        New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
    }

    $result = & $script:NodeExePath $nodeScriptFile $rawFile $OutputPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Node.js PNG encoding failed: $result"
    }

    # Cleanup temp node script
    if (Test-Path $nodeScriptFile) { Remove-Item $nodeScriptFile -Force }

    Write-Host "  📸 Screenshot saved: $OutputPath" -ForegroundColor DarkGray
}
