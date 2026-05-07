---
updated_at: 2026-05-05T05:32:00.000Z
focus_area: Draft PR opened at https://github.com/openclaw/openclaw-windows-node/pull/274. Awaiting Mike's review + decisions on punch-list items + push-vs-merge timing.
active_issues:
  - Kranz final push-readiness verdict (if filed)
  - Mike's decision on push timing
  - Punch-list: DEFECT-RESUME-NO-AUTORETRY, DEFECT-CLI-PENDING-INVISIBILITY
---

# What We're Focused On

All three critical bugs fixed and verified end-to-end:

- **Bug 1 (4d36dcd):** 6-commit journey for operator-pairing auto-approve. Final fix: gate inversion (treat valid JSON as success despite exit code 1). Tests green, Bostick Round 5 verified.
- **Bug 2 (4af2581):** LocalSetupProgressPage stage propagation via RenderSnapshot value equality. Tests green, Mattingly screenshot pass closed gate.
- **Bug 3 (6e532f7):** Phase 14 node role-upgrade auto-approve via direct IPendingDeviceApprover reuse. Tests green, Bostick Round 5 verified.

Branch `feat/wsl-gateway-clean` holds 22 commits since baseline `871b959` (all green per Aaron + Bostick validation).

**End-to-end verification (Bostick Round 5):**
- Phases 1–14 executed cleanly on fresh WSL state
- Operator pairing completed (Bug 1 fixed)
- Node role-upgrade completed (Bug 3 fixed)
- "Grant Permissions" page reached
- Manual node connect verified
- All stages progressed correctly

## Final state

- **Build:** PASS · **Shared.Tests:** 1180/1180/0/0 · **Tray.Tests:** 505+/505+/0/0 (all fixes).
- **Net delta from anchor `871b959`:** ~50 new tests total, zero regressions.
- **PR-readiness sanity:** `.squad/` not in clean worktree (lives only in TEAM_ROOT); `artifacts/` gitignored; `scripts/experiments/` absent in clean worktree. No `.gitignore` update needed.
- **Decisions ledger:** Deduplicated 78% (41.9 KB → 9.0 KB); old entries archived to decisions/archive/round-17-archive.md. Canonical decisions preserved.

## Canonical gotchas (Bug 1 journey)

1. **Exit code 1 is NOT the success signal** — OpenClaw CLI v2026.5.3-1 `devices approve --latest --json` returns exit code 1 in preview mode even with valid JSON on stdout. Valid parseable JSON IS the success signal. Learned: gate inversion from exit-code to JSON-validity check (Aaron-21).
2. **Always surface STDOUT+STDERR+exit code immediately** — Shell-out from .NET via `wsl.exe -- bash -lc` failures are hard to diagnose without all three streams in logs. Empty-stderr failures cost 2 rounds. Learned: surface diagnostics from day 1 (Aaron-19/20 discoveries).
3. **Stale-build trap** — `./build.ps1` may not rebuild WinUI DLLs into the WinUI output. Always explicitly `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q` after source edits and verify DLL timestamp before launching.
4. **Diagnostic harness** — `OPENCLAW_FORCE_ONBOARDING=1` + `OPENCLAW_VISUAL_TEST=1` + `OPENCLAW_VISUAL_TEST_DIR=...` + `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress` + `scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean` is the working e2e drive harness.
5. **Reuse pattern validated** — `WslGatewayCliPendingDeviceApprover` directly reusable across Phase 12 (operator pair) and Phase 14 (node role-upgrade) without modification, confirming `IPendingDeviceApprover` seam was the right architectural call.

## Deferred defects discovered

1. **DEFECT-RESUME-NO-AUTORETRY** (medium, UX/QA) — On tray relaunch with FailedRetryable state, engine doesn't hydrate persisted state → Try-Again button doesn't surface. Worth focused look post-merge (Aaron/Mattingly likely owns).
2. **DEFECT-CLI-PENDING-INVISIBILITY** (medium) — `openclaw devices list --json` returns empty pending even with populated pending.json. Root: gateway maintains pending as in-memory snapshot of live connections, not disk re-read. Shapes future manual-approve UX design.

## Reference paths

- Clean worktree (PR source): `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
- Prototype worktree (reference): `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node`

## Lockouts

None. All agents available for post-PR review feedback.

## Standing rules

- Never use `\\wsl$` or `\\wsl.localhost` for WSL file I/O; use `wsl.exe` commands.
- Destructive WSL cleanup is exact-target only (`OpenClawGateway`) and requires explicit confirmation.
- UI changes require screenshot verification.
- Exit code 1 from `devices approve --latest --json` is NOT an error; valid JSON IS the success signal.
- Always surface STDOUT+STDERR+exit code in shell-out failure messages from day 1.

