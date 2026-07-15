---
name: openclaw-parallels-windows
description: Prepare, inspect, restore, and test the OpenClaw Windows companion and Windows node in a Parallels Windows 11 VM controlled from macOS. Use for first-time VM baseline provisioning, snapshot lifecycle, remote prlctl management, WSL/dev-tool setup, required Windows validation, or Parallels/WSL/winget transport troubleshooting.
---

# OpenClaw Parallels Windows

Use `scripts/parallels-windows-vm.sh` as the canonical host-side controller. Assume Parallels
Desktop is installed and activated and the user has downloaded/created a Windows 11 VM. Do not
reimplement provisioning with ad hoc `prlctl` commands unless debugging the controller itself.

## Quick start

Run from the macOS checkout of `openclaw-windows-node`:

```bash
./scripts/parallels-windows-vm.sh inventory
./scripts/parallels-windows-vm.sh prepare
./scripts/parallels-windows-vm.sh verify
```

`prepare` is idempotent. It:

1. Inventories the named VM and existing snapshots.
2. Requires a logged-in desktop user available through `prlctl exec --current-user`.
3. Refuses a reusable baseline if OpenClaw CLI/app/process/tray state or any WSL distro exists.
4. Creates a dated clean-OS snapshot only when no reusable prerequisites are installed.
5. Enables the WSL and Virtual Machine Platform features and reboots when needed.
6. Installs the latest signed Microsoft WSL MSI for the guest architecture and sets WSL 2 as default.
7. Resolves the current package version and SHA-256 from Microsoft's official WinGet manifest
   repository, downloads that exact version with `winget`, then verifies both the trusted manifest
   hash and expected Authenticode publisher while staging into a freshly ACL-restricted directory.
   Only then does it run system-context silent installers for Git, Node/npm, .NET 10, Windows SDK
   10.0.26100, and WebView2. This avoids hidden UAC prompts without trusting a user-writable file.
8. Clones this repository, runs `scripts/setup-dev.ps1 -CheckOnly`, clears installers, reboots,
   verifies clean state/no pending reboot, and creates the reusable power-on snapshot.

Override names or paths when needed:

```bash
./scripts/parallels-windows-vm.sh prepare \
  --vm "Windows 11" \
  --clean-snapshot "windows-11-clean-os-<date>" \
  --baseline-snapshot "pre-openclaw-native-e2e-<date>"
```

## Snapshot lifecycle

Always inventory before mutation:

```bash
./scripts/parallels-windows-vm.sh inventory
prlctl snapshot-list "Windows 11" --json
```

Restore by exact name or id:

```bash
./scripts/parallels-windows-vm.sh restore --snapshot e2e
# Exact names and ids are also accepted:
./scripts/parallels-windows-vm.sh restore --snapshot "pre-openclaw-native-e2e-<date>"
```

`e2e` selects the newest `pre-openclaw-native-e2e-*` snapshot by snapshot date; `clean` selects the
newest `windows-11-clean-os-*`. Use an exact name or id when reproducing against an older baseline.

Restoring discards all post-snapshot guest changes. Do not restore while another Windows lane or
developer session owns the VM. Preserve the power-on snapshot's logged-in session by switching
normally; do not pass `--skip-resume` at test entry. Stop the VM after ad hoc or credentialed runs
when no follow-up work needs the desktop session.

Create additional snapshots only when no suitable baseline exists or the user explicitly asks.
Use a new dated name; never overwrite or silently delete a known-good baseline.

## Run Windows app validation

The default run restores the newest `e2e` baseline. Pass an exact snapshot to pin an older date:

```bash
./scripts/parallels-windows-vm.sh run-tests
./scripts/parallels-windows-vm.sh run-tests \
  --snapshot "pre-openclaw-native-e2e-<date>"
```

To validate a pushed branch or SHA, fetch it into the guest and detach at `FETCH_HEAD`:

```bash
./scripts/parallels-windows-vm.sh run-tests \
  --snapshot "pre-openclaw-native-e2e-<date>" \
  --ref "<branch-or-full-sha>"
```

For an unpushed local change, restore the baseline first, copy or fetch the reviewed files into an
isolated guest checkout, then preserve that checkout for the run:

