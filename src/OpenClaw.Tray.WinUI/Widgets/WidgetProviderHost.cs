using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;

namespace OpenClawTray.Widgets;

/// <summary>COM activation has its own MTA and does not initialize the Companion UI or claim its single-instance mutex.</summary>
internal static class WidgetProviderHost
{
    internal static int Run()
    {
        return Task.Factory.StartNew(() =>
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            uint cookie = 0;
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                using var provider = new NativeWidgetProvider();
                var factory = new Factory(provider);
                var classId = Guid.Parse(NativeWidgetProvider.ClassId);
                Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref classId, factory, 4, 1, out cookie));
                provider.Start();
                using var stopped = new ManualResetEventSlim();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.Set();
                stopped.Wait();
                GC.KeepAlive(factory);
                return 0;
            }
            finally
            {
                if (cookie != 0)
                    Marshal.ThrowExceptionForHR(CoRevokeClassObject(cookie));
                CoUninitialize();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Factory(NativeWidgetProvider provider) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero)
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            var identity = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(provider);
            try { return Marshal.QueryInterface(identity, in iid, out instance); }
            finally { Marshal.Release(identity); }
        }

        public int LockServer(bool locked) => 0;
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject(
        ref Guid classId, [MarshalAs(UnmanagedType.Interface)] IClassFactory factory,
        uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
}
