using System;
using System.Runtime.InteropServices;

// Same read-only boundary as DesktopCanPresentEvaluator at main932bc0e3.
// No desktop name, switching, foreground manipulation, or ownership claim.
public static class InventoryInputDesktop
{
    public sealed class Observation
    {
        public bool? Accessible { get; set; }
        public bool? HandleClosed { get; set; }
    }
    public static Observation Read()
    {
        var result = new Observation();
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS only.
            result.Accessible = handle != IntPtr.Zero;
        }
        catch { result.Accessible = null; }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { result.HandleClosed = CloseDesktop(handle); }
                catch { result.HandleClosed = null; }
            }
        }
        return result;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
