# OpenClaw Windows local gateway: WSL-owner Q&A

This document is the structured record of the questions we asked Craig Loewen
(WSL) about the Windows OpenClaw local-gateway design, and Craig's answers.
It is the canonical "why does the architecture look like this?" reference
for the Windows local-gateway PR.

Companion: [`docs/wsl-owner-validation.md`](wsl-owner-validation.md)
describes the resulting design as it ships.

**Status legend:** ✅ Answered (verbatim or paraphrased Craig answer
recorded). 🟡 Open.

**Source:** Craig Loewen's review of the prototype `wsl-owner-open-issues.md`
(2026-05-04). His answers are summarized authoritatively in
`.squad/decisions.md` under "Decision: Craig Loewen's WSL Answers
(Authoritative)" and underpinned the Phase 3 plan revision in
`.squad/decisions-archive.md`. The architecture statements below are
paraphrased; Mike's relayed verbatim Q&A lives in the squad decisions thread,
not in the public PR.

The design is built on three coupled choices:

1. **Distribution model:** create a dedicated `OpenClawGateway` instance from
   the Store Ubuntu-24.04 package and configure it post-install — no custom
   OpenClaw rootfs.
2. **Networking model:** loopback only between the Windows tray and the
   gateway in WSL — no WSL-IP fallback, no `lan`/`auto` bind.
3. **Lifecycle model:** instance-scoped `wsl --terminate OpenClawGateway` for
   repair; user-systemd plus a tray-owned keepalive for liveness; no global
   `wsl --shutdown` and no global `.wslconfig` mutation.

The goal remains a low-maintenance implementation that uses the public
OpenClaw Linux installer unchanged and does not maintain a custom OpenClaw
Linux distribution.

## Final shape

1. The Windows tray verifies WSL/WSL2 availability.
2. The tray creates a dedicated WSL2 instance named `OpenClawGateway` from
   the Store Ubuntu-24.04 package:
   ```powershell
   wsl.exe --install Ubuntu-24.04 `
           --name OpenClawGateway `
           --location "$env:LOCALAPPDATA\OpenClawTray\wsl" `
           --no-launch `
           --version 2
   ```
3. The tray launches the instance as root and applies OpenClaw-owned
   configuration:
   - create the `openclaw` user;
   - create `/home/openclaw/.openclaw`, `/opt/openclaw`,
     `/var/lib/openclaw`, and `/var/log/openclaw`;
   - write `/etc/wsl.conf` and `/etc/wsl-distribution.conf`;
   - set the default user to `openclaw` via
     `wsl --manage OpenClawGateway --set-default-user openclaw`;
   - terminate only `OpenClawGateway` so WSL config takes effect.
4. The tray runs the public OpenClaw Linux installer inside the instance:
   `https://openclaw.ai/install-cli.sh` with prefix `/opt/openclaw`. No
   forked or patched gateway installer.
5. The tray uses upstream OpenClaw CLI/service commands to configure and
   start the gateway.
6. The tray calls upstream `openclaw qr --json`, consumes the upstream
   setup-code/bootstrap-token handoff, and pairs Windows tray operator and
   Windows tray node sessions; both device tokens land in
   `%APPDATA%\OpenClawTray\device-key-ed25519.json`.

## Issue 1: Ubuntu Store package + post-install configuration

### Q1.1 — Is `wsl --install Ubuntu-24.04 --name OpenClawGateway --location ... --no-launch --version 2` a supported primitive for a Windows app creating a dedicated app-owned WSL instance?

**Status:** ✅ Answered.

**Craig:** Yes — supportable. This is the canonical primitive for an
app-owned WSL instance.

**Implication:** `LocalGatewaySetup.cs` issues exactly this command. The
clean port removed `--web-download`, `--from-file`, and any rootfs-import
fallback.

### Q1.2 — Is it acceptable to treat the install as successful when post-conditions pass, even if the `wsl --install` process itself hangs or exits unclearly?

**Status:** ✅ Answered.

**Craig:** **Trust the exit code.** The hang-fallback pattern from the
prototype is not needed.

