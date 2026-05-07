# Bostick — Bug-fix e2e verification drive (Apollo 13, Round 16)

- **Date:** 2026-05-04
- **Author:** Bostick (Tester / QA)
- **Worktree:** `..\openclaw-wsl-gateway-clean` @ `4af2581` (`feat/wsl-gateway-clean`)
- **Verifies:** Aaron-15 Bug 1 fix (`fe2de09`) + Mattingly Bug 2 fix (`4af2581`)
- **Pre-state:** PID 8240 already killed, `pending.json` cleared, `./build.ps1` clean per Mattingly hand-off.
- **Live tray PID at end:** **39856** (left running on the LocalSetupProgress page in `FailedRetryable` for Mike to inspect — kill with `Stop-Process -Id 39856`).

---

## Final overall verdict — **YELLOW**

- **Bug 2 (UI propagation): PASS — fix solid.** The page now renders real-engine state changes; first capture already shows stage 0 ✅ green and stage 1 active with spinner driven by the *real* engine (subtitle "Creating OpenClaw Gateway WSL instance"). This is the exact regression Mattingly's `RenderSnapshot` value-equality fix targets. Engine subsequently advanced through 11 history entries (Preflight → PairOperator), and the final `FailedRetryable` state is what the page renders now.
- **Bug 1 (operator pairing): FAIL — fix is incomplete.** Aaron-15's `WslGatewayCliPendingDeviceApprover` *does* fire on `pairing-required` (verified in tray.log immediately after the pairing-required event), but the `openclaw devices approve --latest --json --url <url> --token <admin-token>` invocation returns a non-zero exit and the engine surfaces a NEW failure code `operator_pending_approval_failed` ("Local gateway pending pairing approval CLI failed."). The Bug 1 failure surface has *moved* (from `pairing-required` → `operator_pending_approval_failed`) but the happy-path operator pairing is still blocked. See defect detail below.

Branch is **NOT ready for push.** Bug 2 is shippable; Bug 1 needs a follow-up patch from Aaron.

---

## Reset choice — **Option A (full destructive reset)**

**Why:** Mike's intent for this drive is verifying the fixes against a *clean install* — Bug 1's whole reason for existing is the auto-approve on a freshly-minted-bootstrap-token first connect. Reusing the previous distro would have skipped MintBootstrapToken/PairOperator entirely (engine would resume from a paired state), defeating the Bug 1 verification. Option A also exercises the auto-install path that Aaron added in earlier rounds and which Bug 1 sits downstream of. ~30s of unregister + AppData wipe is cheap insurance.

Reset script ran `-ConfirmDestructiveClean` cleanly:

