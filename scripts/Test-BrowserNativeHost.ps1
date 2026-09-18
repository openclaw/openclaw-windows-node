<#
.SYNOPSIS
    Exercises the packaged native .exe with binary frames and a synthetic current-user tray pipe.
.DESCRIPTION
    Run only on a disposable Windows proof host. Refuses any pre-existing host registration.
    This proves native framing/registry/IPC, not Chrome permission approval or real WSL pairing.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$key = 'Software\Google\Chrome\NativeMessagingHosts\ai.openclaw.browser_bootstrap'
$origin = 'chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/'
$extensionKey = 'Software\Google\Chrome\Extensions\kcdjddhmeafeomebliikmbpblkmkfoig'
$root = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry32')
foreach ($hive in @('CurrentUser', 'LocalMachine')) {
    foreach ($view in @('Registry32', 'Registry64')) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
        try {
            foreach ($probeKey in @($key, $extensionKey)) {
                $existing = $base.OpenSubKey($probeKey)
                if ($null -ne $existing) { $existing.Dispose(); throw 'Proof refuses an existing native host or extension registration.' }
            }
        } finally { $base.Dispose() }
    }
}

function Start-Native([string]$CallerOrigin) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($CallerOrigin)
    $start.ArgumentList.Add('--parent-window=0')
    [Diagnostics.Process]::Start($start)
}
function Write-Frame([IO.Stream]$Stream, [byte[]]$Bytes) {
    $header = [BitConverter]::GetBytes([uint32]$Bytes.Length)
    $Stream.Write($header, 0, 4)
    $Stream.Write($Bytes, 0, $Bytes.Length)
    $Stream.Flush()
}
function Read-Frame([IO.Stream]$Stream) {
    $ct = [Threading.CancellationTokenSource]::new(10000)
    try {
        $header = [byte[]]::new(4)
        $Stream.ReadExactlyAsync([Memory[byte]]$header, $ct.Token).AsTask().GetAwaiter().GetResult()
        $length = [BitConverter]::ToUInt32($header, 0)
        if ($length -eq 0 -or $length -gt 4096) { throw 'Invalid response frame length.' }
        $bytes = [byte[]]::new($length)
        $Stream.ReadExactlyAsync([Memory[byte]]$bytes, $ct.Token).AsTask().GetAwaiter().GetResult()
        [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    } finally { $ct.Dispose() }
}
function Assert-Exit([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForExit(10000)) { $Process.Kill($true); throw 'Native host did not exit.' }
    if ($Process.ExitCode -ne 0) { throw 'Native host returned nonzero exit.' }
    if ($Process.StandardError.ReadToEnd().Length -ne 0) { throw 'Native host unexpectedly wrote diagnostics.' }
    if ($Process.StandardOutput.BaseStream.ReadByte() -ne -1) { throw 'Native host wrote trailing unframed output.' }
}

$registered = $false
$requestRegistered = $false
try {
    & $exe --request-extension
    if ($LASTEXITCODE -eq 0) { throw 'Store request succeeded before native registration.' }
    $early = $root.OpenSubKey($extensionKey)
    if ($null -ne $early) { $early.Dispose(); throw 'Store registry key appeared before native registration.' }
    & $exe --register
    if ($LASTEXITCODE -ne 0) { throw 'Native registration failed.' }
    $registered = $true
    & $exe --request-extension
    if ($LASTEXITCODE -ne 0) { throw 'Owned HKCU Store request failed.' }
    $requestRegistered = $true
    $requestKey = $root.OpenSubKey($extensionKey)
    try {
        if ($requestKey.GetValue('update_url') -ne 'https://clients2.google.com/service/update2/crx' -or
            $requestKey.GetValue('openclaw_native_host') -ne $exe -or $requestKey.ValueCount -ne 2) {
            throw 'Store request registry payload was not exact.'
        }
    } finally { $requestKey.Dispose() }
    & $exe --request-extension
    if ($LASTEXITCODE -ne 0) { throw 'Owned Store request was not idempotent.' }
    $process = Start-Native 'chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/'
    try {
        $reply = Read-Frame $process.StandardOutput.BaseStream
        if ($reply.ok -or $reply.code -ne 'origin_forbidden') { throw 'Foreign origin was not rejected.' }
        Assert-Exit $process
    } finally { $process.Dispose() }

    $process = Start-Native $origin
    try {
        $header = [BitConverter]::GetBytes([uint32]4097)
        $process.StandardInput.BaseStream.Write($header, 0, 4)
        $process.StandardInput.Close()
        $reply = Read-Frame $process.StandardOutput.BaseStream
        if ($reply.ok -or $reply.code -ne 'invalid_frame') { throw 'Oversized frame was not rejected.' }
        Assert-Exit $process
    } finally { $process.Dispose() }

    $pipe = [IO.Pipes.NamedPipeServerStream]::new('OpenClawTray.BrowserBootstrap.v1',
        [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    try {
        $waiting = $pipe.WaitForConnectionAsync()
        $process = Start-Native $origin
        try {
            $json = '{"v":1,"op":"bootstrap","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}'
            Write-Frame $process.StandardInput.BaseStream ([Text.Encoding]::UTF8.GetBytes($json))
            if (-not $waiting.Wait(10000)) { throw 'Native host did not connect to tray pipe.' }
            $request = Read-Frame $pipe
            if ($request.op -ne 'bootstrap' -or $request.nonce -ne 'AAAAAAAAAAAAAAAAAAAAAA') { throw 'Native request changed.' }
            $response = '{"v":1,"ok":true,"nonce":"AAAAAAAAAAAAAAAAAAAAAA","pairingString":"synthetic-native-proof"}'
            Write-Frame $pipe ([Text.Encoding]::UTF8.GetBytes($response))
            $reply = Read-Frame $process.StandardOutput.BaseStream
            if (-not $reply.ok -or $reply.pairingString -ne 'synthetic-native-proof' -or $reply.nonce -ne $request.nonce) { throw 'Native response changed.' }
            Assert-Exit $process
        } finally {
            if (-not $process.HasExited) { $process.Kill($true) }
            $process.Dispose()
        }
    } finally { $pipe.Dispose() }
    & $exe --remove-extension-request
    if ($LASTEXITCODE -ne 0) { throw 'Owned Store request cleanup failed.' }
    $requestRegistered = $false
    $remainingRequest = $root.OpenSubKey($extensionKey)
    if ($null -ne $remainingRequest) { $remainingRequest.Dispose(); throw 'Owned Store request remained after cleanup.' }
    $storeSentinel = 'openclaw-proof-foreign-' + [Guid]::NewGuid().ToString('N')
    $foreignStore = $root.CreateSubKey($extensionKey)
    $foreignStore.SetValue('update_url', 'https://clients2.google.com/service/update2/crx')
    $foreignStore.SetValue('proof_owner', $storeSentinel)
    $foreignStore.Dispose()
    try {
        & $exe --request-extension
        if ($LASTEXITCODE -eq 0) { throw 'Store request adopted a foreign registry entry.' }
        & $exe --remove-extension-request
        if ($LASTEXITCODE -eq 0) { throw 'Store cleanup adopted a foreign registry entry.' }
        $foreignStore = $root.OpenSubKey($extensionKey)
        try {
            if ($foreignStore.ValueCount -ne 2 -or $foreignStore.GetValue('proof_owner') -ne $storeSentinel) {
                throw 'Foreign Store registry entry changed.'
            }
        } finally { $foreignStore.Dispose() }
    } finally {
        $foreignStore = $root.OpenSubKey($extensionKey)
        if ($null -ne $foreignStore) {
            $owned = $foreignStore.GetValue('proof_owner') -eq $storeSentinel
            $foreignStore.Dispose()
            if ($owned) { $root.DeleteSubKey($extensionKey, $false) }
        }
    }
    & $exe --unregister
    if ($LASTEXITCODE -ne 0) { throw 'Native unregister failed.' }
    $registered = $false
    $remaining = $root.OpenSubKey($key)
    if ($null -ne $remaining) { $remaining.Dispose(); throw 'Owned registration remained after uninstall.' }
    # Verify a foreign entry is neither overwritten nor removed. The sentinel is this proof's own key.
    $sentinel = 'openclaw-proof-foreign-' + [Guid]::NewGuid().ToString('N')
    $foreign = $root.CreateSubKey($key)
    $foreign.SetValue('', $sentinel)
    $foreign.Dispose()
    try {
        & $exe --register
        if ($LASTEXITCODE -eq 0) { throw 'Native registration adopted a foreign entry.' }
        & $exe --unregister
        if ($LASTEXITCODE -eq 0) { throw 'Native unregister adopted a foreign entry.' }
        $foreign = $root.OpenSubKey($key)
        try {
            if ($foreign.GetValue('') -ne $sentinel) { throw 'Foreign registration was changed.' }
        } finally { $foreign.Dispose() }
    } finally {
        $foreign = $root.OpenSubKey($key)
        if ($null -ne $foreign) {
            $owned = $foreign.GetValue('') -eq $sentinel
            $foreign.Dispose()
            if ($owned) { $root.DeleteSubKey($key, $false) }
        }
    }
    Write-Host 'NATIVE_BOOTSTRAP_PROOF_OK: binary framing, exact origin, bounded request, current-user IPC, native-first HKCU Store request, owned cleanup, foreign-entry preservation.'
} finally {
    if ($requestRegistered) { & $exe --remove-extension-request }
    if ($registered) { & $exe --unregister }
    $root.Dispose()
}
