using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClaw.SetupEngine.UI.Controls;

internal sealed record DecodedProviderArtwork(ProviderArtworkStatus Status, ImageSource? Source = null);

internal static class ProviderArtworkDecoder
{
    private static readonly SemaphoreSlim Slots = new(2);

    internal static async Task<DecodedProviderArtwork> DecodeAsync(ProviderArtworkData data, CancellationToken ct)
    {
        if (!await Slots.WaitAsync(0, ct))
            return new(ProviderArtworkStatus.Busy);
        // The core retains its semaphore and stream until the native operation actually ends,
        // even if the caller times out or unloads. Abandoned native work cannot free a slot
        // for unbounded concurrent decoders. No task continuation retains the control/page.
        var decode = DecodeOwnedAsync(data);
        try { return await decode.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            _ = decode.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<DecodedProviderArtwork> DecodeOwnedAsync(ProviderArtworkData data)
    {
        try
        {
            if (data.Bytes.Length > ProviderArtworkLoader.MaxBytes)
                return new(ProviderArtworkStatus.TooLarge);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.Bytes.AsBuffer());
            stream.Seek(0);
            if (data.Format == ProviderArtworkFormat.Svg)
            {
                var svg = new SvgImageSource { RasterizePixelWidth = 24, RasterizePixelHeight = 24 };
                var status = await svg.SetSourceAsync(stream);
                return status == SvgImageSourceLoadStatus.Success
                    ? new(ProviderArtworkStatus.Loaded, svg) : new(ProviderArtworkStatus.InvalidImage);
            }
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var expected = data.Format == ProviderArtworkFormat.Png ? BitmapDecoder.PngDecoderId : BitmapDecoder.JpegDecoderId;
            if (decoder.DecoderInformation.CodecId != expected || decoder.FrameCount != 1 ||
                !ProviderArtworkContent.AreDimensionsAllowed(decoder.PixelWidth, decoder.PixelHeight))
                return new(ProviderArtworkStatus.InvalidImage);
            var scale = Math.Min(24d / decoder.PixelWidth, 24d / decoder.PixelHeight);
            var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var bytes = pixels.DetachPixelData();
            if (bytes.Length != (long)width * height * 4)
                return new(ProviderArtworkStatus.InvalidImage);
            var image = new WriteableBitmap((int)width, (int)height);
            using var target = image.PixelBuffer.AsStream();
            target.Write(bytes);
            image.Invalidate();
            return new(ProviderArtworkStatus.Loaded, image);
        }
        catch (COMException) { return new(ProviderArtworkStatus.InvalidImage); }
        catch (ArgumentException) { return new(ProviderArtworkStatus.InvalidImage); }
        catch (IOException) { return new(ProviderArtworkStatus.InvalidImage); }
        finally { Slots.Release(); }
    }
}
