# Mattingly — Bug 2 screenshot verification (closes Kranz CONDITIONAL APPROVE gate)

- **Date:** 2026-05-04
- **Author:** Mattingly (Frontend / Onboarding UX)
- **Worktree verified:** `..\openclaw-wsl-gateway-clean` @ `4af2581`
- **Closes gate from:** `.squad/decisions/inbox-processed/round-15/kranz-bug-fixes-verdict.md` § Bug 2
- **Scope:** mandatory screenshot pass for the `LocalSetupProgressPage` `RenderSnapshot` fix.
- **Machine state at start:** PID 8240 already terminated by Mike; `~/.openclaw/devices/pending.json` cleared in WSL; `./build.ps1` clean. OpenClawGateway WSL distro NOT touched (reserved for Bostick e2e).

Harness per scenario:

```
OPENCLAW_VISUAL_TEST=1
OPENCLAW_VISUAL_TEST_DIR=<worktree>\visual-test-output\bug2-verify-<scenario>
OPENCLAW_FORCE_ONBOARDING=1
OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress
OPENCLAW_VISUAL_TEST_LOCAL_SETUP=<scenario>
dotnet run --project src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore
```

Each scenario was launched from a fresh PowerShell session (env vars set inline so the child `dotnet run` inherited them — confirmed by capture-dir presence and content). The auto-capture path produces three PNGs (`page-00`, `page-01`, `page-02`) at staggered delays (`Loaded`, +1.5 s, +5 s); `page-02` is the steady-state frame after the visual-test override has been applied and is the one visually inspected. Between scenarios the previous PID was confirmed gone via `Get-Process -Name "OpenClaw*"`, killed only with `Stop-Process -Id <PID>`.

Visible-stage labels in the page (per `LocalSetupProgressStageMap.VisibleStages`) map to engine phases as:

| Idx | Label                  | Engine phase(s) folded in                                                            |
|-----|------------------------|---------------------------------------------------------------------------------------|
| 0   | Checking system        | Preflight, ElevationCheck                                                             |
| 1   | Installing Ubuntu      | CreateWslInstance                                                                     |
| 2   | Configuring instance   | ConfigureWslInstance                                                                  |
| 3   | Installing OpenClaw    | InstallOpenClawCli                                                                    |
| 4   | Preparing gateway      | PrepareGatewayConfig                                                                  |
| 5   | Starting gateway       | StartGatewayService                                                                   |
| 6   | Generating setup code  | MintBootstrapToken, PairOperator, CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd |

---

## Scenario 1 — `active:CreateWslInstance`

- **PNG:** `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-active-CreateWslInstance\page-02.png`
- **Expected:** stage 0 ✅, stage 1 spinner, stages 2–6 pending.
- **Actual:** Title "Setting up locally" / subtitle "Setting up your local OpenClaw gateway." Stage 0 "Checking system" renders the green ✅ check. Stage 1 "Installing Ubuntu" renders the active-row bullet on the left + the blue Lottie spinner pinned on the right. Stages 2–6 ("Configuring instance", "Installing OpenClaw", "Preparing gateway", "Starting gateway", "Generating setup code") each render the empty pending circle and grey label. Step indicator dots show position 2 of 6 highlighted (matches LocalSetupProgress in the LocalPath order). No error row, Next disabled.
- **Verdict:** **PASS.**

## Scenario 2 — `active:MintBootstrapToken`

- **PNG:** `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-active-MintBootstrapToken\page-02.png`
- **Expected:** stages 0–5 ✅, stage 6 spinner.
- **Actual:** Stages 0–5 ("Checking system" → "Starting gateway") all render the green ✅ check. Stage 6 "Generating setup code" renders as the active row (bullet + right-pinned spinner). No error row, Next disabled. Subtitle is the default "Setting up your local OpenClaw gateway." (no failure message, since this is an active-not-failed snapshot).
- **Verdict:** **PASS.** Confirms the new `RenderSnapshot` value-equality is correctly propagating mid-stream phase transitions — the exact regression the fix targets.

## Scenario 3 — `retryable:device-auth-invalid`

- **PNG:** `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-retryable-device-auth-invalid\page-02.png`
- **Expected:** stage 6 ❌ pinned on the failed phase, error row visible, Try Again button visible.
- **Actual:** Subtitle replaced with the failure code "device-auth-invalid". Stages 0–5 all green ✅. Stage 6 "Generating setup code" renders the red ❌ icon and the label is restyled in red — failure pinned on the correct (last-running) visible stage, **not** swallowed onto a synthetic Failed row. Error row appears below the stage list with light-red fill, "device-auth-invalid" on the left, and the **"Try again"** button on the right. Back button visible, Next disabled. Step dots show position 2 of 6.
- **Verdict:** **PASS.** This is the Aaron-14 e2e symptom; the pre-fix code would have left the spinner stuck on stage 1 and hidden the retry button entirely.

## Scenario 4 — `terminal:Setup cannot continue`

- **PNG:** `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-terminal-Setup-cannot-continue\page-02.png`
- **Expected:** error row + diagnostics hint, **no** retry button.
- **Actual:** Subtitle "Setup cannot continue". Stages 0–5 all green ✅. Stage 6 "Generating setup code" rendered with red ❌ icon and red label. Error row below with two lines of text — line 1 "Setup cannot continue", line 2 "Diagnostics: aka.ms/wsllogs" — and **no Try again button on the right** (the entire right edge of the error row is empty). Back button visible, Next disabled.
- **Verdict:** **PASS.** `ShouldShowRetryButton` correctly returns false for FailedTerminal; the diagnostics hint replaces the actionable retry, matching the policy spec.

---

## Final verdict

**ALL 4 scenarios PASS. Kranz's CONDITIONAL APPROVE gate (`.squad/decisions/inbox-processed/round-15/kranz-bug-fixes-verdict.md` § Bug 2 closeable item: "screenshot pass after PID 8240 release") is now CLOSED.**

Commit `4af2581` is cleared for ship from the UX side. Bostick can proceed to the live e2e drive against the OpenClawGateway distro (untouched by this run) without further blocker on Bug 2.

## Captured PNGs (preserved — do not delete; Bostick may reference)

- `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-active-CreateWslInstance\page-{00,01,02}.png`
- `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-active-MintBootstrapToken\page-{00,01,02}.png`
- `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-retryable-device-auth-invalid\page-{00,02}.png`
- `..\openclaw-wsl-gateway-clean\visual-test-output\bug2-verify-terminal-Setup-cannot-continue\page-{00,02}.png`

(Scenarios 3 and 4 produced two PNGs instead of three because the `+1.5 s` capture and the `+5 s` capture both fell on the steady-state failed frame and the file-naming dedupes — the inspected `page-02.png` is the steady-state frame in all four cases.)

## Machine state at end

- No `OpenClaw*` process running (verified `Get-Process -Name "OpenClaw*"` returns empty).
- All four `OPENCLAW_VISUAL_TEST*` / `OPENCLAW_FORCE_ONBOARDING` / `OPENCLAW_ONBOARDING_START_ROUTE` env vars cleared in the verification shells.
- OpenClawGateway WSL distro untouched.
- No source-code changes made (no defects required a fix).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
