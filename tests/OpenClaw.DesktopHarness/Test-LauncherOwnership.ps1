[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Launcher,
    [Parameter(Mandatory)][string]$ProbeExecutable,
    [Parameter(Mandatory)][string]$NativeExecutable,
    [Parameter(Mandatory)][string]$NativeProbeExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
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

function Assert-Sentinels {
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

try {
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
            & $Launcher -Executable $ProbeExecutable -ArgumentLine $arguments `
                -DataDirectory "$OutputDirectory\post-create-profile" -McpPort 0 -ConsoleProcess `
                -ResultPath $blockedReceipt
        } catch { $postCreateFailure=$_ }
        if ($postCreateFailure -and !$postCreateFailure.Exception.Data['OwnedProcessReaped']) {
            if (!$ready.WaitOne($ChildSignalTimeoutMs)) {
                throw 'Post-create failure did not reach the controlled waiting child.'
            }
            $childState=Get-Content -LiteralPath $childReceipt -Raw | ConvertFrom-Json
            $unreaped=Get-Process -Id $childState.processId
            if ($unreaped.Path -cne $ProbeExecutable -or
                $unreaped.StartTime.ToUniversalTime().Ticks -ne ([datetime]$childState.processStartUtc).ToUniversalTime().Ticks -or
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
        & $Launcher -Executable $ProbeExecutable -ArgumentLine $arguments `
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
        ownedDesktopsRemoved=$true
    } | ConvertTo-Json | Set-Content -LiteralPath "$OutputDirectory\ownership-regression.json" -Encoding utf8
}
finally {
    if ($contender -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($contender) }
    if ($reference -ne [IntPtr]::Zero) { [void][ProofDesktop]::CloseDesktop($reference) }
    foreach ($name in $names) {
        $value=if ($saved[$name].exists) { [string]$saved[$name].value } else { [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name,$value,'Process')
    }
}
