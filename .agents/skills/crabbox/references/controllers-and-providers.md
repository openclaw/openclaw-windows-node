# Crabbox controller and provider recipes

Read this only when [SKILL.md](../SKILL.md) needs a controller, provider, or
transport detail. It does not change the skill's cost, identity, or cleanup
rules.

## Native Windows controller

When the Crabbox CLI runs on Windows, use PowerShell and verify the installed
binary:

```powershell
$Crabbox = (Get-Command crabbox.exe -ErrorAction Stop).Source
$CrabboxProvider = "azure"

Get-Command ssh, tar, git -ErrorAction Stop
& $Crabbox --version
& $Crabbox config path
git status --short --branch
git rev-parse HEAD
```

Use `& $Crabbox` and `$CrabboxProvider` in place of `"$CRABBOX"` and
`"$CRABBOX_PROVIDER"` in the skill's examples. PowerShell 5.1 or newer is
sufficient for controller commands.

## Direct Azure login

Authenticate interactively and persist the approved location before warming a
lease:

```powershell
Get-Command az -ErrorAction Stop
az login
& $Crabbox azure login --location <approved-location>
& $Crabbox doctor --provider azure --target windows
```

`crabbox azure login` stores Azure identifiers in the user config reported by
`crabbox config path`. Set location with `azure login`; `warmup` has no
`--location` flag.

If public SSH is blocked and the operator confirms an approved VPN route to the
Azure virtual network is already active, set
`$env:CRABBOX_AZURE_NETWORK = "private"` for that session, then rerun
`crabbox doctor`. Do not automate organization-specific VPN, certificate,
vault, subscription, tenant, resource, address, or account setup.

## Brokered AWS

Use AWS only with an existing Crabbox broker session, and only if authorized:

```sh
"$CRABBOX" login --url https://crabbox.openclaw.ai --provider aws
export CRABBOX_PROVIDER=aws
```

## Sync transport

Native Windows targets use local `tar` plus archive transfer, so a Windows
controller does not need WSL or rsync for native mode. POSIX and WSL2 targets
use rsync. When `wsl.exe` exists, Crabbox prefers its rsync, so verify it inside
the default distribution; without WSL, verify a native rsync is on PATH:

```powershell
if (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
    wsl.exe --exec sh -lc 'command -v rsync >/dev/null'
    if ($LASTEXITCODE -ne 0) { throw "Install rsync in the default WSL distribution." }
} else {
    Get-Command rsync.exe -ErrorAction Stop
}
```

Do not install WSL solely for a native Windows target.

## Actions hydration

Use Actions hydration only when the repository already owns a suitable
workflow. Local hydration supports WSL2 targets. Native Windows requires
`crabbox actions hydrate --github-runner`; WSL2 may also use that mode when the
workflow needs full GitHub Actions semantics. Otherwise use normal `warmup` and
`run` loops.

## `--script-stdin` from a Windows controller

Translate the skill's POSIX here-doc examples into a PowerShell here-string:

```powershell
$RemoteScript = @'
# Copy the exact POWERSHELL body from the relevant example here.
'@
$PreviousOutputEncoding = $OutputEncoding
try {
    $OutputEncoding = New-Object System.Text.UTF8Encoding -ArgumentList $false
    $RemoteScript | & $Crabbox run `
        --provider $CrabboxProvider `
        --target windows `
        --windows-mode normal `
        --id <lease-id> `
        --preflight `
        --timing-json `
        --script-stdin --
    $CrabboxExitCode = $LASTEXITCODE
} finally {
    $OutputEncoding = $PreviousOutputEncoding
}
if ($CrabboxExitCode -ne 0) { exit $CrabboxExitCode }
```
