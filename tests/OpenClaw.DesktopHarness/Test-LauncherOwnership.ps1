[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Launcher,
    [Parameter(Mandatory)][string]$ProbeExecutable,
    [Parameter(Mandatory)][string]$NativeExecutable,
    [Parameter(Mandatory)][string]$NativeProbeExecutable,
    [Parameter(Mandatory)][string]$NativeInitFailureExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$QuietStartupOnly,
    [ValidateRange(1,2147483647)][int]$ChildSignalTimeoutMs = 30000
)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$names=@('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_MCP_PORT')
$initial=[Environment]::GetEnvironmentVariables('Process')
$saved=@{}
foreach ($name in $names) { $saved[$name]=@{ exists=$initial.Contains($name); value=$initial[$name] } }
$reference=[IntPtr]::Zero
$contender=[IntPtr]::Zero
Add-Type -TypeDefinition 'using System.Runtime.InteropServices;public static class ProofCallerErrorMode{[DllImport("kernel32.dll")]public static extern uint GetErrorMode();[DllImport("kernel32.dll")]public static extern uint SetErrorMode(uint mode);}'
$ambientErrorMode=[ProofCallerErrorMode]::GetErrorMode()
$callerErrorMode=$ambientErrorMode -band (-bnot [uint32]3)

function Assert-Sentinels {
    if ([ProofCallerErrorMode]::GetErrorMode() -ne $callerErrorMode) {
        throw 'The launcher changed the caller process error mode.'
    }
    $current=[Environment]::GetEnvironmentVariables('Process')
    if ($current['OPENCLAW_TRAY_DATA_DIR'] -cne 'caller-data-sentinel' -or
        $current.Contains('OPENCLAW_TRAY_LOCALAPPDATA_DIR') -or
        $current['OPENCLAW_MCP_PORT'] -cne 'caller-port-sentinel') {
        throw 'The launcher changed the caller isolation environment.'
    }
}

function Assert-DesktopAbsent([string]$Name) {
    $handle=[ProofDesktop]::OpenDesktopW($Name,0,$false,1)
    if ($handle -ne [IntPtr]::Zero) {
        [void][ProofDesktop]::CloseDesktop($handle)
        throw 'An owned test desktop remains after launch completion.'
    }
    if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
        throw 'Desktop cleanup could not be verified.'
    }
}

function Get-RecordedOwnedProcess($Record) {
    $candidate=Get-Process -Id $Record.processId -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    try {
        [void]$candidate.SafeHandle
        if ($candidate.StartTime.ToFileTimeUtc() -ne $Record.processStartFileTime) {
            $candidate.Dispose()
            return $null
        }
        if (!$candidate.HasExited -and $candidate.Path -cne $NativeProbeExecutable) {
            throw 'The recorded timeout fixture process has an unexpected image.'
        }
        return $candidate
    } catch {
        $candidate.Dispose()
        throw
    }
}

function Assert-RecordedProcessExited($Record) {
    $owned=Get-RecordedOwnedProcess $Record
    if ($null -eq $owned) { return }
    try {
        if (!$owned.WaitForExit($ChildSignalTimeoutMs)) {
            throw 'An exact recorded timeout root or descendant remains alive after tree teardown.'
        }
    } finally { $owned.Dispose() }
}

