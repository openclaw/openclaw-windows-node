#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeGatewayProof.psm1') -Force
$script:passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $script:passed++
        return
    }
    throw "Expected failure containing '$Expected'."
}
function Result($Value, [int]$ExitCode = 0) {
    return @{ exitCode = $ExitCode; output = ConvertTo-Json -InputObject $Value -Depth 12 -Compress }
}

# These are explicit package wire-contract fixtures, not live Windows/MXC proof.
foreach ($capability in @($false, 'true', 1, $null)) {
    Assert-Throws {
        Assert-NativeSessionProbe (Result @{ probes = @{ isolationSessionAvailable = $capability } })
    } 'unavailable or unverified'
}
Assert-Throws { Assert-NativeSessionProbe (Result @{ probes = @{ isolationSessionAvailable = $true } } 7) } 'command failed'
Assert-Throws { Assert-NativeSessionProbe @{ exitCode = 0; output = 'not-json-secret-value' } } 'invalid JSON'
Assert-Throws { Assert-NativeSessionProbe (Result @()) } 'must be an object'
Assert-NativeSessionProbe (Result @{ tier = 'none'; probes = @{ isolationSessionAvailable = $true } })
$script:passed++

$packagingSha = 'a' * 40
$version = @{
    ok = $true; schemaVersion = 1; command = 'version'
    package = @{ version = '2026.10.20.0'; commit = $packagingSha }
    payload = @{ version = '2026.10.20'; commit = ('b' * 40) }
}
$build = Get-NativeGatewayBuildIdentity (Result $version) $packagingSha
Assert-True ($build.payloadSha -ceq ('b' * 40)) 'Payload identity must come from the installed CLI.'
Assert-Throws { Get-NativeGatewayBuildIdentity (Result $version) ('c' * 40) } 'build identity'
$version.payload.commit = 'unknown'
Assert-Throws { Get-NativeGatewayBuildIdentity (Result $version) $packagingSha } 'build identity'

function Invoke-Fixture([string]$Fault = '') {
    $state = @{ calls = [Collections.Generic.List[string]]::new(); status = 0; gatewayStatus = 0; fault = $Fault }
    $serialize = ${function:Result}
    $invoke = {
        param([string[]]$Arguments)
        $command = ($Arguments | Where-Object { $_ -notin @('--json', '--force', '--port', '18789') }) -join ' '
        $state.calls.Add(($Arguments -join ' '))
        $response = @{ ok = $true; schemaVersion = 1; command = $command; integration = @{ kind = 'isolated-session'; version = 1 } }
        switch ($command) {
            'status' {
                $state.status++
                $response.session = @{ state = 'not-configured' }
                $response.gateway = @{ state = 'not-started' }
                if ($state.fault -eq 'preexisting' -or ($state.fault -eq 'teardown-state' -and $state.status -gt 1)) {
                    $response.session.state = 'running'
                }
                if ($state.fault -eq 'legacy') { $response.integration.kind = 'legacy' }
                if ($state.fault -eq 'schema') { $response.schemaVersion = 2 }
                if ($state.fault -eq 'string-schema') { $response.schemaVersion = '1' }
            }
            'setup' { $response.session = @{ state = 'ready'; sandboxId = 'proof-session' } }
            'companion prepare' { $response.companion = @{ port = 18789; token = 'test-auth-token' } }
            'gateway-service status' {
                $state.gatewayStatus++
                $response.gateway = @{
                    state = 'running'; port = 18789
                    ownership = @{
                        sandboxId = 'proof-session'; agentUserSid = 'S-1-5-21-100-200-300-400'
                        listeners = @(@{
                            port = 18789; processId = 100 + $state.gatewayStatus
                            sequenceNumber = 200 + $state.gatewayStatus
                            processStartTimeUtc = '2026-10-20T12:00:00Z'
                        })
                    }
                }
                if ($state.fault -eq 'foreign-session') { $response.gateway.ownership.sandboxId = 'unrelated-session' }
                if ($state.fault -eq 'no-listeners') { $response.gateway.ownership.listeners = @() }
                if ($state.fault -eq 'string-process') { $response.gateway.ownership.listeners[0].processId = '101' }
                if ($state.fault -eq 'unchanged-restart') {
                    $response.gateway.ownership.listeners[0].processId = 101
                    $response.gateway.ownership.listeners[0].sequenceNumber = 201
                }
                if ($state.calls.Contains('gateway-service stop --json')) {
                    $response.gateway = @{ state = if ($state.fault -eq 'stop-state') { 'running' } else { 'stopped' } }
                }
            }
        }
        if ($state.fault -eq 'setup-failed' -and $command -eq 'setup') { $response.ok = $false }
        if ($state.fault -eq 'token-missing' -and $command -eq 'companion prepare') { $response.companion.token = '' }
        if ($state.fault -eq 'start-failed' -and $command -eq 'gateway-service start') { return & $serialize $response 1 }
        if ($state.fault -eq 'teardown-failed' -and $command -eq 'teardown') { return & $serialize $response 1 }
        if ($state.fault -eq 'secret-output' -and $command -eq 'setup') { throw 'never-print-this-secret' }
        return & $serialize $response
    }.GetNewClosure()
    try {
        $operations = @(Invoke-NativeGatewayLifecycle -InvokeCommand $invoke)
        return @{ calls = @($state.calls); operations = $operations; error = $null }
    } catch {
        return @{ calls = @($state.calls); operations = @(); error = $_.Exception.Message }
    }
}