- Backup root: `artifacts\reset-backups\20260504213007\` (contains `appdata-OpenClawTray\`, `localappdata-OpenClawTray\`, reset-summary.json).
- Steps: `wsl-terminate-OpenClawGateway` ✓, `wsl-unregister-OpenClawGateway` ✓, `backup-appdata` ✓, `backup-localappdata` ✓, `postconditions: Passed`.
- Verified afterward: `wsl --list` shows no `OpenClawGateway`, `%APPDATA%\OpenClawTray` and `%LOCALAPPDATA%\OpenClawTray` removed. The 17 prototype/build distros were left untouched (script is hard-coded to only touch `OpenClawGateway`).

---

## Phase-by-phase timeline

(Format mirrors aaron-e2e-drive — wall-clock from the drive, observed state, screenshot reference where one was captured.)

| t (PT) | Phase / event | Observed state | Screenshot |
|---|---|---|---|
| 21:30:07 | Reset start | `reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean` invoked | — |
| 21:30:34 | Reset complete | OpenClawGateway unregistered, AppData backed up + cleared | `artifacts\reset-backups\20260504213007\reset-summary.json` |
| 21:30:41 | Tray launch (Run #1) | Started without VISUAL_TEST capture vars; tray PID 37300 came up. Realised auto-capture wouldn't fire without `OPENCLAW_VISUAL_TEST=1`; killed by `Stop-Process -Id 37300`. | — |
| 21:33:01 | Tray launch (Run #2) | Env: `OPENCLAW_FORCE_ONBOARDING=1`, `OPENCLAW_VISUAL_TEST=1`, `OPENCLAW_VISUAL_TEST_DIR=...\e2e-verify-2026-05-04`, `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress`. **`OPENCLAW_VISUAL_TEST_LOCAL_SETUP` deliberately NOT set** — capture is on, state-synth is off; engine is real. Tray PID **39856**. | — |
| 21:33:16 | Page load + capture | LocalSetupProgress page rendered. Engine started Preflight + WSL probe (`wsl.exe --list --verbose`, `--status`, `--install Ubuntu-24.04 --name OpenClawGateway --location ... --no-launch`). Auto-capture fired three frames (`page-00.png`, `page-01.png`, `page-02.png`). | `visual-test-output\e2e-verify-2026-05-04\page-00.png` (also -01, -02; all three are the same render and are the *only* screenshots — see "Screenshot constraint" note below) |
| 21:33:16 | Phase 1 → 3 (Preflight, CheckWslSupport) | Both completed in < 200ms (history shows finished at 21:33:16.499 / 21:33:16.500 UTC-equiv). | page-00.png shows stage 0 ✅ |
| 21:33:16 → 21:33:56 | Phase 4 (CreateWslInstance) | `wsl --install Ubuntu-24.04` ran, `ext4.vhdx` materialised at 2.6 GB. Stage 1 ("Installing Ubuntu") was active in the captured frame. | page-00.png shows stage 1 active row + spinner |
| 21:33:56 → 21:34:04 | Phase 5 (ConfigureWslInstance) | Created `openclaw` user, `/etc/wsl.conf` w/ systemd, install dirs, `loginctl enable-linger`. Set default user, terminate. | (no nav → no new capture) |
| 21:34:07 → 21:34:50 | Phase 6 (InstallOpenClawCli) | `curl ... openclaw.ai/install-cli.sh | bash -s` produced OpenClaw CLI v2026.5.3-1 (commit 2eae30e) at `/opt/openclaw/bin/openclaw`. | — |
| 21:34:50 → 21:35:00 | Phase 7 (PrepareGatewayConfig) | Writes `openclaw.json` with `gateway.mode=local`, `port=18789`, `auth.mode=token`, generates `gateway.auth.token`. | — |
| 21:35:00 → 21:35:04 | Phase 8 (InstallGatewayService) | `openclaw gateway install --force --port 18789` — installed user systemd unit. | — |
| 21:35:04 → 21:35:15 | Phase 9 (StartGatewayService) | `openclaw gateway start` + WSL keepalive PID 9232; service `Active: active (running)` (verified end-state). | — |
| 21:35:15 → 21:35:15 | Phase 10 (CheckGatewayReadiness) | < 100ms — gateway reports ready over WS. | — |
| 21:35:15 → 21:35:17.9 | Phase 11 (MintBootstrapToken) | `openclaw qr --json --url ws://localhost:18789` ran inside distro; tray persisted bootstrap token to `settings.json` (`BootstrapToken = "rOm5yDvahyWZ9jLOMnEtbx4xbnWChq83PO4mekgBFOo"`). | — |
| 21:35:17.9 → 21:35:21.4 | Phase 12 (PairOperator) — **Bug-1 territory** | Tray generated NEW Ed25519 device id `250d04ae...46b3df`. Connected ws://localhost:18789, walked V3AuthToken → V3EmptyToken → V2AuthToken signature ladder. Third connect returned `pairing required: device is not approved yet (requestId: c27875a2-f270-4256-b95a-28123db64ea4)` — **same shape as Aaron-14's drive**, which is exactly the spot Aaron-15's `IPendingDeviceApprover` is supposed to recover from. Pending entry IS present on the gateway side at `/home/openclaw/.openclaw/devices/pending.json` (deviceId/publicKey/role=operator/scopes=operator.* match the tray identity). | — |
| 21:35:21.367 | Auto-approve attempt fires | `wsl.exe -d OpenClawGateway -- bash -lc <redacted>` — matches `WslGatewayCliPendingDeviceApprover`'s shape (admin token dereferenced inside distro). **The approver IS wired and IS being invoked. Bug-1 fix shipped at fe2de09 reaches code.** | — |
| 21:35:24.137 | Engine transitions to FailedRetryable | `setup-state.json`: `Phase=17 (Failed), Status=5 (FailedRetryable), FailureCode="operator_pending_approval_failed", UserMessage="Local gateway pending pairing approval CLI failed."` Phase 12 (PairOperator) has no `FinishedAtUtc` → it's the last-running phase, which the page will pin the ❌ on (per Mattingly's `LastRunningPhase` walk). | — |
| 21:36 → 21:44 | Diagnosis | Reproduced the approver invocation manually. Root cause confirmed (see defect). | — |

