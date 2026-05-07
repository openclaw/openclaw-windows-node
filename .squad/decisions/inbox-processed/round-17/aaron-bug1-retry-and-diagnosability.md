# Aaron — Bug 1 part 4 — first-call race retry + stderr diagnosability

**Date:** 2026-05-04
**Branch:** `feat/wsl-gateway-clean`
**Commit:** `05f7be0` — `fix(setup): retry stage-1 approve preview on first-call race + surface stderr in failure (Bug 1 part 4)`
**Worktree:** `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
**Builds on:** Aaron-18 two-stage approve at `6942a81`
**Acting on:** Bostick-11 Round-2 Path B drive — `bostick-bug1-reverify.md` "Path B re-drive — Round 2" (lines 201–270).

## TL;DR

Bostick-11's Round-2 deterministic reproduction proved Aaron-18's two-stage approve is functionally correct, but the **engine's first stage-1 invocation** races with the gateway's internal Linux-operator auto-bootstrap: the bootstrap completes successfully (linux operator entry IS persisted to `paired.json` within the same wsl-exec window) but the CLI process driving it exits non-zero. A fresh process invocation made hundreds of ms later succeeds because the internal operator is now pre-paired.

Fix: retry stage 1 once on first failure with a short backoff (default 750 ms), and surface both attempts' stderr (each truncated to 1 KB) in the structured failure message so the next regression in this race-prone area is diagnosable from `setup-state.json` alone.

## Root cause confirmation (cite Bostick-11 Round 2)

`bostick-bug1-reverify.md` lines 233–261 ("Root-cause diagnosis: race during in-distro CLI's first `--token` call") provides the deterministic reproduction across all four Round-2 attempts:

| Call | Conditions | exit | result |
|---|---|---|---|
| Engine stage 1 | First call ever; linux internal operator NOT yet paired | non-zero | engine surfaces `(preview stage)` failure at Δt≈2.46 s |
| Manual stage 1 (same script, ~10 s later) | linux operator now auto-paired by the failed engine call | 0 | valid preview JSON with `selected.requestId` |
| Manual stage 2 (same script body) | same conditions | 0 | `paired.json` gains tray's deviceId |

Gateway journal corroboration (lines 254–259):

```
05:58:40.015  [ws] pairing required (windows tray)
05:58:40.019  engine fires bash -lc <stage 1>
05:58:41.xxx  [gateway] device pairing auto-approved device=ca5669…  role=operator   ← internal op auto-paired as side effect
05:58:42.474  engine surfaces (preview stage) failure                                ← exit non-zero, ~0.3s after auto-pair
```

The gateway DID auto-pair the internal operator within the engine's wsl-exec window, but the CLI process can't recover its current invocation after the inline bootstrap. Bostick recommended **Option 2 (retry stage 1 once)** + **Option 4 (surface stage-1 stderr)** in combination — that is exactly what is implemented here.

I did **not** add Option 1 (pre-warm during Phase 7/9). Reasoning: the retry alone is sufficient (the race is bounded — the failed first attempt itself triggers the bootstrap that the second attempt benefits from), and a pre-warm injects a Phase-7/9 dependency on a CLI command that itself would race on the very same code path on a fresh distro. Adding it would not be a clean addition; it would just shift the race surface earlier without removing it. Per directive: "the retry alone should be sufficient. Don't over-engineer."

## Implementation summary

### `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` — `WslGatewayCliPendingDeviceApprover`

- **New retry-aware constructor**: `WslGatewayCliPendingDeviceApprover(IWslCommandRunner wsl, string commandName, TimeSpan stage1RetryDelay)`. The legacy 2-arg constructor still works and defaults to 750 ms.
- **`ApproveLatestAsync` rewritten**: stage 1 is now driven via `RunStage1WithRetryAsync`, which:
  1. Runs stage 1 once.
  2. If success → return immediately (zero-cost on the happy path).
  3. If failure → `Task.Delay(_stage1RetryDelay, ct)` then run stage 1 again.
  4. Returns a `Stage1Outcome` carrying the second attempt's `WslCommandResult` plus the first attempt's stderr.
- **`BuildStage1Failure`**: composes the structured failure message:
  - Always prefixed with the existing `"Local gateway pending pairing approval CLI failed (preview stage)."` so existing log/UI consumers continue to recognize the failure shape.
  - Appends `stage1.attempt1.stderr=<truncated stderr>` when first-attempt stderr is present.
  - Appends `stage1.attempt2.stderr=<truncated stderr>` only when present AND distinct from attempt 1 (no duplication when both attempts produce identical stderr).
- **`TruncateStderr` (public static)**: trims, returns null for null/whitespace, caps at `MaxStderrSurfaceLength` (= 1024) and appends `…[truncated]` when truncated. Public so tests can assert the cap directly.
- **No script changes**: `BuildPreviewScript()` / `BuildCommitScript()` unchanged. The race is in the gateway's auto-bootstrap behavior, not in the script we pass.
- **No changes to stage 2** behavior — its existing stderr surface (line 1727 in the prior file) is unchanged.

### `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs`

Existing test updated:

- `WslGatewayCliPendingDeviceApprover_NonZeroExit_SurfacesStructuredFailureCode` — now uses `TimeSpan.Zero` constructor (so the test is fast) and asserts the new shape: prefix preserved + `stage1.attempt1.stderr=` + `ensureExplicitGatewayAuth` from the surfaced stderr.

New regression tests added:

1. `WslGatewayCliPendingDeviceApprover_TwoStage_Stage1FailsThenSucceeds_OverallSuccess` — stage 1 fails on attempt 1 (exit 1, stderr "auto-bootstrap pairing in progress"), succeeds on attempt 2 with valid preview JSON, then stage 2 commits. Asserts overall success + 3 wsl invocations + that both stage-1 attempts use `--latest` and stage 2 contains the requestId.
2. `WslGatewayCliPendingDeviceApprover_TwoStage_Stage1FailsTwice_SurfacesBothStderrs` — stage 1 fails both attempts with **distinct** stderrs. Asserts `operator_pending_approval_failed` code + message contains `stage1.attempt1.stderr=<first>` AND `stage1.attempt2.stderr=<second>` + stage 2 never ran.
3. `TruncateStderr_RespectsCap_AndAppendsTruncationMarker` — pumps a 1224-byte string through `TruncateStderr`, asserts length cap + `…[truncated]` suffix; also asserts pass-through for short input and null/whitespace handling.

## Test counts (per AGENTS.md)

| Suite | Count | Result |
|---|---|---|
| `./build.ps1` | Cli + Shared + WinNodeCli + WinUI | **all green** |
| `dotnet build src\OpenClaw.Tray.WinUI\…csproj -p:Platform=x64 --no-restore -v q` | 0 errors, 20 (pre-existing) warnings | **green** |
| `dotnet test tests\OpenClaw.Tray.Tests` | **505 / 505** passed (502 prior + 3 new) | passed |
| `dotnet test tests\OpenClaw.Shared.Tests` | **1158 passed + 22 skipped = 1180** (baseline) | passed |

**Note for Bostick / future runs:** the 6 `LocalizationValidationTests.*` tests fail when run inside this worktree without `OPENCLAW_REPO_ROOT` set — `.git` is a worktree marker file (not a directory) so the test's repo-root walker can't find the root. **Pre-existing**, NOT caused by this change. Workaround: `$env:OPENCLAW_REPO_ROOT = (Get-Location).Path` before `dotnet test`. Aaron-18's prior 502/502 report was likely produced from the team-root checkout (where `.git` is a real directory).

## Commit SHA + DLL freshness verification (per directive)

```
git log --oneline -2
05f7be0 (HEAD -> feat/wsl-gateway-clean) fix(setup): retry stage-1 approve preview on first-call race + surface stderr in failure (Bug 1 part 4)
6942a81                                  fix(setup): two-stage operator approve (preview + explicit requestId) against CLI v2026.5.3-1 (Bug 1 part 3)

