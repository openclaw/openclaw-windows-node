using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

if (args.Length < 3)
    throw new ArgumentException("Output directory, exact native executable and payload kind are required.");
var output = Path.GetFullPath(args[0]);
var native = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("OPENCLAW_WXC_EXEC", native);
Environment.SetEnvironmentVariable(DirectAppContainerExecutor.LogFullConfigEnvVar, "1");
var options = new JsonSerializerOptions { WriteIndented = true };
var context = DesktopContext.Capture();
File.WriteAllText(Path.Combine(output, "parent-context.json"), JsonSerializer.Serialize(context, options));
string[] argv = args[2] switch
{
    "powershell" => [Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        "-NoProfile", "-Command", "Write-Output 'OPENCLAW_NATIVE_STARTUP_PROBE'"],
    "where" => [Path.Combine(Environment.SystemDirectory, "where.exe"), "cmd.exe"],
    "builtin" => [Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/s", "/c", "exit 0"],
    "native-probe" when args.Length == 4 => [Path.GetFullPath(args[3]),
        "--current-sddl", JsonSerializer.SerializeToElement(context.currentDesktopSecurity).GetProperty("sddl").GetString()!,
        "--default-sddl", JsonSerializer.SerializeToElement(context.defaultDesktopSecurity).GetProperty("sddl").GetString()!],
    _ => throw new ArgumentException("Unknown payload.")
};
var logger = new ProbeLogger(output);
var availability = MxcAvailability.Probe(null);
if (!availability.CanRunSystemRunSandbox)
    throw new InvalidOperationException("The unchanged emitted BaseContainer policy is not admitted.");
var settings = new SettingsData
{
    SystemRunSandboxEnabled = true,
    SystemRunAllowWindowsUi = true,
    SystemRunAllowOutbound = false
};
var settingsDirectory = Path.Combine(output, "owned-settings");
Directory.CreateDirectory(settingsDirectory);
var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDirectory);
var parameters = JsonSerializer.SerializeToElement(new { argv });
const int diagnosticTimeoutMs = 8000;
var request = new SandboxExecutionRequest(
    "system.run", parameters, policy, diagnosticTimeoutMs, null, null, 65536);
var executor = new DirectAppContainerExecutor(() => availability, logger);
var result = await executor.ExecuteAsync(request);
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    capturedUtc = DateTimeOffset.UtcNow,
    caller = context,
    inheritedIsolation = new
    {
        dataDirectory = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"),
        localAppDataDirectory = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
        mcpPort = Environment.GetEnvironmentVariable("OPENCLAW_MCP_PORT")
    },
    nativeExecutable = availability.WxcExecPath,
    nativeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))),
    sharedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MxcExecutor).Assembly.Location))),
    payload = args[2],
    argv,
    diagnosticTimeoutMs,
    uiAllowed = settings.SystemRunAllowWindowsUi,
    outboundAllowed = settings.SystemRunAllowOutbound,
    result.ExitCode,
    result.TimedOut,
    result.DurationMs,
    result.ContainmentTag,
    result.Stdout,
    result.Stderr
}, options));
Console.WriteLine(JsonSerializer.Serialize(new { payload=args[2], result.ExitCode, result.TimedOut, result.DurationMs, context.sessionId, context.desktop }));

sealed class ProbeLogger(string output) : IOpenClawLogger
{
    public void Info(string message) => Write(message);
    public void Debug(string message) => Write(message);
    public void Warn(string message) => Write(message);
    public void Error(string message, Exception? exception = null) =>
        Write(message + (exception is null ? "" : $" [{exception.GetType().Name}]"));
    private void Write(string message)
    {
        File.AppendAllText(Path.Combine(output, "executor-caller.log"), message + "\n");
        const string marker = "configJson=";
        if (message.StartsWith("[mxc] wxc-exec config (full,", StringComparison.Ordinal))
        {
            var start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
                File.WriteAllText(Path.Combine(output, "actual-config.json"), message[(start + marker.Length)..]);
        }
    }
}

record DesktopContext(int processId, int sessionId, uint threadId, string? userSid,
    string? startupDesktop, string desktop, string station, uint win32kMitigationFlags,
    object currentDesktopSecurity, object defaultDesktopSecurity, object stationSecurity)
{
    internal static DesktopContext Capture()
    {
        var current = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        var station = Native.GetProcessWindowStation();
        Native.GetStartupInfoW(out var startup);
        if (!Native.GetProcessMitigationPolicy(Native.GetCurrentProcess(), 4, out uint policy, 4))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var identity = WindowsIdentity.GetCurrent();
        var defaultDesktop = Native.OpenDesktopW("Default", 0, false, 0x20001);
        object defaultSecurity = defaultDesktop == IntPtr.Zero
            ? new { error = Marshal.GetLastWin32Error() }
            : Security(defaultDesktop);
        if (defaultDesktop != IntPtr.Zero)
            Native.CloseDesktop(defaultDesktop);
        return new(Environment.ProcessId, Process.GetCurrentProcess().SessionId,
            Native.GetCurrentThreadId(), identity.User?.Value, Marshal.PtrToStringUni(startup.desktop),
            Name(current), Name(station), policy, Security(current), defaultSecurity, Security(station));
    }
    private static string Name(IntPtr handle)
    {
        var buffer = new StringBuilder(512);
        if (!Native.GetUserObjectInformationW(handle, 2, buffer, 1024, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    private static object Security(IntPtr handle)
    {
        uint information = 7 | 0x10;
        Native.GetUserObjectSecurity(handle, ref information, IntPtr.Zero, 0, out uint length);
        if (length == 0)
            return new { error = Marshal.GetLastWin32Error() };
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetUserObjectSecurity(handle, ref information, buffer, length, out _))
                return new { error = Marshal.GetLastWin32Error() };
            if (!Native.ConvertSecurityDescriptorToStringSecurityDescriptorW(buffer, 1, information, out var sddl, out _))
                return new { error = Marshal.GetLastWin32Error() };
            try
            {
                var heapKnown = Native.GetUserObjectHeapInformation(handle, 5, out var heap, 4, out _);
                return new
                {
                    sddl = Marshal.PtrToStringUni(sddl),
                    heapKiB = heapKnown ? (uint?)heap : null,
                    heapQueryError = heapKnown ? (int?)null : Marshal.GetLastWin32Error()
                };
            }
            finally { Native.LocalFree(sddl); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Startup
    {
        internal int size;
        internal IntPtr reserved, desktop, title;
        internal uint x, y, width, height, xChars, yChars, fill, flags;
        internal ushort showWindow, reservedSize;
        internal IntPtr reservedPointer, input, output, error;
    }
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern void GetStartupInfoW(out Startup value);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint value, nuint size);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, uint size, out uint needed);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectHeapInformation(IntPtr handle, int index, out uint value, uint size, out uint needed);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern IntPtr OpenDesktopW(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, IntPtr descriptor, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint information, out IntPtr value, out uint length);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr value);
}
