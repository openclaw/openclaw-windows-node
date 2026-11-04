---
name: crabbox
description: "Run OpenClaw Windows node builds, tests, and proof on remote Crabbox leases (Azure, brokered AWS, or static SSH; native Windows or WSL2) when the user asks for remote or Crabbox validation. Report provider, lease id, run URL, command, and result."
---

# Crabbox

Use Crabbox as the transport for Windows-specific validation of this repository.
Sync the current checkout to a native Windows host, run the same PowerShell and
dotnet entrypoints used locally, collect the result, and stop leases created for
the task.

Do not port OpenClaw's Linux `pnpm`, Blacksmith Testbox, macOS, Docker, package,
or gateway-runtime lanes into this repository. They do not prove the Windows
node. Controller, Azure login, brokered AWS, sync transport, Actions hydration,
and Windows here-string recipes are in
[references\controllers-and-providers.md](references/controllers-and-providers.md).

## Select the target

| Need | Provider and target |
|---|---|
| Build, unit tests, CLI, WinUI, installer, or native Windows behavior | `--provider azure --target windows --windows-mode normal` |
| WSL-side gateway/setup behavior on a Windows host | `--provider azure --target windows --windows-mode wsl2` |
| An operator-managed Windows machine | `--provider ssh --target windows --windows-mode normal --static-host <host>` |
| Azure is unavailable and the operator accepts the older AWS Windows path | `--provider aws --target windows --windows-mode normal` |

Prefer Azure when the installed CLI advertises it and Azure auth is already
configured. WSL2 does not prove native WinUI, MSIX, Windows App SDK,
PowerShell, registry, or Windows process behavior. Do not use Linux Testbox for
this repository's validation.

Routine use of an already configured Crabbox environment is part of completing
requested remote validation. This includes task-owned leases, temporary state,
and clean task-owned checkouts or worktrees. Preserve user-managed checkouts,
branches, unrelated edits, trust boundaries, credentials, production access,
budgets, and publication controls. A dirty, missing, or occupied checkout is a
reason to create an isolated task-owned checkout, not to overwrite it.

## First checks

Run from the repository root. Crabbox sync mirrors the current checkout,
including tracked and relevant untracked changes.

Use an installed, verified Crabbox binary. Do not assume a sibling source
checkout is trusted or overwrite its unrelated work. When the task is to test or
repair Crabbox itself, verify its canonical upstream and build it from a clean
task-owned checkout.

```sh
export CRABBOX="$(command -v crabbox)"
export CRABBOX_PROVIDER="${CRABBOX_PROVIDER:-azure}"
test -n "$CRABBOX"
"$CRABBOX" --version
"$CRABBOX" run --help 2>&1 | rg 'provider|target|windows-mode|static-host|script-stdin|timing-json'
"$CRABBOX" config path
"$CRABBOX" whoami
git status --short --branch
git rev-parse HEAD
```

If each tool call starts a fresh shell, substitute the resolved binary path and
provider below. Require the CLI to list the intended provider and the `windows`
target before starting a lease. Use explicit provider and target flags; this
repository has no `.crabbox.yaml`, so inherited user defaults are not a
validation contract.

Do not copy `az login` output or Crabbox config contents into logs, PRs, or
chat. If normal AWS validation asks for `AWS_ACCESS_KEY_ID`,
`AWS_SECRET_ACCESS_KEY`, an AWS profile, or an EC2 instance role, stop: the
command fell through to raw cloud auth. Check `crabbox config path`,
`crabbox doctor`, and `crabbox whoami`, then use the broker login in the
reference file if authorized. Do not ask the user for raw cloud keys. Report an
auth blocker when neither Azure, brokered AWS, nor an approved static host is
available.

Treat contributor or fork code as untrusted until reviewed. This repository does
not carry OpenClaw's sanitized Crabbox bootstrap, so do not run unreviewed code
on a credentialed or operator-managed Windows host. Use secretless fork CI, or
review the exact head and diff before syncing it. `--fresh-pr` changes checkout
mechanics; it does not make untrusted code safe.

## Warm and reuse native Windows

Do not warm a lease speculatively. When the first remote command is ready, warm
one lease and reuse it for iterations:

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

For UI work, add `--desktop` to this warmup and use the returned id as both
`<lease-id>` and `<desktop-lease-id>`. Managed leases cannot gain desktop
capability after acquisition. Do not add `--desktop` to WSL2. Do not silently
change providers to gain desktop support; report the capability blocker.

Save the raw lease id. Report the provider, id, run URL, exact command, and
result exactly as Crabbox returns them. Reuse the lease with `--id <lease-id>`
and let each run sync the current checkout. Use `--no-sync` only for an
intentional rerun of unchanged source. If the remote tree looks stale, retry
with `--full-resync` before replacing the lease.

