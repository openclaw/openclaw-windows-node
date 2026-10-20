param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$ArgumentLine = '',
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][int]$McpPort,
    [switch]$ConsoleProcess,
    [switch]$WaitForExit,
    [switch]$NullSecurityNegativeControl,
    [int]$ProcessTimeoutSeconds = 360,
    [string]$DesktopName = 'HubProof14aff522',
    [Parameter(Mandatory)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;

public static class ProofDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public uint x, y, width, height, xChars, yChars, fill, flags;
        public short showWindow, reservedSize;
        public IntPtr reservedPointer, input, output, error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo
    {
        public IntPtr process, thread;
        public uint processId, threadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int size;
        public IntPtr descriptor;
        public int inheritHandle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode,
        uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopWithSecurityW(string name, IntPtr device, IntPtr mode,
        uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information,
        IntPtr descriptor, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);

    public static byte[] ReadDaclAndLabel(IntPtr handle)
    {
        uint information = 0x14;
        GetUserObjectSecurity(handle, ref information, IntPtr.Zero, 0, out uint size);
        if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetUserObjectSecurity(handle, ref information, buffer, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string Describe(byte[] descriptor)
    {
        var buffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                buffer, 1, 0x14, out var text, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static bool SameDaclAndLabel(byte[] left, byte[] right)
    {
        var first = new RawSecurityDescriptor(left, 0);
        var second = new RawSecurityDescriptor(right, 0);
        return SameAcl(first.DiscretionaryAcl, second.DiscretionaryAcl)
            && SameAcl(first.SystemAcl, second.SystemAcl);
    }

    private static bool SameAcl(GenericAcl left, GenericAcl right)
    {
        if (left == null || right == null) return left == right;
        if (left.BinaryLength != right.BinaryLength) return false;
        var first = new byte[left.BinaryLength];
        var second = new byte[right.BinaryLength];
        left.GetBinaryForm(first, 0);
        right.GetBinaryForm(second, 0);
        for (int index = 0; index < first.Length; index++)
            if (first[index] != second[index]) return false;
        return true;
    }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
        IntPtr environment, string workingDirectory, ref StartupInfo startup,
        out ProcessInfo information);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@

$existing = [ProofDesktop]::OpenDesktopW($DesktopName, 0, $false, 1)
if ($existing -ne [IntPtr]::Zero) {
    [void][ProofDesktop]::CloseDesktop($existing)
    throw 'The requested proof desktop already exists; refusing to reuse it.'
}
if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
$reference = [ProofDesktop]::OpenDesktopW('Default', 0, $false, 0x20001)
if ($reference -eq [IntPtr]::Zero) {
    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
}

$desktop = [IntPtr]::Zero
$info = [ProofDesktop+ProcessInfo]::new()
try {
    $referenceBytes = [ProofDesktop]::ReadDaclAndLabel($reference)
    $referenceSecurity = [ProofDesktop]::Describe($referenceBytes)
    if ($NullSecurityNegativeControl) {
        $desktop = [ProofDesktop]::CreateDesktopW($DesktopName, [IntPtr]::Zero,
            [IntPtr]::Zero, 0, 0xF01FF, [IntPtr]::Zero)
    }
    else {
        $descriptor = [Runtime.InteropServices.Marshal]::AllocHGlobal($referenceBytes.Length)
        try {
            [Runtime.InteropServices.Marshal]::Copy($referenceBytes, 0, $descriptor, $referenceBytes.Length)
            $security = [ProofDesktop+SecurityAttributes]::new()
            $security.size = [Runtime.InteropServices.Marshal]::SizeOf($security)
            $security.descriptor = $descriptor
            $desktop = [ProofDesktop]::CreateDesktopWithSecurityW($DesktopName, [IntPtr]::Zero,
                [IntPtr]::Zero, 0, 0xF01FF, [ref]$security)
        }
        finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($descriptor) }
    }
    if ($desktop -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $createdBytes = [ProofDesktop]::ReadDaclAndLabel($desktop)
    $createdSecurity = [ProofDesktop]::Describe($createdBytes)
    $descriptorMatches = [ProofDesktop]::SameDaclAndLabel($referenceBytes, $createdBytes)
    [ordered]@{
        desktop = $DesktopName
        referenceDaclAndLabel = $referenceSecurity
        createdDaclAndLabel = $createdSecurity
        negativeControl = [bool]$NullSecurityNegativeControl
        aclAndLabelBytesEqual = $descriptorMatches
    } | ConvertTo-Json | Set-Content -LiteralPath "$ResultPath.security.json" -Encoding utf8
    if (!$NullSecurityNegativeControl -and !$descriptorMatches) {
        throw 'New desktop DACL/label differs from the read-only Default reference.'
    }
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    $env:OPENCLAW_TRAY_DATA_DIR = $DataDirectory
    $env:OPENCLAW_TRAY_LOCALAPPDATA_DIR = Join-Path $DataDirectory 'local'
    $env:OPENCLAW_MCP_PORT = $McpPort.ToString()
    $startup = [ProofDesktop+StartupInfo]::new()
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
    $startup.desktop = "winsta0\$DesktopName"
    $command = [Text.StringBuilder]::new(('"{0}" {1}' -f $Executable, $ArgumentLine))
    $creationFlags = if ($ConsoleProcess) { 0x08000000 } else { 0 }
    if (-not [ProofDesktop]::CreateProcessW($Executable, $command, [IntPtr]::Zero,
        [IntPtr]::Zero, $false, $creationFlags, [IntPtr]::Zero, (Split-Path $Executable),
        [ref]$startup, [ref]$info)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $process = Get-Process -Id $info.processId -ErrorAction Stop
    $inputIdle = if ($ConsoleProcess) { $null } else { $process.WaitForInputIdle(30000) }
    [ordered]@{
        processId = $process.Id
        processStartUtc = $process.StartTime.ToUniversalTime().ToString('o')
        executable = $Executable
        desktop = $startup.desktop
        dataDirectory = $DataDirectory
        mcpPort = $McpPort
        inputIdle = $inputIdle
        sharedInputDesktopSwitched = $false
        securityMode = if ($NullSecurityNegativeControl) { 'null-security-negative-control' } else { 'default-dacl-and-label' }
        referenceDaclAndLabel = $referenceSecurity
        createdDaclAndLabel = $createdSecurity
        aclAndLabelBytesEqual = $descriptorMatches
    } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8
    if ($WaitForExit -and -not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'The exact-owned desktop process exceeded its bound.'
    }
    if ([ProofDesktop]::Describe([ProofDesktop]::ReadDaclAndLabel($reference)) -cne $referenceSecurity) {
        throw 'The Default reference changed during the proof; no restoration attempted.'
    }
}
finally {
    if ($info.thread -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseHandle($info.thread) }
    if ($info.process -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseHandle($info.process) }
    if ($desktop -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($desktop) }
    [void][ProofDesktop]::CloseDesktop($reference)
}