```bash
./scripts/parallels-windows-vm.sh restore \
  --snapshot "pre-openclaw-native-e2e-<date>"
# Sync the local ref into the guest checkout.
./scripts/parallels-windows-vm.sh run-tests --no-restore
```

`--no-restore` is an explicit escape hatch for a checkout the developer just synchronized. Never
use it for a clean-snapshot proof or when the guest's ownership/state is unknown.

The controller copies `scripts/parallels-run-validation.ps1` from the host into the guest temp
directory, so it works even when the restored snapshot predates the helper. It launches the helper
in the desktop session, polls a done file through host-bounded `prlctl exec` calls, emits progress,
hard-caps the run at 90 minutes, stops the worker on timeout, and runs the repository-required
build, Shared tests, and Tray tests through `setup-dev.ps1 -RunValidation`. The guest log path is
printed on completion.

Use `.agents/skills/openclaw-proof-validation/SKILL.md` after the required suites when the change
also needs UI screenshots, local MCP/`winnode`, Gateway, accessibility, or MXC proof.

## Run OpenClaw core smoke

From a sibling `openclaw` checkout, use its wrapper so the same baseline owner remains canonical:

```bash
pnpm test:parallels:windows:prepare -- inventory
pnpm test:parallels:windows:prepare -- verify
gtimeout --foreground 90m pnpm test:parallels:windows -- \
  --snapshot-hint "pre-openclaw-native-e2e-<date>" \
  --json
```

Install GNU coreutils on macOS when neither `timeout` nor `gtimeout` exists. Keep the host hard cap;
the lane also has phase timeouts, but they do not protect a stalled `prlctl` process.

## Remote management rules

- Put the VM name before `--current-user`: `prlctl exec "$VM" --current-user ...`.
- Use `--current-user` for OpenClaw, `winget`, Git, app launch, tests, and user state. Plain
  `prlctl exec` runs as `NT AUTHORITY\\SYSTEM`; reserve it for DISM and machine installers.
- Wrap multi-argument Windows commands with `cmd.exe /d /s /c '<command>'`; direct App Execution
  Alias calls can lose arguments or detach without useful output.
- Use explicit `.cmd` shims for `npm`, `pnpm`, and `openclaw` when command resolution is ambiguous.
- Keep long guest work behind a background runner plus log/done files. One long-lived encoded
  PowerShell transport can hang after the guest process already completed.
- Inspect `prlctl status`, then resume/start a suspended or stopped guest before retrying `rc=255`.
- Do not print environment dumps or credentials. Inject only the provider/channel secret required
  for a live lane and restore the clean snapshot afterward.

## Troubleshooting

- **`wsl.exe` says WSL is not installed after features are enabled:** the Store/MSI package is
  missing. Rerun `prepare`; it resolves the current Microsoft/WSL release, validates the
  Authenticode signature, and installs the matching ARM64/x64 MSI.
- **WSL default returns to 1 after install/reboot:** run `wsl.exe --set-default-version 2`; `prepare`
  and `verify` enforce this before snapshot creation.
- **WSL2 cannot start:** inspect Parallels VM CPU settings and Windows virtualization errors. On
  supported hosts, enable nested virtualization, reboot, and rerun the smallest WSL preflight.
- **`winget` prints nothing or waits forever:** invoke it through `cmd.exe /d /s /c`. For packages
  needing elevation, use the controller's download-as-user/install-as-system flow instead of UAC.
- **Snapshot restore reports incompatible saved CPU state:** create a power-off replacement
  snapshot from the known-good disk state, then use that exact name. The test harness starts
  restored power-off snapshots automatically.
- **Snapshot list is empty:** run `prepare`; do not bypass restore with
  `OPENCLAW_PARALLELS_SKIP_SNAPSHOT_RESTORE=1` for a two-lane fresh+upgrade claim.
- **Baseline verification finds OpenClaw state:** restore the clean-OS snapshot or remove the
  product state deliberately. Do not bless a dirty guest as the reusable baseline.
- **Windows installer appears idle:** inspect `tasklist` and the installer/MSI log in
  `C:\Windows\Temp`; Windows SDK and npm installs can remain quiet while healthy.
- **`prlctl exec --current-user` cannot authenticate:** confirm the VM has a logged-in desktop user
  and Parallels Tools is installed. A stopped login-screen VM is not a usable power-on baseline.
