namespace OpenClawTray.Chat;

internal static class ChatAssistantImageDecodePolicy
{
    internal const uint MaximumSourceDimension = 16_384;
    internal const ulong MaximumDecodedPixels = 32UL * 1024 * 1024;
    internal const uint MaximumDecodeDimension = 2_048;

    public static bool TryGetDecodeSize(
        uint sourceWidth,
        uint sourceHeight,
        out int decodeWidth,
        out int decodeHeight)
    {
        decodeWidth = 0;
        decodeHeight = 0;
        if (sourceWidth == 0
            || sourceHeight == 0
            || sourceWidth > MaximumSourceDimension
            || sourceHeight > MaximumSourceDimension
            || (ulong)sourceWidth * sourceHeight > MaximumDecodedPixels)
        {
            return false;
        }

        var scale = Math.Min(
            1d,
            MaximumDecodeDimension / (double)Math.Max(sourceWidth, sourceHeight));
        decodeWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        decodeHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return true;
    }
}
