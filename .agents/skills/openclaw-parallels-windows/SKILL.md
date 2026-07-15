---
name: openclaw-parallels-windows
description: Add OpenClaw Windows companion prerequisites, app-layer snapshots, native build/tests, and UI or node proof on top of the general Parallels Windows baseline owned by the sibling OpenClaw repo.
---

# OpenClaw Parallels Windows Companion

This is the native Windows supplement to
`../../../../openclaw/.agents/skills/openclaw-parallels-smoke/SKILL.md`. Read that skill first for VM
inventory, `prlctl` transport, WSL/Git/Node provisioning, clean/E2E snapshot lifecycle, OpenClaw
install/update smoke, and general troubleshooting. Do not duplicate those rules here.

Assume Parallels Desktop is installed and activated, a Windows 11 VM has been downloaded, and the
`openclaw` and `openclaw-windows-node` checkouts are siblings. Set `OPENCLAW_REPO` when they are not.
The wrapper intentionally fails closed when the sibling controller/API is absent; use an OpenClaw
revision containing `scripts/e2e/parallels-windows-prepare.sh` before running this app layer.

## Prepare the app layer

Run from the macOS `openclaw-windows-node` checkout:

```bash
./scripts/parallels-windows-vm.sh inventory
./scripts/parallels-windows-vm.sh prepare
./scripts/parallels-windows-vm.sh verify
```

The wrapper delegates the general baseline to
`../openclaw/scripts/e2e/parallels-windows-prepare.sh`, restores its newest `e2e` snapshot, then
adds only companion-owned prerequisites:

1. .NET 10 SDK.
2. Windows SDK 10.0.26100.
3. WebView2 Runtime.
4. A clean `openclaw-windows-node` checkout and `scripts/setup-dev.ps1 -CheckOnly`.
5. A dated power-off `pre-openclaw-windows-app-e2e-*` snapshot.

When today's app snapshot already exists, `prepare` restores and verifies it. Like any restore, this
discards post-snapshot guest changes.

Package installation reuses the OpenClaw controller's official WinGet manifest hash,
Authenticode publisher, ACL-restricted staging, reboot-code handling, and bounded transport
implementation. Keep generic package/security/transport changes in OpenClaw; keep these app package
choices and validation here.

## Snapshots

The shared aliases are:

- `clean`: newest raw Windows snapshot.
- `e2e`: newest general OpenClaw Windows snapshot.
- `app`: newest native Windows companion snapshot.

Restore deliberately because post-snapshot guest changes are discarded:

```bash
./scripts/parallels-windows-vm.sh restore --snapshot app
./scripts/parallels-windows-vm.sh restore --snapshot "pre-openclaw-windows-app-e2e-<date>"
```

Never restore while another developer or smoke lane owns the VM. Use exact names or ids for
historical reproduction; aliases select the newest matching snapshot by snapshot date.

## Run required Windows validation

```bash
./scripts/parallels-windows-vm.sh run-tests
./scripts/parallels-windows-vm.sh run-tests --ref <pushed-branch-or-full-sha>
```

The default restores `app`. The controller materializes `scripts/parallels-run-validation.ps1`
into guest temp independently of snapshot age, launches it in the desktop session, polls through
short host-bounded `prlctl exec` calls, stops the full process tree at 90 minutes, and runs:

- `./build.ps1`
- Shared tests
- Tray tests

For an unpushed reviewed change, restore `app`, sync an isolated guest checkout, then use
`run-tests --no-restore`. Never use `--no-restore` as clean-snapshot evidence.

## Native proof

After the required suites, use `.agents/skills/openclaw-proof-validation/SKILL.md` when the change
needs visible app screenshots/video, `winnode` or raw MCP proof, Gateway pairing/invocation,
accessibility, permissions, Command Center, chat/canvas, or MXC evidence.

Keep contexts straight:

- `--current-user`: checkout, app launch, tests, Git, and user state.
- SYSTEM: machine installers only, through the shared verified staging path.
- Explicit `.cmd` shims: `npm`, `pnpm`, and `openclaw` when command resolution is ambiguous.

## App-specific troubleshooting

- `setup-dev.ps1 -CheckOnly` reports .NET/SDK/WebView2 missing: rerun `prepare`; do not modify the
  general `e2e` snapshot to hide an app-layer prerequisite.
- `run-tests` cannot find an `app` snapshot: run `prepare`; existing older general E2E snapshots do
  not implicitly contain the native app layer.
- Build output is locked: stop the companion/WinUI process, restore `app`, and rerun all required
  suites.
- UI, MCP, Gateway, or MXC behavior needs proof beyond build/tests: route to
  `openclaw-proof-validation`; this skill only establishes the reusable VM and native closeout lane.
