#Requires -Version 7.4
Set-StrictMode -Version Latest

function Invoke-NativeProofProcess {
    param([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$StandardInput = '')

    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $deadline = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds($TimeoutSeconds))
    $started = $false
    try {
        if (-not $process.Start()) { throw 'Native proof process did not start.' }
        $started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync($deadline.Token)
        $stderr = $process.StandardError.ReadToEndAsync($deadline.Token)
        $null = $process.StandardInput.WriteAsync($StandardInput).WaitAsync($deadline.Token).GetAwaiter().GetResult()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Native proof command timed out.'
        }
        # Package responses can contain credentials. Never echo stdout, stderr or parse errors.
        try {
            $null = $stderr.GetAwaiter().GetResult()
            return @{ exitCode = $process.ExitCode; output = $stdout.GetAwaiter().GetResult() }
        } catch {
            throw 'Native proof output did not complete within its deadline.'
        }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
        $deadline.Dispose()
    }
}

function Test-NativeProofInteger($Value) {
    return $Value -is [int] -or $Value -is [long]
}

function ConvertFrom-NativeProofJson {
    param($Result)
    if ($Result.exitCode -ne 0) { throw 'Native proof command failed.' }
    try { $value = ConvertFrom-Json -InputObject $Result.output -AsHashtable -ErrorAction Stop }
    catch { throw 'Native proof command returned invalid JSON.' }
    if ($value -isnot [System.Collections.IDictionary]) { throw 'Native proof response must be an object.' }
    return $value
}

function Assert-NativeSessionProbe {
    param($Result)
    $probe = ConvertFrom-NativeProofJson $Result
    if ($probe.probes -isnot [System.Collections.IDictionary] -or
        $probe.probes.isolationSessionAvailable -isnot [bool] -or
        -not $probe.probes.isolationSessionAvailable) {
        throw 'Native MXC session capability is unavailable or unverified. Check Windows Update.'
    }
    # Process BaseContainer admission is a different contract, not a session substitute.
}

function Get-NativeGatewayBuildIdentity {
    param($Result, [string]$PackagingSha)
    $version = ConvertFrom-NativeProofJson $Result
    if (-not (Test-NativeProofInteger $version.schemaVersion) -or
        $version.schemaVersion -cne 1 -or $version.command -cne 'version' -or
        $version.ok -isnot [bool] -or -not $version.ok -or
        $version.package.commit -cne $PackagingSha -or
        $version.package.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
        $version.payload.commit -cnotmatch '^[0-9a-f]{40}$' -or
        $version.payload.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw 'Installed Gateway build identity does not match the approved package.'
    }
    return @{
        packageVersion = $version.package.version
        packagingSha = $version.package.commit
        payloadVersion = $version.payload.version
        payloadSha = $version.payload.commit
    }
}