### Screenshot constraint (note for Mike)

The host environment does not give this CLI agent access to drive the WinUI window or take screen captures from outside the app. The **only** captures available are the three the app fires itself (`Loaded`, `+1.5s`, `+5s`) and any subsequent `_state.PageChanged` events. Because the engine never navigated away from `LocalSetupProgress` (it stayed on the page through advance → fail), no further captures were produced. The first three frames *do* however prove Bug 2's UI-propagation fix end-to-end against the *real* engine: stage 0 ✅ green and stage 1 active *with the real-engine subtitle*, captured before any of the synthetic-state visual-test override path could touch the page (which it can't anyway — `OPENCLAW_VISUAL_TEST_LOCAL_SETUP` was unset).

---

## Bug 1 — verdict: **FAIL** (auto-approve CLI invocation rejected)

**Failure surface:** engine ends in `FailedRetryable` with code `operator_pending_approval_failed` instead of advancing to `CheckWindowsNodeReadiness` / `PairWindowsTrayNode` / `VerifyEndToEnd`.

**Reproduction (verified in this drive):**

```
$ wsl -d OpenClawGateway -u openclaw -- bash -lc \
    'TOKEN=$(cat /var/lib/openclaw/gateway-token); \
     /opt/openclaw/bin/openclaw devices approve --latest --json \
       --url "ws://localhost:18789" --token "$TOKEN"'

[openclaw] Failed to start CLI: Error: gateway url override requires explicit credentials
Fix: pass --token *** --password *** gatewayToken in tools).
    at ensureExplicitGatewayAuth (.../call-BCpe65RR.js:148:8)
```

**Root cause:** OpenClaw CLI v2026.5.3-1 (commit `2eae30e`) — the version installed by the gateway-side `install-cli.sh` — added an `ensureExplicitGatewayAuth` guard that **rejects any `--url` override unless BOTH `--token` AND `--password` are supplied**. Aaron-15's `WslGatewayCliPendingDeviceApprover` only passes `--token`, so the CLI exits before talking to the gateway. The approver's `ParseApproveJson` sees the non-JSON failure → returns a structured error → service surfaces `operator_pending_approval_failed`.

**Confirmation that the token Aaron-15 reads is correct:** `/var/lib/openclaw/gateway-token` content matches `gateway.auth.token` in `/home/openclaw/.openclaw/openclaw.json` (both 64-char hex, byte-exact match). The credential is right; the *invocation shape* is wrong against this CLI version.