**Implication:** The clean engine treats `wsl --install` exit 0 as the
success signal, and additionally confirms `OpenClawGateway` appears in
`wsl --list --quiet` to defend against the "winget-style" failure mode where
exit 0 reports success without registering a distro (see Q1.3). Non-zero
exit ⇒ install failure; no postcondition-on-hang path.

### Q1.3 — Should we prefer generic `Ubuntu`, explicit `Ubuntu-24.04`, `--web-download`, `--from-file`, or another source for the default path?

**Status:** ✅ Answered.

**Craig:** Use **explicit `Ubuntu-24.04`**, not generic `Ubuntu`. No
`--web-download` and no `--from-file` are needed.

**Implication:** The clean install command is pinned to `Ubuntu-24.04`. The
prototype's "generic `Ubuntu` channel was more reliable on this dev machine"
observation is not a basis for a final product default.

Empirical confirmation (2026-05-04, 20-iter harness on Windows 10.0.26200,
WSL 2.6.3.0): `wsl --install Ubuntu-24.04 --name <gen> --location <path>
--no-launch --version 2` succeeded **10/10**; `winget install --id
Canonical.Ubuntu.2404 -e --silent --accept-source-agreements
--accept-package-agreements --disable-interactivity` succeeded **0/10**
(stages the launcher APPX but never registers a WSL distro under
`--silent --disable-interactivity`). Raw artifacts:
`artifacts/wsl-install-vs-winget/run-20260504-131837/summary.json`.

### Q1.4 — What is the recommended enterprise/offline fallback when Store access is blocked?

**Status:** ✅ Answered.

**Craig:** Modern WSL distributions are no longer Store-gated; an offline
fallback is **not needed** for this PR.

**Implication:** No offline fallback path ships in this PR. If a future
enterprise scenario surfaces a real blocker, that decision can be revisited
separately.

### Q1.5 — Are `automount=false`, `interop=false`, and `appendWindowsPath=false` appropriate for this managed instance?

**Status:** ✅ Answered.

**Craig:** Yes — all three settings are appropriate for an app-owned
appliance.

**Implication:** `/etc/wsl.conf` ships with all three disabled (see
`docs/wsl-owner-validation.md`).

### Q1.6 — Are there WSL/systemd/machine-id/DNS/timezone details we should explicitly repair or validate after cloning/configuring an Ubuntu instance?

**Status:** ✅ Answered.

**Craig:** **No post-clone repairs needed** — machine-id / DNS / timezone
work as delivered.

**Implication:** The setup engine does not regenerate `/etc/machine-id`,
does not rewrite `/etc/resolv.conf`, and does not touch timezone state. It
relies on `useWindowsTimezone=true` in `/etc/wsl.conf` for clock alignment.

### Q1.7 — Should OpenClaw avoid writing `/etc/wsl-distribution.conf`, or is it appropriate to suppress shortcuts/terminal profile for the dedicated instance?

**Status:** ✅ Answered.

**Craig:** Use both `wsl.conf` and `wsl-distribution.conf`. Suppressing
shortcut/terminal entries is the correct application of
`wsl-distribution.conf` for a privately managed instance.

**Implication:** The setup engine writes `/etc/wsl-distribution.conf` with
`shortcut.enabled=false` and `terminal.enabled=false`.

## Issue 2: Local networking between Windows and the WSL gateway

### Q2.1 — Is Windows localhost forwarding to a WSL2 service reliable enough to make `loopback` the final default?

**Status:** ✅ Answered.

**Craig:** **Yes — loopback only.** Windows localhost forwarding to a WSL2
service is a reliable core WSL promise.

**Implication:** Gateway binds to loopback inside WSL on `:18789`. Windows
tray connects via `http://localhost:18789` / `ws://localhost:18789`. The
prototype's earlier observations of localhost-forwarding flakiness were
attributed to other lifecycle issues (see Issue 3) and not to the forwarding
contract itself.

### Q2.2 — If localhost forwarding fails, is WSL-IP fallback a supported/recommended pattern for a Windows app-owned WSL instance?

**Status:** ✅ Answered.

**Craig:** **No.** WSL-IP fallback is not the recommended pattern.

**Implication:** The clean port has **no** WSL-IP fallback. The endpoint
resolver does not enumerate WSL interface addresses, does not run
`hostname -I` / `ip -4 addr` / `ip route` / `ss -ltnp` inside WSL, and
returns exactly one candidate: `http://localhost:18789`.

