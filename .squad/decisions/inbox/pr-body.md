## Overview

Clean port of the WSL gateway local-loopback onboarding flow originally prototyped in PR #241. The prototype validated the architecture end-to-end against a real WSL distro; this PR is the disciplined re-port — phase-gated commits, layered cleanly across Shared → Tray → Onboarding → scripts → docs, with three real bugs caught and fixed during the Bostick e2e drives. Branch contains 25 commits on top of `origin/master` (`871b959..6e532f7`) and is GREEN end-to-end as of Bostick Round 6.

## What's included

**Phase 1 — Shared identity & scoping**
- `feat(shared): port DeviceIdentity with role-specific operator/node tokens` (95911b8)
- `fix(shared): close Phase 1 punch list — scope persistence + role validation` (3ae03d3)

**Phase 2 — Shared clients**
- `feat(shared): port OpenClawGatewayClient — bootstrap + role-specific reconnect` (b20b5ce)
- `feat(shared): port WindowsNodeClient — auth.deviceToken reconnect` (b69202d)

**Phase 3 — Tray engine**
- `feat(tray): port LocalGatewaySetup with loopback-only WSL setup` (98bdf77)
- `fix(tray): close Phase 3 punch list — strip worker vocabulary, gate distro override` (4ab1ec6)

**Phase 4 — App startup wiring**
- `feat(tray): wire setup engine + shared identity path in App startup` (8cc32c6)

**Phase 5 — Onboarding pages**
- `feat(onboarding): add SetupWarning + LocalSetupProgress routes and SetupPath state` (43035ca)
- `feat(onboarding): SetupWarningPage with folded security notice` (6a5783a)
- `feat(onboarding): LocalSetupProgressPage bound to LocalGatewaySetup engine` (c2ad1e5)
- `chore(onboarding): remove WelcomePage (folded into SetupWarning)` (99f5107)
- `fix(onboarding): drop time estimate + clean orphan Welcome resw entries` (32cbeae)
- `feat(onboarding): nav-bar Next/Back policy on LocalSetupProgressPage per state` (73767c5)

**Phase 6/7 — Validation + reset scripts**
- `feat(scripts): port validate-wsl-gateway.ps1 — 4 scenarios, loopback-only, no rootfs` (8060ae9)
- `feat(scripts): port reset-openclaw-wsl-validation-state.ps1 — exact-target gated cleanup` (dbd7708)

**Phase 8 — Docs**
- `docs(wsl): port wsl-owner-validation + wsl-owner-open-issues with Craig's answers` (1300981)

**Localization**
- `feat(onboarding): localize SetupWarning + LocalSetupProgress strings (fr-fr/nl-nl/zh-cn/zh-tw)` (ce89251)

**Bug fixes from Bostick e2e drives**
- Bug 1 (bootstrap-token + operator-pair against CLI v2026.5.3-1) — 6-commit journey: `fe2de09`, `3927451`, `6942a81`, `05f7be0`, `f2dec42`, `4d36dcd`'s precursor
- Bug 2 (LocalSetupProgressPage stage advancement + FailedRetryable rendering) — `4af2581`
- Bug 3 (pending-device approver wired into Phase 14 role-upgrade pairing) — `4d36dcd`

## Verification

**Bostick Round 6 e2e drive — GREEN end-to-end.** All four scenarios pass against a clean WSL distro:
1. Fresh install + onboarding → operator-pair → role-upgrade → node-pair
2. Repaired install on existing identity (token-refresh path)
3. Validation-reset + replay
4. Failure injection at each stage shows the correct `FailedRetryable` UI surface

Screenshots: `visual-test-output\bostick-round6\` (final pass: `06-onboarding-complete.png`).

## Test counts

- **Tray:** 524 / 524 ✅ (+77 from baseline 447)
- **Shared:** 1180 / 1180 ✅
- **Build:** clean (`./build.ps1` — zero warnings in changed assemblies)

## Bug 1 journey (6 commits, kept un-squashed on purpose)

The bootstrap-token / operator-pair flow against CLI v2026.5.3-1 took 6 surgical commits because the CLI surface changed shape between the prototype and now. The lesson worth preserving in history:

> **CLI v2026.5.3-1 returns `exit=1` in operator-pair preview mode even on success, with a valid JSON payload on stdout. The exit code is NOT the success signal — the JSON shape is.**

The 6 commits walk through: wire-format consistency → ensureExplicitGatewayAuth → two-stage approve (preview + explicit requestId) → first-call race retry + stderr surface → C#-side token read & shell-literal interpolation → treat valid preview JSON as stage-1 success regardless of exit code.

Squashing this would erase the breadcrumbs the next person will need when CLI v2026.6.x lands.

## Architectural decisions worth highlighting

- **`IPendingDeviceApprover` seam** — Phase 14 role-upgrade pairing now goes through a testable seam instead of reaching directly into the gateway client. Lets us mock the approver in unit tests and swap implementations for the future remote-gateway scenario.
- **`RenderSnapshot` value-equality fix** — `LocalSetupProgressPage` was missing UI updates because `UseState` was doing reference-equality on the snapshot record. Switched the snapshot to a value-equality record. This is a pattern worth sweeping across other UseState consumers (see follow-ups).
- **Two-stage approve flow** — operator-pair is now `preview → explicit requestId approve`, matching the CLI's actual contract instead of the prototype's single-call assumption.

## Follow-up work (NOT in this PR — punch list for separate work)

1. **PermissionsPage UseState sweep** — apply the `RenderSnapshot` value-equality pattern to PermissionsPage and audit other UseState consumers for the same bug.
2. **Uninstall plan PR** — 8 open Mike-questions blocking the uninstall PR; needs decisions before that branch can move.
3. **Translation strings (5 low-confidence)** — flagged in the localization commit; need native-speaker review for fr-fr "appairage" usage and zh-tw segmentation.
4. **e2e harness CONTRIBUTING note** — document the Bostick drive procedure + reset script invocation for the next contributor.
5. **Stale `Token` field cleanup** — `DeviceIdentity` still carries a legacy `Token` field used only by one reconnect path; should be removed once Phase 2.2 settles.

## Open questions for Mike

- Should the operator-pair preview retry budget (currently 3 attempts, 250ms backoff) be configurable, or is hardcoded fine for v1?
- Confirm we want to ship the WelcomePage removal (folded into SetupWarning) or keep WelcomePage as a separate route for future first-run telemetry hooks.
- Translation strings — defer to native-speaker review or ship-and-iterate?

## Why no squash

Kranz recommendation: **keep the 25-commit forensic trail intact.** The Bug 1 6-commit journey in particular documents a CLI-version-specific gotcha that will recur the next time the CLI surface churns. Squashing trades long-term debuggability for short-term log tidiness — wrong trade for an integration-heavy feature.

---

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
