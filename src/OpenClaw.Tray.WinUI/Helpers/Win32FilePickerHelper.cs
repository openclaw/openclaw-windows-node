using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Helpers;

/// <summary>
/// Opens the native Win32 IFileOpenDialog on a dedicated STA thread.
/// UWP FileOpenPicker throws COMException in unpackaged / self-hosted WinUI 3 apps,
/// so we use the COM dialog directly. IFileOpenDialog is an STA COM object and must
/// run on an STA thread — using a dedicated STA thread avoids hangs/failures from
/// shell extensions when called from MTA thread-pool threads.
/// </summary>
internal static class Win32FilePickerHelper
{
    /// <summary>
    /// Shows an "Open" dialog owned by <paramref name="ownerHwnd"/>.
    /// Returns the selected file path, or <c>null</c> if cancelled.
    /// </summary>
    public static Task<string?> PickSingleFileAsync(IntPtr ownerHwnd, string title = "Open")
    {
        var tcs = new TaskCompletionSource<string?>();
        var staThread = new Thread(() =>
        {
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogClass();
                dialog.SetOptions(FOS.FOS_FORCEFILESYSTEM | FOS.FOS_FILEMUSTEXIST);
                dialog.SetTitle(title);
                var hr = dialog.Show(ownerHwnd);
                if (hr < 0)
                {
                    tcs.SetResult(null); // cancelled or error
                    return;
                }
                dialog.GetResult(out var item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var filePath);
                tcs.SetResult(filePath);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        return tcs.Task;
    }

    // ── COM interop ──────────────────────────────────────────────────

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_FORCEFILESYSTEM = 0x40,
        FOS_FILEMUSTEXIST = 0x1000,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }
}