Get-Item src\OpenClaw.Tray.WinUI\Services\LocalGatewaySetup\LocalGatewaySetup.cs ↦ 5/4/2026 11:08:10 PM
Get-Item …\OpenClaw.Tray.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.dll ↦ 5/4/2026 11:09:27 PM   (1232384 bytes)
```

DLL is **77 seconds newer** than the source file → fresh build confirmed. Additional sanity:

```
Select-String <DLL> -Pattern 'stage1.attempt1.stderr','MaxStderrSurfaceLength' -SimpleMatch
✅ MaxStderrSurfaceLength    (new constant present)
✅ stage1.attempt1.stderr    (new diagnosability marker present)
```

Both Aaron-18 and Aaron-19 strings present in the DLL — Bostick can identify Aaron-19 binaries by the `stage1.attempt1.stderr=` substring in `setup-state.json`'s `UserMessage` on any stage-1 failure.

## Bostick handoff: pre-conditions for next Path B re-drive

**Reset required: YES.** The OpenClawGateway distro from Round 2 has muddied state:
- `pending.json` contains the leftover `81ff1b4c-…` entry from Round 2's failed Phase 12 plus `f42b3dd8-…` from your manual stage-2 reproduction.
- `paired.json` already contains both the internal linux operator (`ca5669…`, side-effect of Round 2's failed engine call) AND tray deviceId `67f0595b…` (manually injected during Round 2 diagnosis).
- AppData has the prior tray's `device-key-ed25519.json` (deviceId `67f0595b…`).

Recommended reset before drive:
```
scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean
```
(same as Round 2 — preserves the 17 prototype distros, wipes OpenClawGateway + AppData + tray device-key).

**No new env vars required.** Use the same launch as Round 2:
```
$env:OPENCLAW_FORCE_ONBOARDING = "1"
$env:OPENCLAW_VISUAL_TEST = "1"
$env:OPENCLAW_VISUAL_TEST_DIR = "<repo>\visual-test-output\bug1-reverify-pathB-2026-05-04-round3"
$env:OPENCLAW_ONBOARDING_START_ROUTE = "LocalSetupProgress"
# OPENCLAW_VISUAL_TEST_LOCAL_SETUP deliberately UNSET — real engine
```

**Stale-build check (per directive):**
1. After pulling Aaron-19's source, run: `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`
2. Verify DLL fresh: `Get-Item …\OpenClaw.Tray.WinUI.dll | Select Name,LastWriteTime` — should be > Aaron-18's prior 22:48:42 timestamp.
3. Optional belt-and-suspenders: `Select-String <DLL> -Pattern 'stage1.attempt1.stderr' -SimpleMatch` — should hit. Aaron-18 binaries do NOT contain this string.

**Expected timeline on success (Phase 12 PairOperator):**
- `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` fires.
- You should see **3** `wsl.exe -d OpenClawGateway -- bash -lc <…>` invocations: stage-1 attempt 1 (will exit non-zero — that's the race), then a ~750 ms gap, then stage-1 attempt 2 (should succeed with preview JSON), then stage 2 (explicit requestId, mutates `paired.json`).
- Phase 12 should advance to `FinishedAtUtc`. Phases 13/14/15 should follow.
- `~/.openclaw/devices/paired.json` should contain BOTH the internal linux operator (`ca5669…` or fresh equivalent) AND the tray's NEW deviceId (`device-key-ed25519.json` regenerated by reset).
- Windows-side `settings.json.Token` populated (operator token from `device.pair.approve`).

**Failure-mode signals (so we don't misdiagnose):**

- `operator_pending_approval_failed` with `UserMessage` starting with `"Local gateway pending pairing approval CLI failed (preview stage). stage1.attempt1.stderr=…"` AND `stage1.attempt2.stderr=…` → **both** retries failed; the race fix is insufficient and we need Option 1 (pre-warm) or Option 3 (admin-password CLI flow). The actual stderr from each attempt will be in the message — **paste it into the report and we can diagnose from `setup-state.json` alone** (no tray.log dive required).
- `operator_pending_approval_failed` with stderr-only-in-attempt-1 (no `attempt2` clause in the message) → attempt-1 failed but attempt-2 succeeded with stage 2 itself failing; surfaces stage-2 stderr verbatim under `operator_pending_approval_failed` (existing behavior).
- `no_pending_entries` → both stage-1 attempts ran cleanly but the gateway's pending state did not include the tray's request. On a fresh distro this should not happen.

**Branch state:** `feat/wsl-gateway-clean` is local-only (not pushed). HEAD is `05f7be0`. 17 prototype WSL distros untouched. No changes to scripts, shared layer, or onboarding XAML. Single source file modified (`LocalGatewaySetup.cs`) + single test file (`OperatorPairingApprovalTests.cs`).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
