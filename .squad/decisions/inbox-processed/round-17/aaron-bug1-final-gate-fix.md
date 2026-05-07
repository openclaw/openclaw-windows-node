# Aaron — Bug 1 part 6 — gate inversion (final fix)

**Date:** 2026-05-05
**Branch:** `feat/wsl-gateway-clean` (local-only, not pushed)
**Commit:** `4d36dcd` — `fix(setup): treat valid preview JSON as stage-1 success regardless of exit code (Bug 1 final)`
**Worktree:** `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
**Builds on:** Aaron-20 part-5 at `f2dec42`
**Acting on:** Bostick-11 Round-4 smoking-gun (`bostick-bug1-reverify.md` "Path B re-drive — Round 4", lines 425–608)

## Root cause confirmed (cite Bostick's smoking gun)

> Bostick-11 Round 4 (`bostick-bug1-reverify.md`):
> "Root cause identified — not a race, not a quoting bug, not a bootstrap issue. The CLI's `devices approve --latest --json` returns exit code 1 deterministically in PREVIEW MODE, even when it produces a fully valid JSON payload with a usable `selected.requestId`."

Bostick captured the literal `setup-state.json.UserMessage` from Aaron-20's diagnostics:

```
stage1.attempt1.exit=1
stage1.attempt1.stdout={
  "selected": { "requestId": "89cccfff-bd88-4b4a-b7f5-12d881842de2",
                "deviceId":  "ced3225394ce9c51b5798cbc051aae3f85c090ec2a34da3b9e7150a1f9298ec2",
                "scopes":    ["operator.approvals","operator.read","operator.talk.secrets","operator.write"], ... },
  "approveCommand": "openclaw devices approve 89cccfff-bd88-4b4a-b7f5-12d881842de2 --json",
  "requiresAuthFlags": { "token": true ... }
}
stage1.attempt2.exit=1
```

Both attempts: identical `exit=1` + identical valid preview JSON. Bostick then ran stage 2 manually with the captured requestId → `exit=0` + correct `paired.json` mutation with the tray's `ced3225394ce…` deviceId and full operator scopes. **The flow works once the gate is fixed.**

The exit-1 is not an error — it is the CLI's deliberate "preview only, no actual approve performed" signal. The valid preview JSON on stdout IS the contract.

## Diff summary (the actual line(s) changed)

**File 1:** `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`

Two changes in `WslGatewayCliPendingDeviceApprover`:

### Change A — `ApproveLatestAsync` (gate inversion, ~10 lines)

Before:
```csharp
var stage1 = await RunStage1WithRetryAsync(state, token, cancellationToken);
if (!stage1.Result.Success)
{
    return BuildStage1Failure(stage1.FirstResult, stage1.Result);
}
var preview = ParsePreviewJson(stage1.Result.StandardOutput);
if (!preview.Success)
{
    return new PendingDeviceApprovalResult(false, preview.ErrorCode, preview.ErrorMessage);
}
```

After:
```csharp
var stage1 = await RunStage1WithRetryAsync(state, token, cancellationToken);
// Bug 1 part 6: parse stdout JSON FIRST. CLI v2026.5.3-1 returns exit 1
// from `--latest --json` deterministically on the happy preview path.
var preview = ParsePreviewJson(stage1.Result.StandardOutput);
if (!preview.Success)
{
    if (!stage1.Result.Success)
    {
        return BuildStage1Failure(stage1.FirstResult, stage1.Result);
    }
    return new PendingDeviceApprovalResult(false, preview.ErrorCode, preview.ErrorMessage);
}
```

### Change B — `RunStage1WithRetryAsync` (skip 750ms retry on parseable preview, 1 line of logic)

Before: `if (first.Success) return new Stage1Outcome(first, null);`

After: `if (first.Success || ParsePreviewJson(first.StandardOutput).Success) return new Stage1Outcome(first, null);`

Without Change B, every successful pair would burn the 750ms retry delay (since `first.Success` is now always false on this CLI version). Belt-and-suspenders test pinned this.

**File 2:** `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` — +5 new tests (~155 lines).

**Net:** 168 insertions, 5 deletions, 2 files changed. No other files touched.

## Tests added/flipped

**Added (5):**

1. `WslGatewayCliPendingDeviceApprover_Stage1ExitOneWithValidPreviewJson_ProceedsToStage2_Succeeds` — uses Bostick's actual captured requestId/deviceId. exit=1 + valid preview JSON → SUCCESS, advances to stage 2. **This is the regression test for the gate fix.**
2. `WslGatewayCliPendingDeviceApprover_Stage1ExitZeroWithValidPreviewJson_ProceedsToStage2_Succeeds` — compatibility check: should the CLI ever return exit-0 again, gate must still advance.
3. `WslGatewayCliPendingDeviceApprover_Stage1ExitOneWithEmptyStdout_FailsWithDiagnostics` — exit=1 + empty stdout twice → failure with both attempts surfaced. Gate still rejects malformed responses.
4. `WslGatewayCliPendingDeviceApprover_Stage1ExitOneWithMalformedJson_FailsWithDiagnostics` — exit=1 + unparseable garbage → failure with stdout surfaced. Both attempts run.
5. `WslGatewayCliPendingDeviceApprover_Stage1ExitOneWithValidPreviewJson_DoesNotRetry` — uses `TimeSpan.FromMinutes(1)` retry delay; if retry fired, the test would hang. Pins the no-redundant-retry invariant from Change B.

**Flipped (0):** No existing tests were pinning the buggy "exit-non-zero IS failure for stage 1 always" behavior in a way that conflicted with the new gate. The existing `WslGatewayCliPendingDeviceApprover_NonZeroExit_SurfacesStructuredFailureCode` test (exit=1 + empty stdout + ensureExplicitGatewayAuth stderr) still passes unchanged because empty stdout makes `ParsePreviewJson` fall to the `no_pending_entries` failure branch, which then drops through to `BuildStage1Failure` (exit-non-zero gate). Same observable surface.

## Validation results (per AGENTS.md)

| Step | Command | Result |
|---|---|---|
| 1 | `./build.ps1` | **all green** (Cli + Shared + WinNodeCli + WinUI) |
| 2 | `dotnet build src\OpenClaw.Tray.WinUI\…csproj -p:Platform=x64 --no-restore -v q` | **0 errors**, 20 pre-existing warnings |
| 3 | DLL freshness — `Get-Item …\bin\x64\Debug\…\OpenClaw.Tray.WinUI.dll` | `OpenClaw.Tray.WinUI.dll` ↦ **2026-05-04 23:59:03** (18 minutes newer than Aaron-20's `23:41:22`) |
| 4 | `dotnet test tests\OpenClaw.Tray.Tests` (with `OPENCLAW_REPO_ROOT` set) | **516 / 516** passed (511 baseline + 5 new) |
| 5 | `dotnet test tests\OpenClaw.Shared.Tests` (with `OPENCLAW_REPO_ROOT` + `OPENCLAW_RUN_INTEGRATION=1`) | **1180 / 1180** passed |
| 6 | Commit on `feat/wsl-gateway-clean` | `4d36dcd` |

## Bostick handoff: pre-conditions for Round-5 verification

**Reset required: YES.** Round-4 left the gateway distro with my manually-injected `paired.json` entry for the tray's `ced3225394ce…` deviceId — that entry is correct but it was created by Bostick's manual stage-2 reproduction, not by the engine. For a clean engine-driven Round-5 verification, wipe and start fresh:

```powershell
scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean
```

(Same as Round-3 / Round-4 — preserves the 17 prototype distros, wipes OpenClawGateway + AppData + tray device-key.)

**No new env vars required.** Same launch as Round-4:

```powershell
$env:OPENCLAW_FORCE_ONBOARDING = "1"
$env:OPENCLAW_VISUAL_TEST = "1"
$env:OPENCLAW_VISUAL_TEST_DIR = "<repo>\visual-test-output\bug1-reverify-pathB-2026-05-05-round5"
$env:OPENCLAW_ONBOARDING_START_ROUTE = "LocalSetupProgress"
# OPENCLAW_VISUAL_TEST_LOCAL_SETUP deliberately UNSET — real engine
```

**Stale-build check (mandatory before launch):**

1. After pulling Aaron-21's source, run `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`.
2. `Get-Item …\bin\x64\Debug\…\OpenClaw.Tray.WinUI.dll | Select Name,LastWriteTime` — should be > Aaron-20's `23:41:22`. The expected timestamp from this build is `23:59:03` or newer if you rebuild.
3. There is no new marker string in this commit (the gate change is internal logic, no new diagnostic surface). Identify Aaron-21 binaries by **commit SHA `4d36dcd` in `git log --oneline -1` against the worktree**, not by string scan.

**Expected timeline on success (Phase 12 PairOperator):**

- `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` fires.
- Exactly **3** `wsl.exe -d OpenClawGateway -- bash -lc <…>` invocations on the happy path:
  - Invocation 1: token-read (`cat /var/lib/openclaw/gateway-token`) — exit 0.
  - Invocation 2: stage-1 preview (`devices approve --latest --json --token '…'`) — **exit 1** + valid preview JSON. **Engine now treats this as success.**
  - Invocation 3: stage-2 commit (`devices approve <requestId> --json --token '…'`) — exit 0, mutates `paired.json`.
- **No 750ms backoff window between invocations 2 and 3** (Change B short-circuits the retry on parseable preview JSON).
- Phase 12 advances → Phases 13/14/15 follow → `setup-state.json.Status = Complete`.
- Gateway `paired.json` contains both internal linux operator AND tray's deviceId with operator scopes.
- Windows-side `settings.json.Token` populated.

**Failure-mode signals (so we don't misdiagnose if Round-5 is RED):**

- `operator_pending_approval_failed` with `(token-read stage)` → token file issue (unchanged from part 5).
- `operator_pending_approval_failed` with `(preview stage)` AND `stage1.attempt1.exit=N stage1.attempt2.exit=M` → both stage-1 attempts produced no parseable preview JSON. The `stdout=` and `stderr=` surface will still be present. This now means a **genuine** stage-1 failure (not the part-6 false alarm).
- `operator_pending_approval_failed` with `(commit stage)` → stage 2 itself failed despite a valid preview. Surface includes `stage2.exit=`, `stage2.stdout=`, `stage2.stderr=`.
- `no_pending_entries` → stage 1 succeeded with parseable JSON but no `selected.requestId`. On a fresh distro this should not happen.

**Branch state:** `feat/wsl-gateway-clean`, HEAD `4d36dcd`, local-only (not pushed). 17 prototype WSL distros untouched. No changes to scripts, shared layer, onboarding XAML, or engine wiring outside the approver. Two files modified: `LocalGatewaySetup.cs` + `OperatorPairingApprovalTests.cs`.

## Lessons learned — 6 rounds of fixes for Bug 1, what would have caught this earlier

| # | Round / commit | Hypothesis fixed | What was actually wrong |
|---|---|---|---|
| 1 | bootstrap-token wire-format (`fe2de09`) | wire-format inconsistency between gateway mint and tray pair | Real issue — fixed cleanly in round 1 |
| 2 | drop `--url` (`3927451`) | `ensureExplicitGatewayAuth` rejected `--url` with `--token` | Real issue — fixed cleanly |
| 3 | two-stage approve (`6942a81`) | `--latest --json` is preview only, needs explicit requestId stage 2 | Real issue — fixed cleanly |
| 4 | retry on stage-1 failure (`05f7be0`) | first-call race causing transient stage-1 failure | **Misdiagnosis.** No race existed; the failure was deterministic exit-1 from happy-path preview. The retry was useless because the failure mode was deterministic, not transient. |
| 5 | quoting + stdout surfacing (`f2dec42`) | embedded `$(...)` in script body mangled by argv encoding | **Misdiagnosis** of root cause but **correct outcome:** the stdout/stderr/exit surfacing it added is what *finally* let Bostick see what the CLI was actually returning. The quoting refactor itself is structurally cleaner and worth keeping. |
| 6 | gate inversion (`4d36dcd`, this commit) | gate treats exit-non-zero as failure regardless of stdout JSON | Real root cause |

**What would have caught this in round 1:**

1. **Read the CLI source — `src/cli/devices-cli.ts` — for the `--latest --json` exit code, not just the output shape.** Round 3 read it for the preview-only behavior (correct) but did not notice the exit-1 was deliberate. A single `git grep "process.exit"` near the `usingImplicitSelection` branch in that file would have surfaced the explicit `process.exit(1)` after the JSON write.
2. **Surface stdout in failure messages from day one, not as a part-5 patch.** Bostick spent rounds 2 and 3 debugging in the dark because the only signal we had was "exit non-zero". The 8-line `BuildStage1Failure` extension that surfaces stdout + stderr + exit was the *single highest-leverage diagnostic change in the whole bug fix chain.* Generalize: **any time a child-process call is wrapped, surface stdout, stderr, AND exit code in any failure path. Always. Empty stderr blackouts are a recurring debugging anti-pattern.**
3. **A "happy-path manual reproduction" against a real CLI before writing a wrapper.** Bostick's Round-4 manual reproduction (running the exact engine-style invocation from PowerShell) is what locked in the diagnosis. If we had done that against a freshly-built gateway in round 1, we would have seen `exit=1` + valid JSON + empty stderr immediately, and would have inverted the gate up front.
4. **Distinguish "process success" from "operation success" as a wrapper-design discipline.** Treating `ExitCode == 0` as a synonym for `operation succeeded` is a recurring failure mode for shell-out wrappers around opinionated CLIs. CLI authors routinely use exit codes for additional signaling (preview-only, partial-success, idempotent-noop, etc.). The wrapper's success contract should always parse the structured output first.
5. **Prefer in-process protocols over shell-out CLIs whenever both are available.** Option 4 (drive `device.pair.approve` directly over the existing operator WS connection from the tray) was deferred in part 5 specifically because the leading hypothesis was the cheaper test. In hindsight, Option 4 would have been a single round of work instead of six rounds of CLI-shell-out chasing. **For future "tray talks to gateway" surfaces, default to the WS frame.** Reserve CLI shell-out for human operator scenarios where there is genuinely no in-process API.

**What was *retained* across all 6 rounds and remains correct:** wire-format fix (round 1), `--url` drop (round 2), two-stage flow (round 3), `IsSafeRequestId` (round 3), retry+stderr (round 4 — defensible belt-and-suspenders for genuinely transient failures even though the round-4 hypothesis was wrong), token pre-read + single-quoted literal + stdout/stderr/exit surfacing (round 5). All of those are structurally cleaner and more diagnostic than what they replaced; none are reverted.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
