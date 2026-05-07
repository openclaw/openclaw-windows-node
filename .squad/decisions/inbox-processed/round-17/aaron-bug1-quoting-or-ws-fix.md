# Aaron — Bug 1 part 5 — token-read in C# + stdout surfacing (quoting hypothesis)

**Date:** 2026-05-05
**Branch:** `feat/wsl-gateway-clean`
**Commit:** `f2dec42` — `fix(setup): read gateway token in C# and interpolate as shell literal; surface stdout (Bug 1 part 5)`
**Worktree:** `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
**Builds on:** Aaron-19 retry+stderr at `05f7be0`
**Acting on:** Bostick-11 Round-3 Path B drive — `bostick-bug1-reverify.md` "Path B re-drive — Round 3" (lines 311–421).

## TL;DR

Bostick-11 Round 3 proved the part-4 retry IS firing but BOTH stage-1 attempts still exit non-zero with **EMPTY stderr** in the engine's invocation context, while the IDENTICAL script run manually via PowerShell against the same gateway state returns exit 0 + 1054 bytes of valid preview JSON. Bostick's **leading hypothesis** is that the embedded `"$(cat /var/lib/openclaw/gateway-token)"` shell substitution gets mangled when .NET `ProcessStartInfo.ArgumentList` quoting forwards the script through `wsl.exe` to `bash -lc`.

This commit takes the cheapest fix that proves/disproves the hypothesis and is also the structurally cleaner option:

1. **Read the gateway token in C#** via a separate `wsl … cat /var/lib/openclaw/gateway-token` invocation (a trivial command with no embedded quotes or substitutions — there is nothing for argv encoding to mangle).
2. **Interpolate the token into the approve script as a single-quoted shell literal** (`ShellQuoteScalar`).
3. The approve script body now contains **NO `$(...)` shell substitution and NO `"` characters** — verified by a unit test (`WslGatewayCliPendingDeviceApprover_PreviewScript_HasNoEmbeddedShellSubstitutionOrDoubleQuotes`).
4. **Surface STDOUT (paired with stderr) for both stage-1 attempts and stage 2 failures.** Both streams are independently truncated to 1 KB. Exit codes from each attempt are also appended. If the quoting fix doesn't close the gap, the next round of `setup-state.json` will reveal what the CLI is actually writing — Round 3's empty-stderr blackout is gone.
5. Rejects tokens containing single quotes / newlines / control chars before interpolation; surfaces `operator_pending_approval_failed` with a `token-read stage` prefix when the token file is missing/empty/unreadable.

The `IPendingDeviceApprover` seam is unchanged → `SettingsOperatorPairingService` and the engine path don't notice the swap.

## STDOUT investigation findings (this round)

I did **not** run a focused live integration test against a real gateway in this session — Bostick was already reset and the directive was to land the fix and hand back. So I did not paste new stdout bytes from the engine path. What this commit changes is that, on the next Path B drive:

- If the quoting hypothesis is correct, both stage-1 attempts now succeed (preview JSON exits 0) and Phase 12 advances → the failure goes away outright.
- If something else is at play, the new diagnostic surface will show the actual CLI behavior. Specifically, any of these will now appear in `setup-state.json`'s `Issues[0].Message` instead of being silently dropped:
  - `stage1.attempt1.stdout=` / `stage1.attempt2.stdout=` (CLI writes JSON-mode errors to stdout — the most plausible reason for empty stderr)
  - `stage1.attempt{1,2}.exit=N` (always present now even with empty streams — distinguishes "CLI exited cleanly with no output" from "CLI crashed")
  - `stage2.exit=N` / `stage2.stdout=` (same coverage for the commit stage)

## Root cause finally pinned

