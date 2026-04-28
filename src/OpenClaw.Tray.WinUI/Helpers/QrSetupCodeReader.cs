using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace OpenClawTray.Helpers;

/// <summary>
/// Decodes an OpenClaw setup code from a QR-encoded image. Shared by the
    /// legacy SetupWizardWindow and the onboarding ConnectionPage.
/// </summary>
public static class QrSetupCodeReader
{
    /// <summary>
    /// Decodes the first QR code found in <paramref name="stream"/> (PNG/JPEG/BMP/GIF)
    /// and returns the encoded text. Returns null when no QR code is found.
    /// </summary>
    public static string? Decode(Stream stream)
    {
        using var source = new DrawingBitmap(stream);
        using var bitmap = new DrawingBitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var pixels = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(System.IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }

            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                    TryInverted = true
                }
            };

            var result = reader.Decode(pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
