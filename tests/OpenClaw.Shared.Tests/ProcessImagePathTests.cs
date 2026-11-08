using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class ProcessImagePathTests
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CleanupTimeoutMs = 5_000;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorPartialCopy = 299;

    [Fact]
    public void Read_BeforeLoaderInitialization_ReturnsObservedImage()
    {
        var expectedImage = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // lpApplicationName chooses the image. A deliberately different argv[0] must not become identity evidence.
        if (!CreateProcess(expectedImage, new StringBuilder("\"not-the-observed-image.exe\""),
            IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNoWindow,
            IntPtr.Zero, null, ref startup, out var created))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var processHandle = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(created.Thread, ownsHandle: true);
        try
        {
            using var process = Process.GetProcessById(created.ProcessId);
            Assert.False(process.HasExited);
            try
            {
                Assert.Null(process.MainModule);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorPartialCopy)
            {
                // Windows may report an unreadable loader list instead of an empty one before initialization.
            }

            Assert.Equal(expectedImage, ProcessImagePath.Read(processHandle), StringComparer.OrdinalIgnoreCase);
            Assert.False(processHandle.IsClosed);
            Assert.False(process.HasExited);
        }
        finally
        {
            // Terminate only the never-resumed process represented by the handle returned from our CreateProcess.
            if (!TerminateProcess(processHandle, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var wait = WaitForSingleObject(processHandle, CleanupTimeoutMs);
            Assert.True(wait == 0, $"Suspended fixture cleanup did not complete: wait result {wait}.");
        }
    }

    [Fact]
    public void Read_CurrentProcess_PreservesCallerHandle()
    {
        using var process = Process.GetCurrentProcess();
        var handle = process.SafeHandle;
        Assert.Equal(Environment.ProcessPath, ProcessImagePath.Read(handle), StringComparer.OrdinalIgnoreCase);
        Assert.False(handle.IsClosed);
        Assert.False(process.HasExited);
    }

    [Fact]
    public void Read_InvalidHandle_ReportsNativeError()
    {
        using var handle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var error = Assert.Throws<Win32Exception>(() => ProcessImagePath.Read(handle));
        Assert.Equal(ErrorInvalidHandle, error.NativeErrorCode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
