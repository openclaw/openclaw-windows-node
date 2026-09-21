using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class ClipboardHelper
{
    public static void CopyText(string text, bool flush = false)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        if (flush)
            Clipboard.Flush();
    }

    public static bool TryCopyText(string text, bool flush = false) =>
        TryCopyText(text, flush, CopyText);

    internal static bool TryCopyText(string text, bool flush, Action<string, bool> write)
    {
        try
        {
            write(text, flush);
            return true;
        }
        catch (COMException ex)
        {
            Logger.Warn($"Clipboard copy failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn($"Clipboard copy failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return false;
        }
    }
}
