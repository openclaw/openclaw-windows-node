# Prototype worktree reference

This Squad state lives in the prototype/reference worktree:

```text
C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node
```

Current prototype branch:

```text
pr-241-feedback-fixes
```

This worktree contains the validated WSL gateway prototype code, scripts, tests, docs, and artifacts. It is intentionally dirty and should be treated as **reference material**, not the final PR branch.

## How Squad should use this worktree

- Use this worktree to inspect the prototype implementation and copy/port selected ideas into the clean worktree.
- Do not clean, reset, revert, or delete prototype files unless the user explicitly requests it.
- Do not submit this prototype branch as the final PR.
- Create/implement final code in the clean sibling worktree once it exists:

  ```text
  C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean
  ```

- When launching a future Squad/Copilot session for implementation, include both roots in the prompt:
  - `PROTOTYPE_WORKTREE=C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node`
  - `CLEAN_WORKTREE=C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`

## Prototype files to inspect before porting

### Core shared auth/pairing

- `src\OpenClaw.Shared\DeviceIdentity.cs`
  - role-specific operator/node token storage;
  - `NodeDeviceToken`;
  - `NodeDeviceTokenScopes`;
  - role-aware token persistence.
- `src\OpenClaw.Shared\OpenClawGatewayClient.cs`
  - bootstrap setup-code consumption;
  - `auth.bootstrapToken` initial connection;
  - stored operator reconnect with `auth.deviceToken`;
  - role-specific token handoff from `hello-ok.auth`.
- `src\OpenClaw.Shared\WindowsNodeClient.cs`
  - node reconnect using `auth.deviceToken`, not `auth.token`.

### Tray setup and WSL gateway

- `src\OpenClaw.Tray.WinUI\Services\LocalGatewaySetup\LocalGatewaySetup.cs`
  - Ubuntu Store/package instance creation;
  - postcondition-based `wsl --install` verification;
  - first-boot WSL configuration;
  - upstream `install-cli.sh` invocation;
  - gateway config/service management;
  - endpoint resolver;
  - keepalive/lifecycle prototype;
  - operator pairing and Windows tray node provisioning.
- `src\OpenClaw.Tray.WinUI\App.xaml.cs`
  - setup engine construction;
  - shared identity path;
  - node service setup for local gateway pairing.
- `src\OpenClaw.Tray.WinUI\Services\NodeService.cs`
  - shared identity data path support.
- `src\OpenClaw.Tray.WinUI\Onboarding\Pages\ConnectionPage.cs`
  - prototype WSL setup button/progress panel;
  - use only as a reference because final clean UX is now a forked warning-page flow.

### Validation scripts

- `scripts\validate-wsl-gateway.ps1`
  - UI automation button click;
  - `UpstreamInstall`;
  - `Reuse`, `CleanBeforeEach`, `Recreate` loop modes;
  - endpoint/network diagnostics;
  - real setup-code/bootstrap proof;
  - operator proof;
  - Windows tray node proof;
  - separated validation/cleanup status.
- `scripts\reset-openclaw-wsl-validation-state.ps1`
  - exact-target destructive cleanup;
  - appdata/localappdata backup;
  - generated distro safety gates;
  - port/listener snapshots.

### Tests

- `tests\OpenClaw.Shared.Tests\DeviceIdentityTests.cs`
- `tests\OpenClaw.Shared.Tests\OpenClawGatewayClientTests.cs`
- `tests\OpenClaw.Shared.Tests\WindowsNodeClientTests.cs`
- `tests\OpenClaw.Tray.Tests\LocalGatewaySetupTests.cs`
- `tests\OpenClaw.Tray.Tests\SetupCodeDecoderTests.cs`
- `tests\OpenClaw.Tray.Tests\OnboardingStateTests.cs`

### Craig/WSL docs

- `docs\wsl-owner-validation.md`
- `docs\wsl-owner-open-issues.md`
- `docs\wsl-gateway-rootfs.md`

Only the first two should be considered for final PR docs. `wsl-gateway-rootfs.md` is historical/prototype context unless explicitly retained as a dev appendix.

## Validated prototype artifacts

Strongest proof artifact:

```text
artifacts\wsl-gateway-e2e-recreate-repeat\20260502-173403\summary.json
```

Earlier reuse-mode upstream setup-code proof:

```text
artifacts\wsl-gateway-e2e\20260502-114046\summary.json
```

These artifacts demonstrated:

- public upstream Linux installer path inside WSL;
- upstream `setupCode` bootstrap handoff;
- no dev-shim auto-accept;
- stored operator reconnect;
- Windows tray node visible through `node.list`;
- generated OpenClaw-owned WSL instance create/destroy loop;
- cleanup passed after repeat validation.

## Porting rules for the clean worktree

- Port behavior and tests, not prototype clutter.
- Do not port the dev rootfs/rootfs manifest path into the product path.
- Do not port fake/dev gateway shims into final validation claims.
- Keep `scripts\validate-wsl-gateway.ps1` focused on product validation scenarios:
  - `PreflightOnly`;
  - `UpstreamInstall`;
  - `FreshMachine`;
  - `Recreate`.
- Keep all WSL file access through `wsl.exe`, never `\\wsl$`.
- Keep destructive cleanup exact-target only and gated by `-ConfirmDestructiveClean`.
- Keep token/setup-code/private-key redaction mandatory in artifacts and UI.
- Treat `systemctl active` as insufficient; require Windows-reachable health, gateway RPC/status, and setup-code minting.
