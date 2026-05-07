# Aaron — Bug 1 part 3 — two-stage operator approve

**Date:** 2026-05-04
**Branch:** `feat/wsl-gateway-clean`
**Commit:** `6942a81` — `fix(setup): two-stage operator approve (preview + explicit requestId) against CLI v2026.5.3-1 (Bug 1 part 3)`
**Worktree:** `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
**Builds on:** Aaron-17 fix at `3927451` (drop `--url`)
**Acting on:** Bostick-11 Path B drive — `bostick-bug1-reverify.md` "What Aaron needs to do next" Option 1.

## TL;DR

`openclaw devices approve --latest --json` on CLI v2026.5.3-1 is a **preview / inspection** operation: it returns a JSON envelope describing the selected pending request and an `approveCommand` hint string, but does **not** mutate `paired.json`. To actually approve, the CLI requires a follow-up call with the explicit requestId. The approver now runs both stages in a single `ApproveLatestAsync` call.

## Stage-1 JSON shape (verified against upstream CLI source)

Confirmed in `src/cli/devices-cli.ts` (commit `aef38de`, function body around lines 660–675 of the `devices.command("approve").action(...)` handler) — the `usingImplicitSelection` branch (`!resolvedRequestId || Boolean(opts.latest)`) writes the following JSON via `defaultRuntime.writeJson(...)` and `return`s before reaching `approvePairingWithFallback`:

```json
{
  "selected": {
    "requestId": "57ccdbad-24a7-4750-8e5d-e92c5c497da0",
    "deviceId": "c5979c9c…",
    "publicKey": "…",
    "platform": "windows",
    "clientId": "tray",
    "clientMode": "tray",
    "role": "operator",
    "scopes": ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"],
    "isRepair": false,
    "ts": 1777958500000
  },
  "approvalState": { "kind": "new-pairing", "requested": { … }, "approved": null },
  "approveCommand": "openclaw devices approve 57ccdbad-24a7-4750-8e5d-e92c5c497da0 --json",
  "requiresAuthFlags": { "token": false, "password": false }
}
```

Field path used by the approver: **`selected.requestId`** (with a tolerant fallback to a flat `requestId` at the JSON root for older CLI builds).

When no approvable pending request exists, the CLI prints `No pending device pairing requests to approve` to stderr and exits 1 (line 646–648). On the live Path B drive Bostick observed exit 0 in some shapes, so the parser is conservative: empty/whitespace stdout → `no_pending_entries` regardless of exit code.

I attempted live confirmation by injecting a synthetic pending entry into the OpenClawGateway distro's `~/.openclaw/devices/pending.json`, but the running gateway service holds pending state in memory and only re-reads from disk when the WS pairing path notifies it, so the standalone CLI invocation continued to report "no pending requests". The upstream source confirmation above is sufficient — Bostick-11 already verified the live shape during the Path B drive (see `bostick-bug1-reverify.md` lines 145–155).

## Implementation summary

`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` — `WslGatewayCliPendingDeviceApprover`:

- `ApproveLatestAsync` rewritten as a two-stage flow:
  1. **Preview** — `openclaw devices approve --latest --json --token "$(cat /var/lib/openclaw/gateway-token)"`. Stdout parsed by `ParsePreviewJson` to extract `selected.requestId`.
  2. **Commit** — `openclaw devices approve <requestId> --json --token "$(cat /var/lib/openclaw/gateway-token)"`. Stdout parsed by the existing `ParseApproveJson`.
- Helper methods `BuildPreviewScript()` and `BuildCommitScript(requestId)` keep both shells consistent (token file dereferenced inside the shell, no `--url`, `gateway.env` sourced when present, missing token file → exit 64).
- New static `ParsePreviewJson(string output) → PreviewParseResult { Success, RequestId, ErrorCode, ErrorMessage }`:
  - `selected.requestId` (string, non-empty) → success.
  - Flat `requestId` at root → success (legacy tolerance).
  - `ok:false` → `operator_pending_approval_failed` with the surfaced `error`.
  - Empty/whitespace → `no_pending_entries`.
  - Non-JSON / no requestId → `no_pending_entries`.
- New `IsSafeRequestId` guard rejects requestIds that contain anything outside `[A-Za-z0-9._:-]` or exceed 128 chars before they are interpolated into the stage-2 `bash -lc` script (defense-in-depth against future CLI shape changes).
- Stage 2 surfaces stage-2 stderr verbatim (trimmed) when the runner reports non-zero; stage 1 surfaces a static "preview stage" message (matches the v2026.5.3-1 stderr regression already pinned).
- New public record `PreviewParseResult` (sibling to `PendingDeviceApprovalResult`).

`tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs`:

- `RecordingWslRunner` upgraded from single-result to **queue** of `WslCommandResult`s and now records **all** `RunInDistroAsync` invocations into `RunInDistroCommands` for argv-shape assertions across both stages.
- Existing `DoesNotPassUrlOverride…` test updated for two-stage shape (asserts both stages omit `--url`, both contain `--token`/`--json`, token never on argv).
- Existing `NonZeroExit_SurfacesStructuredFailureCode` test updated for the new "preview stage" failure message.
- New regression tests:
  - `WslGatewayCliPendingDeviceApprover_TwoStage_PreviewThenCommit_Succeeds` — pins both stages' argv (stage 1: `--latest`, no requestId; stage 2: shell-quoted requestId, no `--latest`).
  - `WslGatewayCliPendingDeviceApprover_TwoStage_PreviewEmpty_NoPendingEntries` — stage 2 must **not** run; result code is `no_pending_entries`.
  - `WslGatewayCliPendingDeviceApprover_TwoStage_CommitFails_SurfacesStructuredFailure` — stage-2 stderr surfaces verbatim under `operator_pending_approval_failed`.
  - `WslGatewayCliPendingDeviceApprover_TwoStage_PreviewReturnsUnsafeRequestId_DoesNotRunCommit` — refuses interpolation of a requestId containing `;`/whitespace.
  - `ParsePreviewJson_V20265_Shape_ReturnsRequestId`
  - `ParsePreviewJson_Empty_ReturnsNoPendingEntries`
  - `ParsePreviewJson_OkFalse_ReturnsApprovalFailure`

## Test counts (per AGENTS.md)

| Suite | Count | Result |
|---|---|---|
| `./build.ps1` | (Cli + Shared + WinUI + WinNodeCli) | **all green** |
| `dotnet test tests/OpenClaw.Tray.Tests` | **502 / 502** | passed |
| `dotnet test tests/OpenClaw.Shared.Tests` | **1180 / 1180** | passed |

Tray test count rose from the prior 495 to 502 (+7 new approval tests, two existing tests updated in place — net +7).

## Notes for Bostick's next verification (Path B re-drive on `6942a81`)

**Pre-conditions / state hygiene:**

- The OpenClawGateway distro from your last Path B drive can be reused as-is for a quick "does the new commit reach paired.json" smoke test, BUT the existing pending state is muddied: `paired.json` already contains the manually-injected `c5979c9c…` entry from your diagnosis, and `pending.json` only has the gateway's own internal repair entry (`92471459…`, `isRepair=true`) which the CLI filters out. The engine will hit `no_pending_entries` from stage 1 against this state — that's the new structured failure surfacing correctly, but it is **not** a green end-to-end signal.
- For a clean end-to-end verification I recommend a destructive reset before re-driving:
  - `scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean` (same as the previous Path B drive — it preserves the 17 prototype distros).
  - This wipes `OpenClawGateway`, AppData, and the tray's `device-key-ed25519.json` so the engine generates a fresh deviceId and the gateway records a fresh non-repair pending entry that the new two-stage approver will discover and commit.

**What to look for in the live drive:**

- At Phase 12 (PairOperator) the gateway's `pending.json` should briefly contain the tray's new deviceId with `isRepair=false`.
- The engine should fire `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` and you should see **two** `wsl.exe -d OpenClawGateway -- bash -lc <…>` invocations in close succession (preview, then commit). The second one should contain a UUID where the first one had `--latest`.
- After the commit returns, `~/.openclaw/devices/paired.json` should grow to include the tray's deviceId entry **without manual intervention**, and the engine should advance to Phase 13.
- Windows-side `settings.json` should populate `Token` (operator token returned by `device.pair.approve`) and the tray should be able to reconnect with the stored device token.

**Failure-mode signals to watch (so we don't misdiagnose):**

- `operator_pending_approval_failed` with `ErrorMessage = "Local gateway pending pairing approval CLI failed (preview stage)."` → stage-1 exit non-zero, same as previous Bug 1 part 2 stderr surface.
- `no_pending_entries` → stage 1 ran cleanly but found nothing approvable; usually means the gateway's pending state didn't include the tray's request when the approver ran (timing / repair-only state). On a fresh distro this should not happen.
- `operator_pending_approval_failed` with stderr text in `ErrorMessage` (e.g. `"unknown requestId"`) → stage 1 succeeded but stage 2 itself rejected the requestId; this is genuinely new ground and warrants a fresh diagnosis.

**Branch state:** `feat/wsl-gateway-clean` is local-only (not pushed). HEAD is `6942a81`. 17 prototype WSL distros untouched.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