### Q2.3 — Is `gateway.bind=lan` inside the WSL instance acceptable for the fallback path, assuming the Windows tray still only advertises/selects local endpoints by default?

**Status:** ✅ Answered.

**Craig:** **No** — loopback only.

**Implication:** The setup engine never writes `gateway.bind=lan`. The
runtime configuration surface for `gateway.bind` was removed.

### Q2.4 — Should we implement `auto` bind promotion instead of defaulting to `lan`?

**Status:** ✅ Answered.

**Craig:** **No.** Loopback only; no `auto` promotion.

**Implication:** No promotion logic exists in the clean port. There is one
bind mode, and it is loopback.

### Q2.5 — Are there WSL NAT, mirrored networking, firewall, or portproxy recommendations we should follow while still avoiding global `.wslconfig` changes?

**Status:** ✅ Answered.

**Craig:** No — loopback forwarding works without any of those
modifications.

**Implication:** The tray does not write to `.wslconfig`, does not configure
mirrored networking, does not add Windows firewall rules, and does not run
`netsh interface portproxy` for normal local-gateway operation.

### Q2.6 — What diagnostics should we capture before asking users/maintainers to file WSL networking bugs?

**Status:** ✅ Answered.

**Craig:** Point at **<https://aka.ms/wsllogs>**. Do not scrape WSL internal
log files from the product.

**Implication:** On any setup or networking failure, the
`LocalSetupProgressPage` shows an aka.ms/wsllogs hint, the validation
script's `Save-DiagnosticsSnapshot` records `wslLogsHelp =
https://aka.ms/wsllogs`, and the run summary appends a "Diagnostics: see
https://aka.ms/wsllogs..." note. The product captures only its own state
(Windows-side `:18789` listener snapshot, loopback `/health` probe,
redacted setup-state.json) and a generated repro guide.

## Issue 3: WSL gateway lifecycle and service ownership

### Q3.1 — For an app-owned WSL appliance, should the gateway be a user-systemd service, a root/system service wrapper, or something else?

**Status:** ✅ Answered.

**Craig:** Both **user-systemd** and a **tray-owned keepalive** are
acceptable for this shape.

**Implication:** The clean port uses upstream OpenClaw service primitives
under the `openclaw` user, plus a tray-owned WSL keepalive
(`wsl.exe -d OpenClawGateway -u openclaw -- sleep 2147483647`) while
local-gateway mode is active. Readiness still requires Windows-side
`/health` to succeed — `systemctl active` alone does not imply Windows
reachability.

### Q3.2 — Is `loginctl enable-linger openclaw` expected to be reliable in this WSL shape, or should we avoid depending on it?

**Status:** ✅ Answered.

**Craig:** Linger is acceptable for this shape (alongside the tray
keepalive).

**Implication:** Setup runs `loginctl enable-linger openclaw`. The tray
keepalive remains as belt-and-suspenders for the active local-gateway
window.

### Q3.3 — Is a tray-owned keepalive process acceptable, or should it be treated as validation-only?

**Status:** ✅ Answered.

**Craig:** Acceptable as a product primitive (see Q3.1). It is not
validation-only.

**Implication:** The keepalive ships as part of the runtime, not just as a
test scaffold.

### Q3.4 — Is instance-scoped `wsl --terminate OpenClawGateway` the right repair/restart primitive?

**Status:** ✅ Answered.

**Craig:** **Yes.** Use `wsl --terminate OpenClawGateway` only. **Never**
global `wsl --shutdown`.

**Implication:** Setup, repair, validation, and removal paths all use
`wsl --terminate OpenClawGateway`. `git grep 'wsl --shutdown'` over the
clean worktree returns no product or validation hits.

### Q3.5 — Are there cases where global `wsl --shutdown` is recommended or unavoidable, despite our desire to avoid it?

**Status:** ✅ Answered.

**Craig:** **No.** Do not issue `wsl --shutdown` from this product.

**Implication:** Recreate / FreshMachine validation scenarios use
`wsl --unregister OpenClawGateway` for destructive cleanup. They never
issue a global shutdown.

### Q3.6 — What lifecycle diagnostics should the tray collect when WSL reports the service active but Windows cannot connect?