## Run validation

Choose the commands under the
[validation policy](../../../docs/TEST_COVERAGE.md#validation-policy), including
any targeted lane for the touched subsystem, and run them through one
`--script-stdin` adapter. On a fresh lease, build each test project before a
`--no-restore` test so the test does not silently no-op.

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
function Invoke-Step([scriptblock]$Step) {
    & $Step
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Replace these lines with the checks selected under the validation policy.
Invoke-Step { & .\build.ps1 }
Invoke-Step { dotnet build .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj }
Invoke-Step { dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore }
POWERSHELL
```

Diagnose missing prerequisites with `.\scripts\setup-dev.ps1 -CheckOnly`; run
`.\scripts\setup-dev.ps1` only on a task-owned lease, then rerun the selected
checks. If a process locks build outputs, stop only verified task-owned
processes; otherwise report a blocker. For MXC work,
`.\scripts\validate-mxc-e2e.ps1 -AllowSkip` is not merge validation. Never
report a prerequisite failure or a skipped test as passing.

## Use WSL2 narrowly

Use WSL2 only when the behavior crosses the WSL gateway boundary. WSL2 proof
does not replace native Windows proof. Warm WSL2 separately:

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

Save the id as `<wsl2-lease-id>` and run the smallest repository WSL/setup
command that proves the changed path with `--windows-mode wsl2 --id
<wsl2-lease-id> --script-stdin`. Read
[`docs\WSL_EXE_ARGV_PITFALL.md`](../../../docs/WSL_EXE_ARGV_PITFALL.md) before
changing any multi-line WSL script passed through `RunInWslAsync`.

## Use a static Windows host

For an operator-managed machine, make the target contract explicit:

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
defaults. Never overwrite real tray settings; use isolated data directories.

## Collect real behavior proof

Build and test output proves automation, not visible WinUI behavior. For UI
claims, launch the isolated app in an interactive session and capture
current-head evidence. Remote native scripts run through Windows PowerShell, so
UI-only proof uses the installed Store Gateway rather than building one:

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
    -NoBuild -Isolated -UseStoreGateway
```

Without `-UseStoreGateway`, `-Isolated` builds an npm-latest native Gateway
through a script that requires PowerShell 7.4. When proof needs a dev Gateway,
launch with `pwsh.exe` and prepare a task-owned patch as in
[native-gateway-from-source](../native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build).

If the host has no interactive desktop, report UI proof as blocked and keep the
automated result. For node/MCP changes, collect the `winnode` proof in
[openclaw-proof-validation](../openclaw-proof-validation/SKILL.md).

## Observe and troubleshoot

- `--preflight` prints workspace, SSH, PowerShell, execution policy, long-path,
  temp, and tool probes.
- `--timing-json` emits the provider, lease, sync, command, and exit summary.
- `--debug` adds sync and transport diagnostics.
- `--capture-stdout <path>` and `--capture-stderr <path>` retain noisy output
  locally; treat captured output as potentially secret-bearing.
- `--capture-on-fail` downloads standard failure artifacts when supported.
- `--keep-on-failure` preserves a failed one-shot lease for bounded debugging.
- `--script <file>` or `--script-stdin` avoids fragile quoting; native Windows
  scripts run through Windows PowerShell.

```sh
"$CRABBOX" status --id <lease-id> --wait
"$CRABBOX" inspect --id <lease-id> --json
"$CRABBOX" history --lease <lease-id>
"$CRABBOX" logs <run-id>
"$CRABBOX" results <run-id>
```

On failure, distinguish provider acquisition, SSH, sync, prerequisites, and the
test command. Retry transport or sync once with `--debug --timing-json`; rerun
only the focused failing command until understood, then rerun the selected
checks. Do not silently move Windows proof to Linux or WSL2.

## Cleanup and report

Stop every cloud lease created for the task unless the user explicitly asks for
a handoff window:

```sh
"$CRABBOX" stop --provider "$CRABBOX_PROVIDER" <lease-id>
"$CRABBOX" stop --provider "$CRABBOX_PROVIDER" <wsl2-lease-id>
"$CRABBOX" list --provider "$CRABBOX_PROVIDER"
```

Run only the unique stop commands for leases actually created, each with its
actual provider. Do not stop shared or pre-existing leases. `crabbox stop` does
not accept `--timing-json`. In the handoff or PR body, record:

- source head SHA and whether the checkout was dirty
- actual provider, target, Windows mode, raw lease id, and run URL
- exact commands and pass/fail counts
- focused real-behavior proof, or an explicit blocker
- cleanup result

Never call a WSL2 run native Windows proof, and never call a skipped or no-op
test successful validation.