function Invoke-NativeGatewayLifecycle {
    param([Parameter(Mandatory)][scriptblock]$InvokeCommand)

    function Invoke-Package([string[]]$Arguments, [string]$Command) {
        $response = ConvertFrom-NativeProofJson (& $InvokeCommand $Arguments)
        if (-not (Test-NativeProofInteger $response.schemaVersion) -or
            $response.schemaVersion -cne 1 -or $response.command -cne $Command -or
            $response.integration.kind -cne 'isolated-session' -or
            -not (Test-NativeProofInteger $response.integration.version) -or
            $response.integration.version -cne 1 -or
            $response.ok -isnot [bool] -or -not $response.ok) {
            throw "Unsupported or unsuccessful package response for $Command."
        }
        return $response
    }

    function Assert-Running($Response, [string]$SandboxId, [int]$Port) {
        $gateway = $Response.gateway
        if ($gateway.state -cne 'running' -or
            -not (Test-NativeProofInteger $gateway.port) -or $gateway.port -ne $Port -or
            $gateway.ownership.sandboxId -cne $SandboxId -or
            $gateway.ownership.agentUserSid -isnot [string] -or
            $gateway.ownership.agentUserSid -notmatch '^S-1-5-21-[0-9-]+$') {
            throw 'Gateway is not running in the task-owned isolated session.'
        }
        $listeners = @($gateway.ownership.listeners)
        if ($listeners.Count -eq 0) { throw 'Gateway did not report owned listeners.' }
        foreach ($listener in $listeners) {
            $created = [datetime]::MinValue
            if (-not (Test-NativeProofInteger $listener.port) -or $listener.port -ne $Port -or
                -not (Test-NativeProofInteger $listener.processId) -or $listener.processId -le 0 -or
                -not (Test-NativeProofInteger $listener.sequenceNumber) -or $listener.sequenceNumber -le 0 -or
                -not [datetime]::TryParse([string]$listener.processStartTimeUtc, [ref]$created)) {
                throw 'Gateway listener ownership is incomplete.'
            }
        }
        return @($listeners | ForEach-Object { "$($_.processId):$($_.sequenceNumber)" })
    }

    $initial = Invoke-Package @('status', '--json') 'status'
    if ($initial.session.state -cne 'not-configured' -or
        $initial.gateway.state -cnotin @('not-started', 'stopped')) {
        throw 'Native proof requires an unused package session. Existing sessions are never changed.'
    }

    $operations = [Collections.Generic.List[string]]::new()
    $failure = $null
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    try {
        # From this point setup may have partially created a session, so teardown is mandatory.
        $setup = Invoke-Package @('setup', '--json') 'setup'
        if ($setup.session.state -cne 'ready' -or
            $setup.session.sandboxId -isnot [string] -or
            [string]::IsNullOrWhiteSpace($setup.session.sandboxId)) {
            throw 'Package setup did not create a ready isolated session.'
        }
        $sandboxId = $setup.session.sandboxId
        $operations.Add('setup')
        $prepared = Invoke-Package @('companion', 'prepare', '--port', '18789', '--json') 'companion prepare'
        $port = $prepared.companion.port
        if ($port -isnot [long] -and $port -isnot [int]) { throw 'Package returned an invalid port.' }
        if ($port -lt 1 -or $port -gt 65535 -or
            $prepared.companion.token -isnot [string] -or
            [string]::IsNullOrWhiteSpace($prepared.companion.token)) {
            throw 'Package did not prepare Companion configuration.'
        }
        $prepared = $null
        $operations.Add('companion prepare')
        $null = Invoke-Package @('gateway-service', 'start', '--json') 'gateway-service start'
        $running = Invoke-Package @('gateway-service', 'status', '--json') 'gateway-service status'
        $before = @(Assert-Running $running $sandboxId $port)
        $operations.Add('start/status')
        $null = Invoke-Package @('gateway-service', 'restart', '--json') 'gateway-service restart'
        $restarted = Invoke-Package @('gateway-service', 'status', '--json') 'gateway-service status'
        $after = @(Assert-Running $restarted $sandboxId $port)
        if (@($after | Where-Object { $_ -in $before }).Count -gt 0) {
            throw 'Gateway restart retained a previous listener process.'
        }
        $operations.Add('restart/status')
    } catch {
        # Only fixed stage names are safe evidence, not package output or exception payloads.
        $failure = 'Native Gateway lifecycle failed.'
    } finally {
        try {
            $null = Invoke-Package @('gateway-service', 'stop', '--json') 'gateway-service stop'
            $stopped = Invoke-Package @('gateway-service', 'status', '--json') 'gateway-service status'
            if ($stopped.gateway.state -cnotin @('stopped', 'not-started')) {
                throw 'Gateway did not stop.'
            }
            $operations.Add('stop/status')
        } catch { $cleanupFailures.Add('stop') }
        try {
            $null = Invoke-Package @('teardown', '--force', '--json') 'teardown'
            $final = Invoke-Package @('status', '--json') 'status'
            if ($final.session.state -cne 'not-configured' -or
                $final.gateway.state -cnotin @('not-started', 'stopped')) {
                throw 'Package session remained after teardown.'
            }
            $operations.Add('teardown/status')
        } catch { $cleanupFailures.Add('teardown') }
    }
    if ($failure -or $cleanupFailures.Count) {
        throw "Native Gateway proof failed after [$($operations -join ', ')]. Cleanup failures: [$($cleanupFailures -join ', ')]."
    }
    return @($operations)
}

Export-ModuleMember -Function Invoke-NativeProofProcess, Assert-NativeSessionProbe, Get-NativeGatewayBuildIdentity, Invoke-NativeGatewayLifecycle
