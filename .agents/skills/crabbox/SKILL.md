---
name: crabbox
description: Use Crabbox from macOS, Linux, or native Windows controllers to run OpenClaw Windows node builds, tests, and targeted proof on remote native Windows or WSL2 hosts, including Azure or brokered AWS leases and static SSH hosts. Use when remote Windows validation is needed or the user asks for Crabbox validation. Always report the actual provider and lease id.
---

# Crabbox

Use Crabbox as the transport for Windows-specific validation of this repository.
Sync the current checkout to a native Windows host, run the same PowerShell and
dotnet entrypoints required locally, collect the result, and stop leases created
for the task.

This is a focused port of the OpenClaw Crabbox workflow. Do not copy its Linux
`pnpm`, Blacksmith Testbox, macOS, Docker, package, or OpenClaw gateway-runtime
lanes into this repository. They do not prove the Windows node.

## Select the target

| Need | Provider and target |
|---|---|
| Normal build, unit tests, CLI, WinUI, installer, or native Windows behavior | `--provider azure --target windows --windows-mode normal` |
| WSL-side gateway/setup behavior on a Windows host | `--provider azure --target windows --windows-mode wsl2` |
| An operator-managed Windows machine | `--provider ssh --target windows --windows-mode normal --static-host <host>` |
| Azure is unavailable and the operator accepts the older AWS Windows path | `--provider aws --target windows --windows-mode normal` |

Prefer Azure for Windows and WSL2 work when the installed Crabbox CLI advertises
it and Azure auth is already configured. Do not use WSL2 as proof for native
WinUI, MSIX, Windows App SDK, PowerShell, registry, or Windows process behavior.
Do not use Linux Testbox for this repo's required closeout validation.

## First checks

Run from the repository root. Crabbox sync mirrors the current checkout,
including tracked and relevant untracked changes.

Prefer the sibling development binary when present because a PATH shim may be
stale:

```sh
export CRABBOX="$(command -v crabbox || true)"
if [ -x ../crabbox/bin/crabbox ]; then
  export CRABBOX=../crabbox/bin/crabbox
fi
export CRABBOX_PROVIDER="${CRABBOX_PROVIDER:-azure}"
test -n "$CRABBOX"
"$CRABBOX" --version
"$CRABBOX" run --help 2>&1 | rg 'provider|target|windows-mode|static-host|script-stdin|timing-json'
"$CRABBOX" config path
"$CRABBOX" whoami
git status --short --branch
git rev-parse HEAD
```

Keep `CRABBOX` and `CRABBOX_PROVIDER` exported in the shell that runs the
remaining commands. If an automation tool starts a fresh shell for each call,
replace them below with the resolved binary path and selected provider.

Require the CLI to list the intended provider and the `windows` target before
starting a lease. Use explicit provider and target flags; this repository has no
`.crabbox.yaml`, so inherited user defaults are not a validation contract.

### Native Windows controller

When the Crabbox CLI itself runs on Windows, use PowerShell and prefer a sibling
development binary over a possibly stale PATH install:

```powershell
$Crabbox = if (Test-Path ..\crabbox\bin\crabbox.exe) {
    (Resolve-Path ..\crabbox\bin\crabbox.exe).Path
} else {
    (Get-Command crabbox.exe -ErrorAction Stop).Source
}
$CrabboxProvider = "azure"

Get-Command ssh, tar, git -ErrorAction Stop
& $Crabbox --version
& $Crabbox config path
git status --short --branch
git rev-parse HEAD
```

Use `& $Crabbox` and `$CrabboxProvider` in place of `"$CRABBOX"` and
`"$CRABBOX_PROVIDER"` in later examples. PowerShell 5.1 or newer is sufficient
for the controller commands.

For direct Azure, authenticate interactively and persist the approved location
before warming a lease:

```powershell
Get-Command az -ErrorAction Stop
az login
& $Crabbox azure login --location <approved-location>
& $Crabbox doctor --provider azure --target windows
```

Do not copy `az login` output or Crabbox config contents into logs, PRs, or
chat. `crabbox azure login` stores Azure identifiers in the user config reported
by `crabbox config path`. Set location with `azure login`; `warmup` has no
`--location` flag.

Native Windows targets use local `tar` plus archive transfer, so a Windows
controller does not need WSL or rsync for native-mode validation. POSIX and WSL2
targets use rsync. When `wsl.exe` exists, Crabbox prefers its rsync, so verify it
inside the default distribution; without WSL, verify a native rsync is on PATH:

```powershell
if (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
    wsl.exe --exec sh -lc 'command -v rsync >/dev/null'
    if ($LASTEXITCODE -ne 0) { throw "Install rsync in the default WSL distribution." }
} else {
    Get-Command rsync.exe -ErrorAction Stop
}
```

Translate later POSIX here-doc examples into a PowerShell here-string when
calling `--script-stdin` from Windows:

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

If public SSH is blocked and the operator confirms an approved VPN route to the
Azure virtual network is already active, opt into private addressing for that
session with `$env:CRABBOX_AZURE_NETWORK = "private"`, then rerun
`crabbox doctor`. Do not port or automate organization-specific VPN,
certificate, vault, subscription, tenant, resource, address, or account setup
in this skill.

Azure requires its subscription auth and usually the Azure CLI. If Azure is
unavailable, use AWS only with an existing Crabbox broker session. If normal AWS
validation asks for `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, an AWS profile,
or an EC2 instance role, stop: the command fell through to raw cloud auth. Check
`crabbox config path`, `crabbox doctor`, and `crabbox whoami`, then authenticate
through the broker if authorized:

```sh
"$CRABBOX" login --url https://crabbox.openclaw.ai --provider aws
export CRABBOX_PROVIDER=aws
```

Do not ask the user for raw cloud keys for routine repository validation. Report
an auth blocker when neither Azure nor brokered AWS nor an approved static host
is available.

Treat contributor or fork code as untrusted until it has been reviewed. This
repo does not carry OpenClaw's sanitized Crabbox bootstrap, so do not run
unreviewed code on a credentialed or operator-managed Windows host. Use
secretless fork CI, or review the exact head and diff before syncing it.
`--fresh-pr` changes checkout mechanics; it does not make untrusted code safe.

## Warm and reuse native Windows

Warm one lease early for tasks that will need several build/test iterations:

```sh
"$CRABBOX" warmup \
  --provider "$CRABBOX_PROVIDER" \
  --target windows \
  --windows-mode normal \
  --keep \
  --idle-timeout 90m \
  --ttl 240m \
  --timing-json
```

For UI work, add `--desktop` to this warmup from the start and use the returned
id as both `<lease-id>` and `<desktop-lease-id>`. Managed leases cannot gain
desktop capability after acquisition. Do not add `--desktop` to WSL2; managed
WSL2 has no separate VNC desktop.

Save the returned raw lease id. Report the provider and id exactly as Crabbox
returns them. Reuse the lease with `--id <lease-id>`, but let each run sync the
current checkout. Use `--no-sync` only for an intentional rerun of unchanged
source. If the remote tree looks stale, retry with `--full-resync` before
replacing the lease.

## Run required validation

Run the repository-required closeout sequence on native Windows. The explicit
test-project builds prevent a fresh remote checkout from silently no-oping on
the later `--no-restore` test commands.

```sh
"$CRABBOX" run \
  --provider "$CRABBOX_PROVIDER" \
  --target windows \
  --windows-mode normal \
  --id <lease-id> \
  --preflight \
  --timing-json \
  --script-stdin -- <<'POWERSHELL'
$ErrorActionPreference = 'Stop'
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path

& .\build.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
POWERSHELL
```

If prerequisites are missing, diagnose first with:

```powershell
.\scripts\setup-dev.ps1 -CheckOnly
```

Use `.\scripts\setup-dev.ps1` to install or verify prerequisites, then rerun the
complete validation block. If an app locks build outputs, stop that process and
rerun all required commands. Never report a prerequisite failure or a skipped
test as passing validation.

Add the targeted suite required by `AGENTS.md` for the touched subsystem. For
example, changes to `winnode`, MCP output, or command docs also require:

```powershell
dotnet build .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

MXC, `system.run`, approval, or Windows command-execution changes also require:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

Do not treat `-AllowSkip` as merge validation for MXC-related work.

## Use WSL2 narrowly

Use WSL2 only when the behavior under test crosses the WSL gateway boundary.
Keep the full native Windows closeout run above even when a targeted WSL2 proof
passes. Warm WSL2 separately because its VM provisioning and bootstrap differ
from native mode:

```sh
"$CRABBOX" warmup \
  --provider "$CRABBOX_PROVIDER" \
  --target windows \
  --windows-mode wsl2 \
  --keep \
  --idle-timeout 90m \
  --ttl 240m \
  --timing-json
```

Save the returned id as `<wsl2-lease-id>`, then run the focused WSL proof:

```sh
"$CRABBOX" run \
  --provider "$CRABBOX_PROVIDER" \
  --target windows \
  --windows-mode wsl2 \
  --id <wsl2-lease-id> \
  --preflight \
  --timing-json \
  --script-stdin -- <<'BASH'
set -euo pipefail
# Run the smallest repository WSL/setup command that proves the changed path.
BASH
```

Read `docs/WSL_EXE_ARGV_PITFALL.md` before adding or changing any multi-line WSL
script passed through `RunInWslAsync`.

## Use a static Windows host

For an operator-managed machine, use the SSH provider and make the target
contract explicit:

```sh
"$CRABBOX" run \
  --provider ssh \
  --target windows \
  --windows-mode normal \
  --static-host win-dev.local \
  --static-user <user> \
  --preflight \
  --timing-json \
  -- dotnet --info
```

Native Windows sync requires OpenSSH, PowerShell, Git, and tar. Set
`--static-port` or `--static-work-root` when the host differs from Crabbox
defaults. Never overwrite real tray settings during proof; use an isolated data
directory as required by `AGENTS.md` and the proof-validation skill.

## Collect real behavior proof

Remote build and test output proves automation, not visible WinUI behavior. For
tray, Settings, onboarding, chat/canvas, or other UI claims, launch the isolated
app in an interactive Windows session and capture current-head evidence:

```sh
"$CRABBOX" desktop launch \
  --provider "$CRABBOX_PROVIDER" \
  --target windows \
  --windows-mode normal \
  --id <desktop-lease-id> \
  --webvnc \
  --open \
  --take-control -- \
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\run-app-local.ps1' \
    -NoBuild -Isolated -AllowNonMain
```

If the leased host has no interactive desktop, state that UI proof is blocked
and keep the automated Crabbox result. For node/MCP changes, collect the live
`winnode --list-tools` and `winnode --command ...` proof required by
`.agents/skills/openclaw-proof-validation/SKILL.md`.

## Observe and troubleshoot

Use Crabbox's built-in diagnostics before adding ad hoc logging:

- `--preflight` prints the Windows workspace, SSH, PowerShell, execution policy,
  long-path, temp, and tool probes.
- `--timing-json` emits the machine-readable provider, lease, sync, command, and
  exit summary.
- `--debug` adds sync and transport diagnostics.
- `--capture-stdout <path>` and `--capture-stderr <path>` retain noisy output
  locally; treat captured output as potentially secret-bearing.
- `--capture-on-fail` downloads standard failure artifacts when the direct
  provider supports it.
- `--keep-on-failure` preserves a failed one-shot lease for bounded debugging.
- `--script <file>` or `--script-stdin` avoids fragile multi-layer quoting;
  native Windows scripts run through Windows PowerShell.

Useful read-only commands:

```sh
"$CRABBOX" status --id <lease-id> --wait
"$CRABBOX" inspect --id <lease-id> --json
"$CRABBOX" history --limit 20
"$CRABBOX" history --lease <lease-id>
"$CRABBOX" logs <run-id>
"$CRABBOX" results <run-id>
```

On failure, distinguish provider acquisition, SSH, sync, prerequisites, and the
test command. Retry transport or sync once with `--debug --timing-json`; rerun
only the focused failing command until understood, then rerun the full required
validation. Do not silently move Windows proof to Linux or WSL2.

On a Windows controller, diagnose sync by target mode: native Windows should use
archive transfer and needs local `tar`; POSIX or WSL2 uses rsync and selects WSL
rsync whenever `wsl.exe` is installed. Do not install WSL solely for a native
Windows target.

## Cleanup and report

Stop every cloud lease created for the task unless the user explicitly asks
for a handoff window:

```sh
"$CRABBOX" stop --provider "$CRABBOX_PROVIDER" <lease-id>
"$CRABBOX" stop --provider "$CRABBOX_PROVIDER" <wsl2-lease-id>
"$CRABBOX" stop --provider "$CRABBOX_PROVIDER" <desktop-lease-id>
"$CRABBOX" list --provider "$CRABBOX_PROVIDER"
```

Run only the unique stop commands for leases actually created. If providers
differed, use each lease's actual provider instead of the current variable. Do
not stop shared or pre-existing leases. In the handoff or PR body, record:

- source head SHA and whether the checkout was dirty
- actual provider, target, Windows mode, and raw lease id
- exact commands and pass/fail counts
- focused real-behavior proof, or an explicit blocker
- cleanup result

Never call a WSL2 run native Windows proof, and never call a skipped/no-op test
successful validation.