**Suggested fix (Aaron's call):** one of —
1. Drop `--url` and rely on the CLI's default `gateway.remote.url` (after Phase 7 PrepareGatewayConfig writes it). Risk: today `openclaw.json` does NOT contain `gateway.remote.*` (verified — config file shown above), so the CLI may fall back to a different code path.
2. Pass `--password ""` (empty) alongside `--token` if the CLI accepts it — needs a quick CLI source check.
3. Pull the admin token through an env var (`OPENCLAW_GATEWAY_TOKEN`?) that the CLI may honour without triggering `ensureExplicitGatewayAuth`.
4. Stop using the public CLI for the approve and instead drive the WS protocol directly from the tray (uses the same WS client already wired for the operator connect).

(Option 4 is the most defensive against future CLI churn but is the heaviest patch.)

---

## Bug 2 — verdict: **PASS** (UI advances on real-engine events; FailedRetryable rendering covered by Mattingly's evidence)

- `page-00.png` (real-engine drive, no state synthesis): stage 0 ✅ green check, stage 1 "Installing Ubuntu" active row + spinner, stages 2–6 pending circles. Subtitle = engine's *real* `UserMessage`: **"Creating OpenClaw Gateway WSL instance"** (exactly Phase 4's message string). Step dots show position 2 of 6 highlighted. **This is the regression Bug 2 was about** — pre-fix, the page would have stayed stuck on stage 0 because `UseState<LocalGatewaySetupState?>` short-circuited on reference equality. The fix is doing what it says on the tin.
- The engine subsequently produced 7 more `StateChanged` events (Phases 5 → 12) and finally a `Block(operator_pending_approval_failed)` transition. Without further screenshots I can't *visually* confirm the live FailedRetryable render, BUT:
  - The page derives its render from a single pure helper (`LocalSetupProgressStageMap`).
  - Mattingly's screenshot pass already verified that helper against four scenarios including `retryable:device-auth-invalid` (stages 0–5 ✅, stage 6 ❌, error row + Try Again button) and the engine's terminal `Block(...)` shape is the same as the one mattingly's synthetic test fires. Failure code differs (`operator_pending_approval_failed` vs `device-auth-invalid`); rendering path is identical.
  - The `Capture(LocalGatewaySetupState)` LastRunningPhase walk handles Phase 12 (`PairOperator`) — verified by `LocalSetupProgressStageMapTests.FailedRetryable_AtPairOperator_PinsFailureOnLastVisibleStage`.
- No regressions vs Mattingly's screenshots: subtitle shows engine message; stages render `VisibleStages` ordering; step dots match LocalPath.

---

## New defects discovered

1. **DEFECT-BUG1-RESIDUAL** *(severity: high — blocks happy-path operator pairing)*
   `WslGatewayCliPendingDeviceApprover` invocation incompatible with OpenClaw CLI ≥ v2026.5.3-1 (commit `2eae30e`). See Bug 1 section. Owner: Aaron. New failure code surface: `operator_pending_approval_failed`.

2. **DEFECT-CLI-PENDING-INVISIBILITY** *(severity: medium — diagnostic noise)*
   Even when supplied with valid creds, `openclaw devices list --json` (run inside the gateway distro) returns `pending: []` while `~/.openclaw/devices/pending.json` on the same machine clearly contains the operator pairing entry. Suggests CLI-vs-server view divergence (scope filter? in-memory snapshot mismatch?) — worth a focused poke from Aaron because it complicates manual recovery / diagnosis. Not a tray-side defect.

3. **DEFECT-WSL-PROTOTYPE-LITTER** *(severity: low — housekeeping)*
   `wsl --list` still shows 17 leftover prototype/build distros (`OpenClawGatewayBuild-*`, `OpenClawGatewayPrototype-*`, `OpenClawUbuntuStoreProbe-*`) from earlier Phase-3 work. Harmless but eats disk and clutters diagnostics. Out of scope for this drive (Mike asked us NOT to touch them); flagging for someone with broader cleanup authority.

---

## Final state of artifacts