**Pinned to a strong working hypothesis, not yet verified end-to-end.** Bostick-11 Round-3's evidence is dispositive that the failure is in the engine's invocation context (same script, same gateway state, manual run = exit 0; engine run = exit non-zero + empty stderr; both attempts). The quoting hypothesis is the leading concrete mechanism that fits the evidence (only the script's embedded `"$(…)"` is sensitive to argv encoding; everything else in the script is single-quoted or unquoted whitespace-separated tokens).

This commit closes the leading hypothesis cleanly. If Round 4 still RED with non-empty stdout/stderr surfaced, we move to Option 4 (drive `device.pair.approve` over WS directly from the tray, eliminating CLI shell-out entirely) — see "Why not Option 4 right now" below.

## Chosen fix + rationale

**Quoting refactor + stdout surfacing.** Both in one commit. Rationale:

- **Cheapest test of the leading hypothesis.** Single source file, single test file, no new dependencies, no protocol surface added. Fully reversible if needed.
- **Strictly improves the diagnostic surface even if the hypothesis is wrong.** Round 3's empty-stderr blackout cannot recur — exit codes are always surfaced now, and stdout is surfaced wherever the existing stderr was.
- **Structurally cleaner regardless of whether it fixes Bug 1.** The previous `"$(cat ...)"` was load-bearing on a fragile interaction between .NET MSVCRT escaping and wsl.exe's argv forwarding. The new approach has zero embedded shell metacharacters in the approve script.
- **Engine path is untouched.** `SettingsOperatorPairingService.PairAsync` still calls `IPendingDeviceApprover.ApproveLatestAsync(state, ct)` exactly as before.

### Why not Option 4 (WS protocol) right now

I considered it seriously per the directive's meta-pattern observation ("4 fixes, 4 distinct failure modes, all in the CLI shell-out path"). I deferred it as the **fallback** for two reasons:

1. **Surface area.** Option 4 requires (a) a new `device.pair.approve` admin frame on `OpenClawGatewayClient`, (b) a new `IPendingDeviceApprover` impl that uses it, (c) wiring it into the engine in `LocalGatewaySetup.cs`, (d) verifying the upstream gateway WS handler accepts the frame from a non-admin operator connection (the same connection the tray already holds for keepalive — needs scope check), and (e) deciding whether to keep the CLI-based path as a fallback. That is a larger blast radius than a quoting refactor.
2. **The leading hypothesis is concrete, narrowly scoped, and not yet falsified.** The Round-3 evidence is fully consistent with quoting-mediated argv mangling; killing that hypothesis cheaply is the right next move. If Round 4 lands RED with the new stdout surface populated and the failure is clearly NOT a quoting mangle, Option 4 becomes the next commit and I'll have the diagnostic data to write the WS frame correctly the first time.

If Round 4 is RED, my next commit will be Option 4 — the existing seam keeps that swap small.

## File changes

| File | Change |
|---|---|
| `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` | `WslGatewayCliPendingDeviceApprover` — new `ReadGatewayTokenAsync` (separate cat call), refactored `BuildPreviewScript`/`BuildCommitScript` to take token argument and interpolate via `ShellQuoteScalar` (no `$(...)` / no `"`), `RunStage1WithRetryAsync` now carries the full first-attempt `WslCommandResult` (not just stderr), `BuildStage1Failure` now surfaces both stderr AND stdout AND exit codes for both attempts, new `BuildStage2Failure` surfacing stdout for stage 2 (back-compat: bare-stderr shape preserved when no stdout), new `IsSafeTokenForSingleQuoteInterpolation` guard, new `MaxStdoutSurfaceLength = 1024` + `TruncateStdout`, `TokenReadResult` record, expanded class-level comment. **No script changes outside the approver. No engine wiring changes. No new public types beyond what tests need.** |
| `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` | Updated all `WslGatewayCliPendingDeviceApprover_*` two-stage tests to prepend a successful token-read `WslCommandResult` (cmd[0] = token-read) and to use the new command-index offsets. Added 6 new tests (see below). Removed assertion that the script contains `$(cat …)` and replaced with the inverse — `Assert.DoesNotContain("$(", script)` + `Assert.DoesNotContain("\"", script)` + `Assert.Contains("'TOKEN-VALUE-XYZ'", script)`. |

### New test names

1. `WslGatewayCliPendingDeviceApprover_PreviewScript_HasNoEmbeddedShellSubstitutionOrDoubleQuotes` — pins the quoting-fix invariant: approve script body contains no `$(`, no `"`, and contains the literal token wrapped in single quotes.
2. `WslGatewayCliPendingDeviceApprover_TokenReadFails_SurfacesStructuredFailure_NoApproveScriptRuns` — `cat` non-zero exit → `operator_pending_approval_failed` with `token-read stage` prefix and stderr surfaced; no approve script runs.
3. `WslGatewayCliPendingDeviceApprover_TokenReadEmpty_SurfacesStructuredFailure` — empty/whitespace token → `operator_pending_approval_failed` with `token file empty`.
4. `WslGatewayCliPendingDeviceApprover_TokenWithUnsafeCharacters_RejectedBeforeApprove` — single-quote, `\n`, `\r`, `\0`, control char tokens all rejected with `unsafe characters` before any approve invocation.
5. `WslGatewayCliPendingDeviceApprover_Stage1FailureWithStdoutOnly_SurfacesStdout` — both attempts exit non-zero with stdout-only (mimics Round 3's empty-stderr signature plus the most plausible cause); message contains `stage1.attempt1.stdout=`, `stage1.attempt2.stdout=`, both exit codes.
6. `WslGatewayCliPendingDeviceApprover_Stage2FailureWithStdoutOnly_SurfacesStdout` — stage 2 exit non-zero with stdout-only; message contains `commit stage`, `stage2.exit=`, `stage2.stdout=`.

## Test counts (per AGENTS.md)

| Suite | Count | Result |
|---|---|---|
| `./build.ps1` | Cli + Shared + WinNodeCli + WinUI | **all green** |
| `dotnet build src\OpenClaw.Tray.WinUI\…csproj -p:Platform=x64 --no-restore -v q` | 0 errors, 20 (pre-existing) warnings | **green** |
| `dotnet test tests\OpenClaw.Tray.Tests` (with `OPENCLAW_REPO_ROOT` set) | **511 / 511** passed (505 baseline + 6 new) | passed |
| `dotnet test tests\OpenClaw.Shared.Tests` (with `OPENCLAW_REPO_ROOT` set) | **1158 passed + 22 skipped = 1180** (baseline) | passed |

**Pre-existing 6 LocalizationValidationTests failures** when run inside this worktree without `OPENCLAW_REPO_ROOT` (worktree's `.git` is a marker file, not a directory — Aaron-19 documented this). Workaround unchanged: `$env:OPENCLAW_REPO_ROOT = (Get-Location).Path` before `dotnet test`. With the env var set, all 511 pass.

## Commit SHA + DLL freshness verification

```
git log --oneline -2
f2dec42 (HEAD -> feat/wsl-gateway-clean) fix(setup): read gateway token in C# and interpolate as shell literal; surface stdout (Bug 1 part 5)
05f7be0                                  fix(setup): retry stage-1 approve preview on first-call race + surface stderr in failure (Bug 1 part 4)

Get-Item …\Services\LocalGatewaySetup\LocalGatewaySetup.cs                                        ↦ 5/4/2026 11:34:52 PM
Get-Item …\OpenClaw.Tray.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.dll ↦ 5/4/2026 11:41:22 PM (1237504 bytes)
Get-Item …\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.dll      ↦ 5/4/2026 11:39:31 PM (1237504 bytes)
```

DLL is **6m30s newer** than the source file → fresh build confirmed. All Bug-1-part-5 marker strings present in DLL:

```
✅ token-read stage                          (new structured-failure prefix)
✅ stage1.attempt1.stdout                    (new diagnostic surface)
✅ IsSafeTokenForSingleQuoteInterpolation    (new guard)
✅ BuildPreviewScript                        (refactored — now takes token arg)
✅ MaxStdoutSurfaceLength                    (new 1024-byte cap)
```

Bostick can identify Aaron-20/part-5 binaries by any of these strings; Aaron-19 binaries do NOT contain `token-read stage`, `stage1.attempt1.stdout`, or `MaxStdoutSurfaceLength`.

## Bostick handoff: pre-conditions for next Path B re-drive

**Reset required: YES.** Round-3 left the OpenClawGateway distro with `paired.json = {02f0a6c7…}` (linux internal operator from Round-3's failed engine call) and an empty `pending.json` (cleared by your Round-3 manual diagnostic). Round-3 also generated a tray device key `8ca1a4d6…` that's no longer relevant. Run:

```
scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean
```

(Same as Round 3 — preserves the 17 prototype distros, wipes OpenClawGateway + AppData + tray device-key.)

**No new env vars required.** Use the same launch as Round 3:

```
$env:OPENCLAW_FORCE_ONBOARDING = "1"
$env:OPENCLAW_VISUAL_TEST = "1"
$env:OPENCLAW_VISUAL_TEST_DIR = "<repo>\visual-test-output\bug1-reverify-pathB-2026-05-05-round4"
$env:OPENCLAW_ONBOARDING_START_ROUTE = "LocalSetupProgress"
# OPENCLAW_VISUAL_TEST_LOCAL_SETUP deliberately UNSET — real engine
```

**Stale-build check (per directive):**

1. After pulling Aaron-20's source, run: `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`
2. Verify DLL fresh: `Get-Item …\bin\x64\Debug\…\OpenClaw.Tray.WinUI.dll | Select Name,LastWriteTime` — should be > Aaron-19's prior `23:09:27` timestamp.
3. Belt-and-suspenders marker check: `Select-String <DLL> -Pattern 'token-read stage' -SimpleMatch -Quiet` should return `True`. Aaron-19 binaries return `False`.

**Expected timeline on success (Phase 12 PairOperator):**

- `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` fires.
- You should see **3 to 4** `wsl.exe -d OpenClawGateway -- bash -lc <…>` invocations:
  - Invocation 1: token-read (`bash -lc 'cat /var/lib/openclaw/gateway-token'`) — should always succeed.
  - Invocation 2: stage-1 attempt 1 (preview script). May exit non-zero if the part-4 race still bites.
  - Invocation 3: stage-1 attempt 2 (after 750 ms backoff) if attempt 1 failed. Should now succeed.
  - Invocation 4 (or 3 if attempt 1 succeeded): stage-2 commit (explicit requestId, mutates `paired.json`).
- Phase 12 should advance to `FinishedAtUtc`. Phases 13/14/15 should follow.
- `~/.openclaw/devices/paired.json` should contain BOTH the internal linux operator AND the tray's NEW deviceId.
- Windows-side `settings.json.Token` populated (operator token from `device.pair.approve`).

**Failure-mode signals (so we don't misdiagnose):**

- `operator_pending_approval_failed` with `UserMessage` starting with `"Local gateway pending pairing approval CLI failed (token-read stage)."` → the `cat` itself failed. Paste the surfaced `exit=` and `stderr=` into the report; this would be a new failure mode (token file permissions, gateway service hadn't yet written the token, etc.).
- `operator_pending_approval_failed` with `UserMessage` starting with `"Local gateway pending pairing approval CLI failed (preview stage)."` AND containing `stage1.attempt1.exit=` AND (any of `stage1.attempt1.stdout=`, `stage1.attempt2.stdout=`, `stage1.attempt1.stderr=`, `stage1.attempt2.stderr=`) → quoting fix did NOT solve it. **Paste the entire UserMessage verbatim into the report — it now contains both stdout and stderr from both attempts plus exit codes, which should pin the actual failure mode.** Likely follow-up: Option 4 (WS-protocol drive of `device.pair.approve` from the tray).
- `operator_pending_approval_failed` with `UserMessage` starting with `"Local gateway pending pairing approval CLI failed (commit stage)."` → preview succeeded but commit failed. Same surface (stage2.exit + stage2.stdout + stage2.stderr) for diagnostics.
- `no_pending_entries` → both stage-1 attempts succeeded but the gateway's pending state did not include the tray's request. On a fresh distro this should not happen.

**Branch state:** `feat/wsl-gateway-clean` is local-only (not pushed). HEAD is `f2dec42`. 17 prototype WSL distros untouched. No changes to scripts, shared layer, onboarding XAML, or engine wiring. Single source file modified (`LocalGatewaySetup.cs`) + single test file (`OperatorPairingApprovalTests.cs`).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
