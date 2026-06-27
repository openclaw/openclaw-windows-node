using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Helpers;

/// <summary>
/// Opens native Win32 file dialogs on a dedicated STA thread.
/// UWP FileOpenPicker/FileSavePicker are unreliable in unpackaged / self-hosted WinUI 3 apps,
/// so we use the COM dialogs directly. These STA COM objects must
/// run on an STA thread — using a dedicated STA thread avoids hangs/failures from
/// shell extensions when called from MTA thread-pool threads.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32FilePickerHelper
{
    /// <summary>
    /// Shows an "Open" dialog owned by <paramref name="ownerHwnd"/>.
    /// Returns the selected file path, or <c>null</c> if cancelled.
    /// </summary>
    public static async Task<string?> PickSingleFileAsync(IntPtr ownerHwnd, string title = "Open")
    {
        var paths = await PickFilesAsync(ownerHwnd, title, allowMultiple: false);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Shows an "Open" dialog owned by <paramref name="ownerHwnd"/>.
    /// Returns all selected file paths, or an empty list if cancelled.
    /// </summary>
    public static Task<IReadOnlyList<string>> PickMultipleFilesAsync(IntPtr ownerHwnd, string title = "Open")
        => PickFilesAsync(ownerHwnd, title, allowMultiple: true);

    private static Task<IReadOnlyList<string>> PickFilesAsync(IntPtr ownerHwnd, string title, bool allowMultiple)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() =>
        {
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogClass();
                var options = FOS.FOS_FORCEFILESYSTEM | FOS.FOS_FILEMUSTEXIST;
                if (allowMultiple)
                    options |= FOS.FOS_ALLOWMULTISELECT;
                dialog.SetOptions(options);
                dialog.SetTitle(title);
                var hr = dialog.Show(ownerHwnd);
                if (hr < 0)
                {
                    tcs.SetResult(Array.Empty<string>()); // cancelled or error
                    return;
                }

                if (allowMultiple)
                {
                    dialog.GetResults(out var items);
                    if (items is null)
                    {
                        dialog.GetResult(out var fallbackItem);
                        if (fallbackItem is null)
                        {
                            tcs.SetResult(Array.Empty<string>());
                            return;
                        }

                        fallbackItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var fallbackFilePath);
                        tcs.SetResult(new[] { fallbackFilePath });
                        return;
                    }

                    items.GetCount(out var count);
                    var paths = new List<string>((int)count);
                    for (uint i = 0; i < count; i++)
                    {
                        items.GetItemAt(i, out var multiItem);
                        if (multiItem is null)
                            continue;

                        multiItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var multiFilePath);
                        paths.Add(multiFilePath);
                    }
                    tcs.SetResult(paths);
                    return;
                }

                dialog.GetResult(out var singleItem);
                singleItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var singleFilePath);
                tcs.SetResult(new[] { singleFilePath });
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

    /// <summary>
    /// Shows a "Save as" dialog owned by <paramref name="ownerHwnd"/>.
    /// Returns the selected file path, or <c>null</c> if cancelled.
    /// </summary>
    public static Task<string?> PickSaveFileAsync(
        IntPtr ownerHwnd,
        string title = "Save as",
        string suggestedFileName = "",
        string defaultExtension = "txt")
    {
        var tcs = new TaskCompletionSource<string?>();
        var staThread = new Thread(() =>
        {
            IntPtr filterSpecPtr = IntPtr.Zero;
            try
            {
                var dialog = (IFileSaveDialog)new FileSaveDialogClass();
                dialog.SetOptions(FOS.FOS_FORCEFILESYSTEM | FOS.FOS_PATHMUSTEXIST | FOS.FOS_OVERWRITEPROMPT);
                dialog.SetTitle(title);
                dialog.SetDefaultExtension(defaultExtension);
                if (!string.IsNullOrWhiteSpace(suggestedFileName))
                    dialog.SetFileName(suggestedFileName);

                var filterSpec = new COMDLG_FILTERSPEC("Text file (*.txt)", "*.txt");
                filterSpecPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<COMDLG_FILTERSPEC>());
                Marshal.StructureToPtr(filterSpec, filterSpecPtr, fDeleteOld: false);
                dialog.SetFileTypes(1, filterSpecPtr);
                dialog.SetFileTypeIndex(1);

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
            finally
            {
                if (filterSpecPtr != IntPtr.Zero)
                {
                    Marshal.DestroyStructure<COMDLG_FILTERSPEC>(filterSpecPtr);
                    Marshal.FreeCoTaskMem(filterSpecPtr);
                }
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

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogClass { }

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
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog
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
        void SetSaveAsItem(IShellItem psi);
        void SetProperties(IntPtr pStore);
        void SetCollectedProperties(IntPtr pList, bool fAppendDefault);
        void GetProperties(out IntPtr ppStore);
        void ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string pszName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string pszSpec;

        public COMDLG_FILTERSPEC(string name, string spec)
        {
            pszName = name;
            pszSpec = spec;
        }
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
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
        FOS_ALLOWMULTISELECT = 0x200,
        FOS_FILEMUSTEXIST = 0x1000,
        FOS_PATHMUSTEXIST = 0x800,
        FOS_OVERWRITEPROMPT = 0x2,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }
}
