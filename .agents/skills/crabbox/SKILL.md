---
name: crabbox
description: Use Crabbox to run OpenClaw Windows node builds, tests, and targeted proof on remote native Windows or WSL2 hosts, including Azure or brokered AWS leases and static SSH hosts. Use when remote Windows validation is needed, the local host is not Windows, or the user asks for Crabbox validation. Always report the actual provider and lease id.
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
it and Azure auth is already configured. Do not use WSL2 as proof for native WinUI, MSIX, Windows App SDK,
PowerShell, registry, or Windows process behavior. Do not use Linux Testbox for
this repo's required closeout validation.

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
test -n "$CRABBOX"
"$CRABBOX" --version
"$CRABBOX" run --help 2>&1 | rg 'provider|target|windows-mode|static-host|script-stdin|timing-json'
"$CRABBOX" config show
"$CRABBOX" whoami
git status --short --branch
git rev-parse HEAD
```

Keep `CRABBOX` exported in the shell that runs the remaining commands. If an
automation tool starts a fresh shell for each call, replace `"$CRABBOX"` below
with the resolved absolute binary path.

Require the CLI to list the intended provider and the `windows` target before
starting a lease. Use explicit provider and target flags; this repository has no
`.crabbox.yaml`, so inherited user defaults are not a validation contract.

Azure requires its subscription auth and usually the Azure CLI. If Azure is
unavailable, use AWS only with an existing Crabbox broker session. If normal AWS
validation asks for `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, an AWS profile,
or an EC2 instance role, stop: the command fell through to raw cloud auth. Check
`crabbox config show`, `crabbox doctor`, and `crabbox whoami`, then authenticate
through the broker if authorized:

```sh
"$CRABBOX" login --url https://crabbox.openclaw.ai --provider aws
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
  --provider azure \
  --target windows \
  --windows-mode normal \
  --keep \
  --idle-timeout 90m \
  --ttl 240m \
  --timing-json
```

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
  --provider azure \
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
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

MXC, `system.run`, approval, or Windows command-execution changes also require:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

Do not treat `-AllowSkip` as merge validation for MXC-related work.

## Use WSL2 narrowly

Use WSL2 only when the behavior under test crosses the WSL gateway boundary.
Keep the full native Windows closeout run above even when a targeted WSL2 proof
passes.

```sh
"$CRABBOX" run \
  --provider azure \
  --target windows \
  --windows-mode wsl2 \
  --id <lease-id> \
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
  -- pwsh -NoProfile -Command 'dotnet --info'
```

Native Windows sync requires OpenSSH, PowerShell, Git, and tar. Set
`--static-port` or `--static-work-root` when the host differs from Crabbox
defaults. Never overwrite real tray settings during proof; use an isolated data
directory as required by `AGENTS.md` and the proof-validation skill.

## Collect real behavior proof

Remote build and test output proves automation, not visible WinUI behavior. For
tray, Settings, onboarding, chat/canvas, or other UI claims, launch the isolated
app in an interactive Windows session and capture current-head evidence:

```powershell
.\run-app-local.ps1 -Isolated
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

## Cleanup and report

Stop every cloud lease created for the task unless the user explicitly asks
for a handoff window:

```sh
"$CRABBOX" stop --provider azure <lease-id>
"$CRABBOX" list --provider azure
```

Do not stop shared or pre-existing leases. In the handoff or PR body, record:

- source head SHA and whether the checkout was dirty
- actual provider, target, Windows mode, and raw lease id
- exact commands and pass/fail counts
- focused real-behavior proof, or an explicit blocker
- cleanup result

Never call a WSL2 run native Windows proof, and never call a skipped/no-op test
successful validation.
