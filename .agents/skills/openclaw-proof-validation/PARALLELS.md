# Parallels Windows proof backend

Use this optional backend only from macOS when Parallels Desktop is installed and activated and a
Windows 11 VM has already been downloaded. Run commands from the `openclaw-windows-node` repo root.

The sibling OpenClaw repo owns the general Windows VM lifecycle. Read
`../../../../openclaw/.agents/skills/openclaw-parallels-smoke/SKILL.md` for `prlctl` transport,
WSL/Git/Node provisioning, clean/E2E snapshots, OpenClaw smoke, and general troubleshooting. Keep
the `openclaw` and `openclaw-windows-node` checkouts beside each other, or set `OPENCLAW_REPO`.

## Prepare the app layer

```bash
./scripts/parallels-windows-vm.sh inventory
./scripts/parallels-windows-vm.sh prepare
./scripts/parallels-windows-vm.sh verify
```

The wrapper restores the newest general `e2e` snapshot, then adds only the native app prerequisites:

1. .NET 10 SDK.
2. Windows SDK 10.0.26100.
3. WebView2 Runtime.
4. A clean `openclaw-windows-node` checkout and `scripts/setup-dev.ps1 -CheckOnly`.
5. A dated power-off `pre-openclaw-windows-app-e2e-*` snapshot.

When today's app snapshot exists, `prepare` restores and verifies it. This discards post-snapshot
guest changes. Package installation reuses the OpenClaw controller's official WinGet manifest hash,
Authenticode publisher, ACL-restricted staging, reboot handling, and bounded transport.

## Restore snapshots

- `clean`: newest raw Windows snapshot.
- `e2e`: newest general OpenClaw Windows snapshot.
- `app`: newest native Windows app snapshot.

```bash
./scripts/parallels-windows-vm.sh restore --snapshot app
./scripts/parallels-windows-vm.sh restore --snapshot "pre-openclaw-windows-app-e2e-<date>"
```

Never restore while another developer or smoke lane owns the VM. Use an exact name or id for
historical reproduction; aliases select the newest matching snapshot.

## Run required validation

```bash
./scripts/parallels-windows-vm.sh run-tests
./scripts/parallels-windows-vm.sh run-tests --ref <pushed-branch-or-full-sha>
```

The default restores `app`. The controller copies the current
`scripts/parallels-run-validation.ps1` into guest temp, launches it in the desktop session, polls
with short host-bounded `prlctl exec` calls, stops the process tree at 90 minutes, and runs:

- `./build.ps1`
- Shared tests
- Tray tests

For an unpushed reviewed change, restore `app`, sync an isolated guest checkout, then use
`run-tests --no-restore`. Never use `--no-restore` as clean-snapshot evidence.

Continue with the proof checklist in [SKILL.md](SKILL.md) when the change needs screenshots/video,
`winnode` or raw MCP output, Gateway invocation, accessibility, permissions, Command Center,
chat/canvas, or MXC evidence.

## Remote execution rules

- Use `--current-user` for checkout, app launch, tests, Git, and user state.
- Use SYSTEM only for machine installers through the shared verified staging path.
- Use explicit `.cmd` shims for `npm`, `pnpm`, and `openclaw` when resolution is ambiguous.

## Troubleshooting

- Missing .NET, Windows SDK, or WebView2: rerun `prepare`; do not add app prerequisites to the
  general `e2e` snapshot.
- Missing `app` snapshot: run `prepare`; an older general E2E snapshot does not contain the native
  app layer.
- Locked build output: stop the companion/WinUI process, restore `app`, and rerun all required
  suites.
- Missing sibling controller/API: use an OpenClaw revision containing
  `scripts/e2e/parallels-windows-prepare.sh`.