$good = Invoke-Fixture
$expected = @(
    'status --json', 'setup --json', 'companion prepare --port 18789 --json',
    'gateway-service start --json', 'gateway-service status --json',
    'gateway-service restart --json', 'gateway-service status --json',
    'gateway-service stop --json', 'gateway-service status --json',
    'teardown --force --json', 'status --json'
)
Assert-True (-not $good.error) "Valid lifecycle failed: $($good.error)"
Assert-True (($good.calls -join '|') -ceq ($expected -join '|')) 'Proof must use the real package CLI lifecycle in order, including cleanup.'
Assert-True (($good.operations -join '|') -ceq 'setup|companion prepare|start/status|restart/status|stop/status|teardown/status') 'Every successful lifecycle stage must be reported.'
Assert-True (($good | ConvertTo-Json -Depth 5) -notmatch 'test-auth-token|proof-session|S-1-5-21') 'Evidence must not contain package secrets or user/session identities.'

foreach ($fault in @('preexisting', 'legacy', 'schema', 'string-schema')) {
    $result = Invoke-Fixture $fault
    Assert-True ([bool]$result.error -and $result.calls.Count -eq 1) "$fault must fail before setup or cleanup."
}
foreach ($fault in @('setup-failed', 'token-missing', 'start-failed', 'foreign-session', 'no-listeners', 'string-process', 'unchanged-restart', 'stop-state', 'teardown-failed', 'teardown-state', 'secret-output')) {
    $result = Invoke-Fixture $fault
    Assert-True ([bool]$result.error) "$fault must fail, not skip or claim proof."
    Assert-True ($result.calls -contains 'teardown --force --json') "$fault must attempt task-owned session cleanup."
    Assert-True ($result.error -notmatch 'never-print-this-secret|test-auth-token') 'Failures must not expose captured package output.'
}

$runner = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-NativeGatewayProof.ps1') -Raw
Assert-True ($runner.Contains('Get-ProofSourceIdentity') -and $runner.Contains('$source.dirtyFileCount -ne 0')) 'Cloud proof must bind to clean exact source.'
Assert-True ($runner.Contains('Add-AppxPackage -Path $MsixPath') -and $runner.Contains('Microsoft\WindowsApps\$($installed.PackageFamilyName)\clawctl.exe')) 'Proof must install the verified package and use its qualified alias.'
Assert-True ($runner -notmatch 'AllowSkip|winget install|Import-Certificate|wsl\.exe') 'Native proof must not change trust or fall back to another runtime.'

$processResult = Invoke-NativeProofProcess -FilePath (Get-Command pwsh).Source `
    -Arguments @('-NoProfile', '-Command', '[Console]::Out.Write(''{"ok":true}''); [Console]::Error.Write(''do-not-export-stderr''); exit 7') `
    -TimeoutSeconds 10
Assert-True ($processResult.exitCode -eq 7 -and $processResult.output -ceq '{"ok":true}') 'Process adapter must retain exit status and keep stderr out of response evidence.'
$processResult = Invoke-NativeProofProcess -FilePath (Get-Command pwsh).Source `
    -Arguments @('-NoProfile', '-Command', '[Console]::Out.Write([Console]::In.ReadToEnd())') `
    -StandardInput 'test-auth-token' -TimeoutSeconds 10
Assert-True ($processResult.exitCode -eq 0 -and $processResult.output -ceq 'test-auth-token') 'Process adapter must close stdin and return exactly one response object.'
Assert-Throws {
    Invoke-NativeProofProcess -FilePath (Get-Command pwsh).Source -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 10') -TimeoutSeconds 1
} 'timed out'
Write-Host "Native Gateway proof contracts: $script:passed passed (fixtures only, no native runtime)."
$global:LASTEXITCODE = 0