**Status:** ✅ Answered.

**Craig:** Same answer as Q2.6 — point at <https://aka.ms/wsllogs>; the
product should not scrape WSL logs.

**Implication:** The product collects only its own state and points at the
WSL-team-owned diagnostics page. See Q2.6.

## Mac app comparison: operator vs node

The macOS app runs operator/UI and a local Mac node from the same app
binary/process via separate gateway sessions:

- `GatewayConnection.shared` owns one `GatewayChannelActor` for
  operator/UI scopes (`role: "operator"`, `clientMode: "ui"`).
- `MacNodeModeCoordinator.shared.start()` owns a separate
  `GatewayNodeSession` and `MacNodeRuntime` (`role: "node"`,
  `clientId: "openclaw-macos"`, capabilities for canvas / screen / browser
  / etc.), connecting to the same gateway URL over a distinct WebSocket.
- In local mode, `GatewayProcessManager` manages the local gateway via
  launchd / OpenClaw CLI behavior; in remote mode,
  `ConnectionModeCoordinator` stops the local gateway and uses
  `NodeServiceManager.start()` against the remote gateway.

**Implication for Windows (decided by Mike):** The Windows tray pairs as
**both operator and node** against the local gateway, mirroring the macOS
in-app node model. There is **no separate WSL-internal worker** in this
PR. `StartWorker` / `PairWorker` phases were dropped; the
`PreserveWorkerData` parameter and `worker_data_preserved` lifecycle step
were removed in Phase 3 cleanup.

If a future scope adds a Linux worker inside the WSL gateway instance, it
will require a separate upstream-supported install/start/list proof and a
new owner decision — not a re-litigation of the current PR.

## Architectural decisions captured

For traceability, the high-order decisions implied by Craig's answers are:

1. **Distribution model** — Store Ubuntu-24.04 + post-install configuration;
   no custom rootfs; no offline fallback. (Q1.1, Q1.3, Q1.4)
2. **Configuration** — `wsl.conf` (systemd, automount/interop/appendPath
   off, default user `openclaw`, `useWindowsTimezone=true`) +
   `wsl-distribution.conf` (no shortcut, no terminal). No post-clone
   repairs. (Q1.5, Q1.6, Q1.7)
3. **Networking** — Loopback only, port 18789. No WSL-IP fallback. No
   `lan`/`auto` bind. No `.wslconfig` / portproxy / firewall mutation.
   (Q2.1–Q2.5)
4. **Lifecycle** — User-systemd + tray keepalive. Linger acceptable.
   `wsl --terminate OpenClawGateway` for repair. **Never** global
   `wsl --shutdown`. (Q3.1–Q3.5)
5. **Diagnostics** — `https://aka.ms/wsllogs`. No internal log scraping.
   (Q2.6, Q3.6)
6. **Roles in scope** — Windows tray operator + Windows tray node.
   Worker-in-WSL out of scope. (Mac app comparison + Mike's Phase-0
   decision.)

These decisions are reflected one-for-one in:

- `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`
- `src/OpenClaw.Tray.WinUI/App.xaml.cs` (factory + identity-path wiring)
- `src/OpenClaw.Tray.WinUI/Services/NodeService.cs`
- `src/OpenClaw.Tray.WinUI/Onboarding/Pages/SetupWarningPage.cs`
- `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs`
- `scripts/validate-wsl-gateway.ps1` (4 scenarios)
- `scripts/reset-openclaw-wsl-validation-state.ps1` (exact-target gated
  cleanup)

## Open follow-ups

These are not open architecture questions for Craig — they are tracked
work items that intentionally fall outside this PR:

- **Off-box / LAN / phone reachability via OpenClaw relay.** Blocked on
  relay ownership / protocol clarity. Not addressed in this PR.
- **`winget install Microsoft.WSL` as a platform repair fallback.** Deeper
  research in flight; does not change the Phase 3 decision to use
  `wsl --install` for distro creation in this PR.
- **Onboarding copy localization.** `Onboarding_SetupWarning_*` /
  `Onboarding_LocalSetupProgress_*` resw entries to be added across
  supported locales after Mike signs off final copy.

No open questions for Craig remain that block this PR.