try {
    # Only the task-owned controller uses this mode; every child must receive the quiet flags.
    [void][ProofCallerErrorMode]::SetErrorMode($callerErrorMode)
    [Environment]::SetEnvironmentVariable('OPENCLAW_TRAY_DATA_DIR','caller-data-sentinel','Process')
    [Environment]::SetEnvironmentVariable('OPENCLAW_TRAY_LOCALAPPDATA_DIR',[NullString]::Value,'Process')
    [Environment]::SetEnvironmentVariable('OPENCLAW_MCP_PORT','caller-port-sentinel','Process')
    $legacyFailure=$null
    try {
        & $Launcher -Executable $ProbeExecutable -DesktopName 'DisallowedExactName' `
            -DataDirectory "$OutputDirectory\legacy-profile" -McpPort 0 `
            -ResultPath "$OutputDirectory\legacy-result.json"
    } catch { $legacyFailure=$_ }
    if (!$legacyFailure -or $legacyFailure.FullyQualifiedErrorId -notlike 'NamedParameterNotFound*' -or
        (Test-Path -LiteralPath "$OutputDirectory\legacy-result.json.security.json")) {
        throw 'Caller-selected exact desktop names were not rejected before launch.'
    }
    Assert-Sentinels

    $launchFailure=$null
    $failedPath="$OutputDirectory\failed-result.json"
    try {
        & $Launcher -Executable "$OutputDirectory\missing.exe" -NamePrefix 'FailedOwnershipTest' `
            -DataDirectory "$OutputDirectory\failed-profile" -McpPort 0 -ConsoleProcess `
            -WaitForExit -ResultPath $failedPath
    } catch { $launchFailure=$_ }
    if (!$launchFailure -or $launchFailure.Exception -isnot [ComponentModel.Win32Exception] -or
        $launchFailure.Exception.NativeErrorCode -ne 2) {
        throw 'The failure-path regression did not reach the real CreateProcess failure.'
    }
    Assert-Sentinels
    $failedDesktop=Get-Content -LiteralPath "$failedPath.security.json" -Raw | ConvertFrom-Json
    Assert-DesktopAbsent $failedDesktop.desktop

    $inheritedModePath="$OutputDirectory\inherited-error-mode.json"
    & $Launcher -Executable $NativeProbeExecutable -ArgumentLine ('--error-mode "{0}"' -f $inheritedModePath) `
        -DataDirectory "$OutputDirectory\error-mode-profile" -McpPort 0 -ConsoleProcess -WaitForExit `
        -ResultPath "$OutputDirectory\error-mode-launch.json"
    Assert-Sentinels
    $inheritedMode=Get-Content -LiteralPath $inheritedModePath -Raw | ConvertFrom-Json
    if ($inheritedMode.errorMode -ne ($callerErrorMode -bor 3)) {
        throw 'The actual native child did not inherit both quiet startup error-mode flags.'
    }
    $eventId=[Guid]::NewGuid().ToString('N')
    $ready=[Threading.EventWaitHandle]::new($false,'ManualReset',"Local\OwnedReady$eventId")
    $release=[Threading.EventWaitHandle]::new($false,'ManualReset',"Local\OwnedRelease$eventId")
    $unreaped=$null
    try {
        $childReceipt="$OutputDirectory\waiting-child.json"
        $arguments='ownership-wait "Local\OwnedReady{0}" "Local\OwnedRelease{0}" "{1}"' -f $eventId,$childReceipt
        $blockedReceipt="$OutputDirectory\blocked-receipt"
        New-Item -ItemType Directory -Path $blockedReceipt | Out-Null
        $postCreateFailure=$null
        try {
            & $Launcher -Executable $NativeProbeExecutable -ArgumentLine $arguments `
                -DataDirectory "$OutputDirectory\post-create-profile" -McpPort 0 -ConsoleProcess `
                -ResultPath $blockedReceipt
        } catch { $postCreateFailure=$_ }
        if ($postCreateFailure -and !$postCreateFailure.Exception.Data['OwnedProcessReaped']) {
            if (!$ready.WaitOne($ChildSignalTimeoutMs)) {
                throw [InvalidOperationException]::new('Post-create failure did not reach the controlled native waiting child.', $postCreateFailure.Exception)
            }
            $childState=Get-Content -LiteralPath $childReceipt -Raw | ConvertFrom-Json
            $unreaped=Get-Process -Id $childState.processId
            if ($unreaped.Path -cne $NativeProbeExecutable -or
                $unreaped.StartTime.ToFileTimeUtc() -ne $childState.processStartFileTime -or
                $unreaped.HasExited) {
                throw 'Could not verify the identity of the unreaped controlled child.'
            }
            throw 'Receipt failure left the actual event-controlled child running.'
        }
        if (!$postCreateFailure -or
            !$postCreateFailure.Exception.Data['OwnedProcessReaped'] -or
            !$postCreateFailure.Exception.Data['OwnedProcessId']) {
            throw 'Receipt failure did not reap a successfully created owned child.'
        }
        $failedDesktop=Get-Content -LiteralPath "$blockedReceipt.security.json" -Raw | ConvertFrom-Json
        Assert-DesktopAbsent $failedDesktop.desktop
        Assert-Sentinels
        $ready.Reset() | Out-Null
        $successPath="$OutputDirectory\nonblocking-result.json"
        & $Launcher -Executable $NativeProbeExecutable -ArgumentLine $arguments `
            -DataDirectory "$OutputDirectory\nonblocking-profile" -McpPort 0 -ConsoleProcess `
            -ResultPath $successPath
        $success=Get-Content -LiteralPath $successPath -Raw | ConvertFrom-Json
        $owned=Get-Process -Id $success.processId
        try {
            if (!$ready.WaitOne($ChildSignalTimeoutMs) -or $owned.HasExited) {
                throw 'Successful nonblocking launch did not preserve the waiting child.'
            }
            Assert-Sentinels
        } finally {
            $release.Set() | Out-Null
            if ($null -ne $unreaped) {
                if (!$unreaped.WaitForExit($ChildSignalTimeoutMs)) { throw 'The baseline child did not exit after release.' }
                $unreaped.Dispose()
            }
            if (!$owned.WaitForExit($ChildSignalTimeoutMs)) { throw 'The controlled child did not exit after release.' }
            $owned.Dispose()
        }
        Assert-DesktopAbsent ($success.desktop.Split('\')[-1])
    } finally {
        $release.Set() | Out-Null
        $ready.Dispose()
        $release.Dispose()
    }

    $initializationFailure=$null
    $initializationResult="$OutputDirectory\initialization-failure.json"
    try {
        & $Launcher -Executable $NativeInitFailureExecutable `
            -DataDirectory "$OutputDirectory\initialization-failure-profile" -McpPort 0 `
            -ConsoleProcess -WaitForExit `
            -ProcessTimeoutSeconds ([int][Math]::Ceiling($ChildSignalTimeoutMs / 1000.0)) `
            -ResultPath $initializationResult
    } catch { $initializationFailure=$_ }
    if (!$initializationFailure -or
        $initializationFailure.Exception.Data['NativeProcessExitCode'] -ne [uint32]3221225794 -or
        !$initializationFailure.Exception.Data['OwnedProcessReaped']) {
        throw 'The real DLL attach failure did not stop with explicit native initialization status and owned cleanup.'
    }
    Assert-Sentinels
    $initializationDesktop=Get-Content -LiteralPath "$initializationResult.security.json" -Raw | ConvertFrom-Json
    Assert-DesktopAbsent $initializationDesktop.desktop
    $initializationExit=Get-Content -LiteralPath "$initializationResult.exit.json" -Raw | ConvertFrom-Json
    if (!$initializationExit.nativeHandleSignaled -or $initializationExit.nativeExitStatus -cne '0xC0000142') {
        throw 'The initialization-failure proof has no explicit native exit-status receipt.'
    }
    $treeId=[Guid]::NewGuid().ToString('N')
    $treeReady=[Threading.EventWaitHandle]::new($false,'ManualReset',"Local\TimeoutRoot$treeId")
    $descendantReady=[Threading.EventWaitHandle]::new($false,'ManualReset',"Local\TimeoutChild$treeId")
    $treeRelease=[Threading.EventWaitHandle]::new($false,'ManualReset',"Local\TimeoutRelease$treeId")
    $treeResult="$OutputDirectory\timeout-tree.json"
    $rootReceipt="$OutputDirectory\timeout-root.json"
    $descendantReceipt="$OutputDirectory\timeout-descendant.json"
    $treeError=$null
    try {
        $treeArguments='ownership-tree "Local\TimeoutRoot{0}" "Local\TimeoutChild{0}" "Local\TimeoutRelease{0}" "{1}" "{2}"' -f $treeId,$rootReceipt,$descendantReceipt
        $treeFailure=$null
        try {
            & $Launcher -Executable $NativeProbeExecutable -ArgumentLine $treeArguments `
                -DataDirectory "$OutputDirectory\timeout-profile" -McpPort 0 -ConsoleProcess -WaitForExit `
                -ProcessTimeoutSeconds ([int][Math]::Ceiling($ChildSignalTimeoutMs / 1000.0)) `
                -ResultPath $treeResult
        } catch { $treeFailure=$_ }
        Assert-Sentinels
        if (!$treeFailure -or $treeFailure.Exception.Message -ne 'The exact-owned desktop process exceeded its bound.') {
            throw [InvalidOperationException]::new('The controlled tree did not reach timeout with successful verified-root teardown.', $treeFailure.Exception)
        }
        if (!$treeReady.WaitOne(0) -or !$descendantReady.WaitOne(0)) {
            throw 'The timeout fixture did not establish both controlled waiting processes.'
        }
        $rootRecord=Get-Content -LiteralPath $rootReceipt -Raw | ConvertFrom-Json
        $descendantRecord=Get-Content -LiteralPath $descendantReceipt -Raw | ConvertFrom-Json
        $rootLaunch=Get-Content -LiteralPath "$treeResult.launch.json" -Raw | ConvertFrom-Json
        if ($rootRecord.processId -ne $rootLaunch.processId -or
            $rootRecord.processStartFileTime -ne ([datetime]$rootLaunch.processStartUtc).ToFileTimeUtc()) {
            throw 'The timeout fixture root does not match the native launch identity.'
        }
        Assert-RecordedProcessExited $rootRecord
        Assert-RecordedProcessExited $descendantRecord
    } catch { $treeError=$_ }
    finally {
        $cleanupErrors=[Collections.Generic.List[Exception]]::new()
        $treeRelease.Set() | Out-Null
        foreach ($receiptPath in @($rootReceipt,$descendantReceipt)) {
            $owned=$null
            try {
                if (Test-Path -LiteralPath $receiptPath) {
                    $record=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
                    $owned=Get-RecordedOwnedProcess $record
                    if ($null -ne $owned) {
                        if (!$owned.WaitForExit($ChildSignalTimeoutMs)) {
                            $owned.Kill()
                            if (!$owned.WaitForExit($ChildSignalTimeoutMs)) {
                                throw 'An exact-owned timeout fixture process could not be reaped during test cleanup.'
                            }
                            $cleanupErrors.Add([InvalidOperationException]::new('An exact-owned timeout fixture process required forced test cleanup after release.'))
                        }
                    }
                }
            } catch { $cleanupErrors.Add($_.Exception) }
            finally { if ($null -ne $owned) { $owned.Dispose() } }
        }
        $treeReady.Dispose()
        $descendantReady.Dispose()
        $treeRelease.Dispose()
        try {
            if (Test-Path -LiteralPath "$treeResult.security.json") {
                $treeDesktop=Get-Content -LiteralPath "$treeResult.security.json" -Raw | ConvertFrom-Json
                Assert-DesktopAbsent $treeDesktop.desktop
            }
        } catch { $cleanupErrors.Add($_.Exception) }
        if ($cleanupErrors.Count -gt 0) {
            if ($null -ne $treeError) { $cleanupErrors.Add($treeError.Exception) }
            throw [AggregateException]::new('Timeout fixture cleanup failed.', $cleanupErrors)
        }
    }
    if ($null -ne $treeError) { throw $treeError }
    [ordered]@{
        completedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        childInheritedQuietErrorMode=$true
        callerErrorModeRestored=$true
        nativeDllInitializationExitStatus=$initializationExit.nativeExitStatus
        nativeHandleSignaled=$true
        ownedDesktopAbsent=$true
        postCreateReceiptFailureReaped=$true
        successfulNonblockingChildRetained=$true
        environmentRestored=$true
        timeoutRootAndDescendantExited=$true
        visibleNoModalObservation='Not recorded. Requires coordinated Home observation.'
    } | ConvertTo-Json | Set-Content -LiteralPath "$OutputDirectory\quiet-startup-regression.json" -Encoding utf8
    if ($QuietStartupOnly) { return }

    $contenderName="NativeContender$([Guid]::NewGuid().ToString('N'))"
    $reference=[ProofDesktop]::OpenDesktopW('Default',0,$false,0x20001)
    if ($reference -eq [IntPtr]::Zero) { throw 'Could not open the read-only security reference.' }
    $referenceBytes=[ProofDesktop]::ReadDaclAndLabel($reference)
    $descriptor=[Runtime.InteropServices.Marshal]::AllocHGlobal($referenceBytes.Length)
    try {
        [Runtime.InteropServices.Marshal]::Copy($referenceBytes,0,$descriptor,$referenceBytes.Length)
        $security=[ProofDesktop+SecurityAttributes]::new()
        $security.size=[Runtime.InteropServices.Marshal]::SizeOf($security)
        $security.descriptor=$descriptor
        $contender=[ProofDesktop]::CreateDesktopWithSecurityW($contenderName,[IntPtr]::Zero,[IntPtr]::Zero,0,0xF01FF,[ref]$security)
    } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($descriptor) }
    if ($contender -eq [IntPtr]::Zero) { throw 'Could not create the task-owned contender.' }
    $contenderBytes=[ProofDesktop]::ReadDaclAndLabel($contender)
    $childOutput="$OutputDirectory\actual-child"
    $dataDirectory="$OutputDirectory\successful-profile"
    $arguments='"{0}" "{1}" native-probe "{2}"' -f $childOutput,$NativeExecutable,$NativeProbeExecutable
    $successPath="$OutputDirectory\successful-result.json"
    & $Launcher -Executable $ProbeExecutable -ArgumentLine $arguments -NamePrefix $contenderName `
        -DataDirectory $dataDirectory -McpPort 0 -ConsoleProcess -WaitForExit `
        -ProcessTimeoutSeconds 45 -ResultPath $successPath
    Assert-Sentinels
    $launch=Get-Content -LiteralPath $successPath -Raw | ConvertFrom-Json
    $child=Get-Content -LiteralPath "$childOutput\result.json" -Raw | ConvertFrom-Json
    $actualDesktop=$launch.desktop.Split('\')[-1]
    if ($actualDesktop -ceq $contenderName -or $child.caller.desktop -cne $actualDesktop -or
        !$actualDesktop.StartsWith("$contenderName-",[StringComparison]::Ordinal) -or
        $child.ExitCode -ne 0 -or $child.inheritedIsolation.dataDirectory -cne $dataDirectory -or
        $child.inheritedIsolation.localAppDataDirectory -cne (Join-Path $dataDirectory 'local') -or
        $child.inheritedIsolation.mcpPort -cne '0') {
        throw 'The real child did not receive its own generated desktop and isolated environment.'
    }
    if (![ProofDesktop]::SameDaclAndLabel($contenderBytes,[ProofDesktop]::ReadDaclAndLabel($contender))) {
        throw 'The live task-owned contender security changed.'
    }
    Assert-DesktopAbsent $actualDesktop
    [void][ProofDesktop]::CloseDesktop($contender)
    $contender=[IntPtr]::Zero
    Assert-DesktopAbsent $contenderName
    [ordered]@{
        completedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        exactNameRejectedBeforeLaunch=$true
        realLaunchFailureEnvironmentRestored=$true
        successEnvironmentRestored=$true
        unsetVariablePreserved=$true
        childInheritedIntendedOverrides=$true
        actualChildAvoidedLiveContender=$true
        contenderSecurityUnchanged=$true
        postCreateReceiptFailureReaped=$true
        successfulNonblockingChildRetained=$true
        childInheritedQuietErrorMode=$true
        callerErrorModeRestored=$true
        nativeDllInitializationFailureStopped=$true
        ownedDesktopsRemoved=$true
    } | ConvertTo-Json | Set-Content -LiteralPath "$OutputDirectory\ownership-regression.json" -Encoding utf8
}
finally {
    [void][ProofCallerErrorMode]::SetErrorMode($ambientErrorMode)
    if ($contender -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($contender) }
    if ($reference -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($reference) }
    foreach ($name in $names) {
        $value=if ($saved[$name].exists) { [string]$saved[$name].value } else { [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name,$value,'Process')
    }
}