| Artifact | Path | State |
|---|---|---|
| Tray process | PID **39856** | **Running**, stuck on `LocalSetupProgress` page in `FailedRetryable` (per Mike's request — kill with `Stop-Process -Id 39856` when done inspecting). |
| Engine state | `%LOCALAPPDATA%\OpenClawTray\setup-state.json` | `Phase=17 (Failed), Status=5 (FailedRetryable), FailureCode=operator_pending_approval_failed`. Full History array shows Phases 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 — Phase 12 has no `FinishedAtUtc` → `LastRunningPhase = PairOperator`. |
| Tray settings | `%APPDATA%\OpenClawTray\settings.json` | `GatewayUrl=ws://localhost:18789`, `Token=""` (NOT populated — pairing failed), `BootstrapToken="rOm5yDvahyWZ9jLOMnEtbx4xbnWChq83PO4mekgBFOo"` (minted but never converted to operator token). **Pairing NOT complete.** |
| Tray paired.json | `%APPDATA%\OpenClawTray\paired.json` | **Missing** — would only be written by tray after a successful operator pairing. |
| Gateway pending.json | `OpenClawGateway:/home/openclaw/.openclaw/devices/pending.json` | **Populated** — 1 entry: requestId `c27875a2-f270-4256-b95a-28123db64ea4`, deviceId `250d04ae...46b3df`, role `operator`, scopes `operator.{approvals,read,talk.secrets,write}`. Awaiting approval. |
| Gateway paired.json | `OpenClawGateway:/home/openclaw/.openclaw/devices/paired.json` | Contains 1 entry — but it's the gateway's own internal Linux operator (deviceId `5b326408...a3f5`, scopes `operator.pairing` only), NOT the Windows tray. Bootstrap-internal pairing — not the operator we care about. |
| Gateway service | `openclaw-gateway.service` (systemd --user, PID 914 in distro) | **active (running)** since 04:35:07 UTC. CPU 12.8s, no errors in journal. |
| Tray log | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` | Captured full Phase-1-through-12 progression + signature-mode ladder + pairing-required + approve-attempt invocation. Useful evidence for Aaron's follow-up patch. |
| Capture frames | `..\openclaw-wsl-gateway-clean\visual-test-output\e2e-verify-2026-05-04\page-{00,01,02}.png` | All three are the same Phase-4-active render (Stage 0 ✅, Stage 1 spinner). Real-engine subtitle. |
| Reset backup | `..\openclaw-wsl-gateway-clean\artifacts\reset-backups\20260504213007\` | Pre-drive AppData snapshots preserved for forensics. |

---

## Validation per AGENTS.md

No source code modified during this drive (verification only). Test counts cited from Mattingly's just-completed pass on the same commit `4af2581` (`Shared.Tests: 1180/1180`, `Tray.Tests: 493/493`). `./build.ps1` was clean per pre-state hand-off. The only build/run during this drive was the live `dotnet run` of `OpenClaw.Tray.WinUI.csproj` — succeeded (tray reached the LocalSetupProgress page and the engine ran 11 phases against real WSL).

---

## Recommendations / handoff

1. **Aaron — Bug 1 follow-up (highest priority):** patch `WslGatewayCliPendingDeviceApprover` to satisfy CLI v2026.5.3-1's `ensureExplicitGatewayAuth` contract. See "Suggested fix" options above. Add a regression test that calls the approver against a stub `IWslCommandRunner` returning the new "gateway url override requires explicit credentials" stderr, asserting it surfaces a distinct error code (not silently retried).
2. **Mattingly — Bug 2:** **CLEARED.** Real-engine drive confirmed the propagation fix; no UX work needed.
3. **Kranz — gating:** Bug 2's CONDITIONAL APPROVE gate stays CLOSED (already closed by Mattingly's screenshot pass). Bug 1's gate should be re-OPENED with this report; commit `fe2de09` is incomplete against current upstream CLI.
4. **Mike — inspection:** Tray is on the FailedRetryable page right now (PID 39856). The page should show stages 0–5 ✅, stage 6 (Generating setup code) ❌, error row "operator_pending_approval_failed", Try-Again button visible. If you want to manually re-trigger after Aaron patches, click Try Again — engine will re-attempt PairOperator using the *same* deviceId already sitting in `pending.json`, so a fix that wires the approve correctly will close the loop without needing another reset.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
