# Scopes the current logon session's client-area animation preference around a synchronous test process.
# Usage: Invoke-WithNativeAnimationPreference -Enabled $true -Action { & dotnet test ... }

if (-not ('OpenClaw.Testing.NativeAnimationPreference' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenClaw.Testing
{
    public static class NativeAnimationPreference
    {
        private const uint GetClientAreaAnimation = 0x1042;
        private const uint SetClientAreaAnimation = 0x1043;
        private const uint SendChange = 0x0002;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool Read(uint action, uint parameter, out int value, uint flags);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool Write(uint action, uint parameter, IntPtr value, uint flags);

        public static string MutexName()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null)
                    throw new InvalidOperationException("Cannot identify the user for animation preference ownership.");
                return @"Local\OpenClaw.Tests.ClientAreaAnimation." + identity.User.Value
                    + "." + Process.GetCurrentProcess().SessionId;
            }
        }

        public static bool Get()
        {
            int value;
            if (!Read(GetClientAreaAnimation, 0, out value, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read client-area animation preference.");
            return value != 0;
        }

        public static void Set(bool enabled)
        {
            // Do not persist a profile change (SPIF_UPDATEINIFILE). Notify the native preference owner.
            if (!Write(SetClientAreaAnimation, 0, new IntPtr(enabled ? 1 : 0), SendChange))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set client-area animation preference.");
        }
    }
}
'@
}

function Invoke-WithNativeAnimationPreference {
    [CmdletBinding(DefaultParameterSetName = 'Set')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Set')][bool]$Enabled,
        [Parameter(Mandatory, ParameterSetName = 'Preserve')][switch]$PreservePreference,
        [Parameter(Mandatory)][scriptblock]$Action,
        [scriptblock]$ReadPreference = { [OpenClaw.Testing.NativeAnimationPreference]::Get() },
        [scriptblock]$WritePreference = { param($value) [OpenClaw.Testing.NativeAnimationPreference]::Set($value) }
    )

    $mutex = [Threading.Mutex]::new($false, [OpenClaw.Testing.NativeAnimationPreference]::MutexName())
    $ownsMutex = $false
    $originalKnown = $false
    $mutationAttempted = $false
    $failure = $null
    $restoreFailure = $null
    $original = $false
    $environmentName = 'OPENCLAW_TEST_ANIMATION_PREFERENCE'
    $previousEnvironment = [Environment]::GetEnvironmentVariable($environmentName, 'Process')
    try {
        try {
            $ownsMutex = $mutex.WaitOne(0)
        } catch [Threading.AbandonedMutexException] {
            $ownsMutex = $true
            throw 'The previous animation preference owner terminated unexpectedly. Refusing to adopt an unknown original setting.'
        }
        if (-not $ownsMutex) {
            throw 'Another test process owns the animation preference for this user/session. No setting was changed.'
        }
        if ($PreservePreference) {
            # Ordinary native UI invocations participate in ownership without reading or changing the preference.
            & $Action
            return
        }
        $original = & $ReadPreference
        if ($original -isnot [bool]) { throw 'Animation preference read did not return a Boolean.' }
        $originalKnown = $true
        if ($original -ne $Enabled) {
            # A failing setter may have changed state before reporting failure.
            $mutationAttempted = $true
            & $WritePreference $Enabled
        }
        $observed = & $ReadPreference
        if ($observed -isnot [bool] -or $observed -ne $Enabled) {
            throw "Could not establish native animation preference Enabled=$Enabled. Tests were not started."
        }
        [Environment]::SetEnvironmentVariable($environmentName, $(if ($Enabled) { 'Enabled' } else { 'Disabled' }), 'Process')
        Write-Host "Native animation preference verified: original=$original; testing=$Enabled."
        & $Action
    } catch {
        $failure = $_.Exception
    } finally {
        if ($ownsMutex -and $originalKnown) {
            try {
                if ($mutationAttempted -or (& $ReadPreference) -ne $original) {
                    & $WritePreference $original
                }
                $restored = & $ReadPreference
                if ($restored -isnot [bool] -or $restored -ne $original) {
                    throw "Read-back did not match original Enabled=$original."
                }
                Write-Host "Native animation preference restored and verified: Enabled=$original."
            } catch {
                $restoreFailure = [InvalidOperationException]::new(
                    "Could not restore native animation preference to Enabled=$original. Manual recovery is required.", $_.Exception)
            }
        }
        [Environment]::SetEnvironmentVariable($environmentName, $previousEnvironment, 'Process')
        if ($ownsMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    if ($failure -and $restoreFailure) {
        throw [AggregateException]::new('Test operation and animation preference restoration both failed.',
            [Exception[]]@($failure, $restoreFailure))
    }
    if ($restoreFailure) { throw $restoreFailure }
    if ($failure) { throw $failure }
}
