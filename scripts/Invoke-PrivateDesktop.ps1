[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$ArgumentLine = '',
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][int]$McpPort,
    [switch]$ConsoleProcess,
    [switch]$WaitForExit,
    [switch]$NullSecurityNegativeControl,
    [ValidateRange(1,2147483)][int]$ProcessTimeoutSeconds = 360,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$NamePrefix = 'OpenClawProof',
    [Parameter(Mandatory)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'PrivateDesktopSecurity.cs')
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;

public static class ProofDesktop
{
    private static readonly object ErrorModeLock = new object();
    [DllImport("kernel32.dll")]
    public static extern uint GetErrorMode();
    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr process, out long created, out long exited,
        out long kernel, out long user);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint WaitForInputIdle(IntPtr process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
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

    public static bool CreateQuietProcess(string executable, StringBuilder command, uint flags,
        string directory, ref StartupInfo startup, ref ProcessInfo info,
        out int error, out uint previousErrorMode, out uint launchErrorMode)
    {
        lock (ErrorModeLock)
        {
            previousErrorMode = GetErrorMode();
            launchErrorMode = previousErrorMode | 0x0001u | 0x0002u;
            SetErrorMode(launchErrorMode);
            try
            {
                bool created = CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero,
                    false, flags, IntPtr.Zero, directory, ref startup, out info);
                error = Marshal.GetLastWin32Error();
                return created;
            }
            finally { SetErrorMode(previousErrorMode); }
        }
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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetUserObjectSecurity(IntPtr handle, ref uint information,
        byte[] descriptor);
    public static void CompleteNullCreatedDesktop(IntPtr ownedDesktop, byte[] created)
    {
        uint information = 0x14;
        if (!SetUserObjectSecurity(ownedDesktop, ref information, created))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
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

$DesktopName = "$NamePrefix-$([Guid]::NewGuid().ToString('N'))"
$ownershipKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($DesktopName.ToUpperInvariant())))
$ownership = [Threading.Mutex]::new($false, "Local\OpenClawDesktop-$ownershipKey")
$ownsName = $false
$environmentNames = @('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_MCP_PORT')
$environmentBefore = [Environment]::GetEnvironmentVariables('Process')
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = @{
        exists = $environmentBefore.Contains($name)
        value = $environmentBefore[$name]
    }
}
$reference = [IntPtr]::Zero
$desktop = [IntPtr]::Zero
$info = [ProofDesktop+ProcessInfo]::new()
try {
    try { $ownsName = $ownership.WaitOne(0) }
    catch [Threading.AbandonedMutexException] {
        $ownsName = $true
        throw 'Proof-launch ownership was abandoned; refusing to continue.'
    }
    if (!$ownsName) { throw 'Could not acquire exclusive proof-launch ownership.' }
    $existing = [ProofDesktop]::OpenDesktopW($DesktopName, 0, $false, 1)
    if ($existing -ne [IntPtr]::Zero) {
        [void][ProofDesktop]::CloseDesktop($existing)
        throw 'The generated proof desktop already exists; refusing to reuse it.'
    }
    if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $reference = [ProofDesktop]::OpenDesktopW('Default', 0, $false, 0x20001)
    if ($reference -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
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
    if ($NullSecurityNegativeControl) {
        # NULL attributes inherit the creator token's read-only package ACEs and
        # implicit Medium label, not the interactive desktop's Low-label contract.
        $completedBytes = [PrivateDesktopSecurity]::CompleteNullCreatedDesktop($createdBytes)
        [ProofDesktop]::CompleteNullCreatedDesktop($desktop, $completedBytes)
        $appliedBytes = [ProofDesktop]::ReadDaclAndLabel($desktop)
        if (![ProofDesktop]::SameDaclAndLabel($completedBytes, $appliedBytes)) {
            throw 'Owned NULL-created desktop access completion did not persist.'
        }
        [ordered]@{
            desktop = $DesktopName
            original = $createdSecurity
            applied = [ProofDesktop]::Describe($appliedBytes)
            packageAdditionalRights = 0x82
            label = 'Low, no-write-up'
        } | ConvertTo-Json | Set-Content -LiteralPath "$ResultPath.access-completion.json" -Encoding utf8
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
    $creationError=0
    [uint32]$previousErrorMode=0
    [uint32]$launchErrorMode=0
    if (-not [ProofDesktop]::CreateQuietProcess($Executable, $command, $creationFlags,
        (Split-Path $Executable), [ref]$startup, [ref]$info, [ref]$creationError,
        [ref]$previousErrorMode, [ref]$launchErrorMode)) {
        throw [ComponentModel.Win32Exception]::new($creationError)
    }
    [long]$created=0; [long]$exited=0; [long]$kernel=0; [long]$user=0
    if (![ProofDesktop]::GetProcessTimes($info.process,[ref]$created,[ref]$exited,[ref]$kernel,[ref]$user)) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $parent=[Diagnostics.Process]::GetCurrentProcess()
    try {
        [ordered]@{
            capturedUtc=[DateTimeOffset]::UtcNow.ToString('O')
            processId=$info.processId
            processStartUtc=[DateTime]::FromFileTimeUtc($created).ToString('O')
            parentProcessId=$parent.Id
            parentProcessStartUtc=$parent.StartTime.ToUniversalTime().ToString('O')
            executable=$Executable
            argumentLine=$ArgumentLine
            requestedDesktop=$startup.desktop
            creationFlags=$creationFlags
            previousErrorMode=$previousErrorMode
            launchErrorMode=$launchErrorMode
            restoredErrorMode=[ProofDesktop]::GetErrorMode()
        } | ConvertTo-Json | Set-Content -LiteralPath "$ResultPath.launch.json" -Encoding utf8
    } finally { $parent.Dispose() }
    $inputIdle=$null
    if (!$ConsoleProcess) {
        $idle=[ProofDesktop]::WaitForInputIdle($info.process,30000)
        if ($idle -eq [uint32]::MaxValue) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
        $inputIdle=($idle -eq 0)
    }
    [ordered]@{
        processId = $info.processId
        processStartUtc = [DateTime]::FromFileTimeUtc($created).ToString('o')
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
    if ($WaitForExit) {
        $wait=[ProofDesktop]::WaitForSingleObject($info.process,[uint32]($ProcessTimeoutSeconds * 1000))
        if ($wait -eq 258) {
            $timeoutRoot=$null
            try {
                $timeoutRoot=[Diagnostics.Process]::GetProcessById([int]$info.processId)
                $timeoutRootHandle=$timeoutRoot.SafeHandle
                [long]$timeoutCreated=0; [long]$timeoutExited=0; [long]$timeoutKernel=0; [long]$timeoutUser=0
                if (![ProofDesktop]::GetProcessTimes($timeoutRootHandle.DangerousGetHandle(),
                    [ref]$timeoutCreated,[ref]$timeoutExited,[ref]$timeoutKernel,[ref]$timeoutUser)) {
                    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
                if ($timeoutCreated -ne $created) {
                    throw 'The timeout root PID no longer identifies the exact-owned native process.'
                }
                $timeoutRoot.Kill($true)
                if ([ProofDesktop]::WaitForSingleObject($info.process,[uint32]($ProcessTimeoutSeconds * 1000)) -ne 0) {
                    throw 'The timeout root native handle did not signal after tree termination.'
                }
            } catch {
                throw [InvalidOperationException]::new('The exact-owned process timed out and verified-root tree cleanup failed.', $_.Exception)
            } finally {
                if ($null -ne $timeoutRoot) { $timeoutRoot.Dispose() }
            }
            throw 'The exact-owned desktop process exceeded its bound.'
        }
        if ($wait -ne 0) { throw 'The exact-owned desktop process wait failed.' }
        [uint32]$nativeExitCode=0
        if (![ProofDesktop]::GetExitCodeProcess($info.process,[ref]$nativeExitCode)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
        [ordered]@{
            observedUtc=[DateTimeOffset]::UtcNow.ToString('O')
            processId=$info.processId
            nativeExitCode=$nativeExitCode
            nativeExitStatus=('0x{0:X8}' -f $nativeExitCode)
            nativeHandleSignaled=$true
        } | ConvertTo-Json | Set-Content -LiteralPath "$ResultPath.exit.json" -Encoding utf8
        if ($nativeExitCode -eq [uint32]3221225794) {
            $initializationFailure=[InvalidOperationException]::new('The exact-owned child failed native DLL initialization (0xC0000142). Stop this proof lane.')
            $initializationFailure.Data['NativeProcessExitCode']=$nativeExitCode
            throw $initializationFailure
        }
    }
    if ([ProofDesktop]::Describe([ProofDesktop]::ReadDaclAndLabel($reference)) -cne $referenceSecurity) {
        throw 'The Default reference changed during the proof; no restoration attempted.'
    }
}
catch {
    $launchError = $_
    # The CreateProcess handle identifies only our child, even if its PID is reused.
    if ($info.process -ne [IntPtr]::Zero) {
        if ([ProofDesktop]::WaitForSingleObject($info.process, 0) -eq 258) {
            if (![ProofDesktop]::TerminateProcess($info.process, 1)) {
                throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
            }
        }
        if ([ProofDesktop]::WaitForSingleObject($info.process, [uint32]($ProcessTimeoutSeconds * 1000)) -ne 0) {
            throw 'Could not reap the exact-owned process after launch failure.'
        }
        $launchError.Exception.Data['OwnedProcessId'] = $info.processId
        $launchError.Exception.Data['OwnedProcessReaped'] = $true
    }
    throw $launchError
}
finally {
    foreach ($name in $environmentNames) {
        $original = $originalEnvironment[$name]
        $value = if ($original.exists) { [string]$original.value } else { [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    if ($info.thread -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseHandle($info.thread) }
    if ($info.process -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseHandle($info.process) }
    if ($desktop -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($desktop) }
    if ($reference -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($reference) }
    if ($ownsName) { $ownership.ReleaseMutex() }
    $ownership.Dispose()
}
