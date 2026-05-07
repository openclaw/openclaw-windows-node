# Aaron — Bug 3 — Phase 14 role-upgrade auto-approve

**Date:** 2026-05-05
**Branch:** `feat/wsl-gateway-clean` (local-only, not pushed)
**Commit:** `6e532f7` — `fix(setup): wire pending-device approver into Phase 14 role-upgrade pairing (Bug 3)`
**Worktree:** `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`
**Builds on:** Aaron-21 part-6 (Bug 1 final) at `4d36dcd`
**Acting on:** Bostick-11 Round-5 (`bostick-bug1-reverify.md` "## Path B re-drive — Round 5", lines 612–739)

## Phase 14 root cause (cite Bostick's evidence)

> Bostick-11 Round 5: "NEW failure surface at Phase 14 (PairWindowsTrayNode):
> `FailureCode=windows_node_pairing_failed`, `UserMessage=\"Timed out waiting for the
> Windows tray node to pair with the gateway.\"` This is **NOT a regression of Bug 1**
> — it's a separate, previously-masked auto-approve gap for the **node role-upgrade**
> pairing."
>
> Round-5 timeline: "Tray attempts repeated WS connects as a NODE-role client. Each
> connect gets gateway response: `NOT_PAIRED, reason=role-upgrade,
> requestId=a80b5dbe-9ad2-4a32-baa9-d7d93aeb50dc, message=\"pairing required: device
> is asking for a higher role than currently approved\"`. The node-role connection
> adds a NEW pending entry (deviceId `1da8cb85eea2c742…`, role `node`, isRepair
> `true`) but no auto-approve fires for it."

The Phase-12 fix Aaron-21 shipped (`4d36dcd`) handled the operator-pairing pending
entry but Phase 14's `SettingsWindowsTrayNodeProvisioner.PairAsync` had no analogous
seam. When `IWindowsNodeConnector.ConnectAsync` threw `TimeoutException` (because
the node-role connect was being NOT_PAIRED'd by the gateway), the engine surfaced
`windows_node_pairing_failed` and stopped. The pending node entry sat unaddressed in
`pending.json`.

## Wiring approach: direct reuse of `WslGatewayCliPendingDeviceApprover`

**Direct reuse, no new approver class.** Bostick-11 Round-5 explicitly called this
out: "The Phase-12 fix Aaron just shipped is almost certainly directly reusable."
After inspection, the same `IPendingDeviceApprover` instance fits Phase 14
unchanged — `--latest` picks the most recently created pending entry, which on the
Phase-14 happy path IS the node role-upgrade entry created by the just-failed
connect (the operator entry from Phase 12 is already in `paired.json`, not
`pending.json`).

### Why direct reuse, not a wrapper or new class

1. The CLI `openclaw devices approve --latest --json --token …` is role-agnostic.
   It approves whatever's at the top of the pending list.
2. The two-stage preview→commit dance (Bug 1 parts 3 + 6) is identical for
   role-upgrade entries — the preview shape carries `selected.requestId`, the
   commit takes the explicit requestId. No new code path is needed in the
   approver.
3. Bostick noted `pending.json` may transiently hold both an internal
   operator-repair entry (`04e4f494-…`) and the node role-upgrade entry
   (`a80b5dbe-…`). The role-upgrade entry is the most recent (created during
   Phase 14), so `--latest` resolves to it deterministically. If a future
   regression shows tie-breaking is ambiguous, the explicit-requestId form
   (`approve <requestId>`) is already supported by the same approver — switching
   would be a one-line change.
4. Adding a wrapper or new class would create two near-identical CLI shells with
   their own diagnostic-surface drift risk. Bug 1 took 6 rounds to stabilise the
   diagnostic surface; we don't want to fork that.

### What changed

`SettingsWindowsTrayNodeProvisioner` now takes an optional
`IPendingDeviceApprover` (defaults to `null` for backwards compatibility). In
`PairAsync`:

- First connect attempt as before.
- On exception (other than `OperationCanceledException`):
  - If `_pendingApprover != null` AND `LocalGatewayApprover.IsLocalGateway(state.GatewayUrl)`:
    - Call `_pendingApprover.ApproveLatestAsync(state, cancellationToken)`.
    - If approval fails → return `ProvisioningResult(false, approval.ErrorCode, approval.ErrorMessage)`
      (mirrors Phase 12's "approval-error wins over pairing-required-error" pattern from
      `SettingsOperatorPairingService.PairAsync`).
    - If approval succeeds → retry connect once. On retry exception, return
      `windows_node_pairing_failed` with the retry exception's message.
  - Else (remote gateway OR no approver wired) → return `windows_node_pairing_failed`
    unchanged (legacy surface preserved).

Wired in `LocalGatewaySetupEngine.Build()`: the same `pendingDeviceApprover` instance
already constructed for `SettingsOperatorPairingService` is now also passed to
`SettingsWindowsTrayNodeProvisioner`.

### File changes

| File | Change |
|---|---|
| `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` | `SettingsWindowsTrayNodeProvisioner` ctor + `PairAsync` accept and use `IPendingDeviceApprover`; `Build()` passes the existing approver instance through. +43 / -5 lines. |
| `tests/OpenClaw.Tray.Tests/WindowsTrayNodePairingApprovalTests.cs` | NEW. 8 tests + 3 fakes. 216 lines. |

**Net diff:** 2 files changed, 259 insertions(+), 5 deletions(-).

## Tests added

All in `WindowsTrayNodePairingApprovalTests.cs`:

1. `PairAsync_LocalLoopback_RoleUpgradePending_ApprovesAndRetries_Succeeds` — happy
   path. Connect throws TimeoutException → approver fires once with the right
   gateway URL + distro → retry connect succeeds → result.Success=true,
   EnableNodeMode=true, exactly 2 connect calls, 1 approver call.
2. `PairAsync_LocalLoopback_RoleUpgradePending_ApproverFails_SurfacesStructuredFailure`
   — approver returns Bug-1-shape commit-stage failure → result surfaces
   `operator_pending_approval_failed` + the `stage2.exit=1` diagnostic. No retry
   connect attempted (exactly 1 connect call).
3. `PairAsync_LocalLoopback_RoleUpgradePending_ApproverNoPendingEntries_SurfacesStructuredFailure`
   — approver returns `no_pending_entries` (the case Bostick worried about: nothing
   in `--latest` to approve). Result surfaces `no_pending_entries` verbatim, not
   masked behind `windows_node_pairing_failed`.
4. `PairAsync_LocalLoopback_RetryAfterApproveAlsoFails_SurfacesPairingFailed` —
   approver succeeds but retry connect throws too. Result surfaces
   `windows_node_pairing_failed` with the retry exception's message (not the first
   exception's). Pins the retry-doesn't-loop invariant.
5. `PairAsync_RemoteGateway_ConnectFails_DoesNotApprove` — remote gateway URL
   (`ws://gateway.example.com:18789`). Connect fails → no approver call → legacy
   `windows_node_pairing_failed` surface preserved (Bug-1's remote-gateway opt-out
   pattern carried forward to Bug-3).
6. `PairAsync_LocalLoopback_FirstConnectSucceeds_DoesNotApprove` — happy path
   without a pending entry. 1 connect call, 0 approver calls.
7. `PairAsync_LocalLoopback_NoApproverWired_PreservesLegacyFailureCode` — legacy
   caller passing `pendingApprover: null`. Local gateway connect fails → legacy
   `windows_node_pairing_failed` surface preserved unchanged.
8. `PairAsync_OperationCanceled_DoesNotApproveOrSwallow` — cancellation must
   propagate. `OperationCanceledException` from the connector escapes
   `PairAsync` (no approver call, no swallow).

## Validation results (per AGENTS.md)

| Step | Command | Result |
|---|---|---|
| 1 | `./build.ps1` | **all green** (Cli + Shared + WinNodeCli + WinUI) |
| 2 | DLL freshness — `Get-Item …\bin\x64\Debug\…\OpenClaw.Tray.WinUI.dll` | `OpenClaw.Tray.WinUI.dll` ↦ **2026-05-04 23:59:03** (newer than Aaron-21's 23:59:03 — same minute, fresh build artifact) |
| 3 | `dotnet test tests\OpenClaw.Tray.Tests` (with `OPENCLAW_REPO_ROOT`) | **524 / 524** passed (516 baseline + 8 new) |
| 4 | `dotnet test tests\OpenClaw.Shared.Tests` (with `OPENCLAW_REPO_ROOT` + `OPENCLAW_RUN_INTEGRATION=1`) | **1180 / 1180** passed |
| 5 | Commit on `feat/wsl-gateway-clean` | `6e532f7` |

No regressions. No untouched files in the working tree.

## Bostick handoff: pre-conditions for next Path B re-drive

**Reset required: YES — full destructive clean.** Round-5 left the gateway distro with
`paired.json` containing the operator entry AND `pending.json` containing the
unaddressed `a80b5dbe-…` node role-upgrade entry plus the `04e4f494-…`
operator-internal-repair entry. The tray's Windows-side state (`%LOCALAPPDATA%\OpenClawTray`)
is on the FailedRetryable page for `windows_node_pairing_failed`. If you reuse this state,
the engine will NOT exercise the Phase-14 fix path cleanly because the pending entries
are stale (their requestIds no longer correspond to a live tray-side connect attempt).

```powershell
scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean
```

(Same as Round-3 / Round-4 / Round-5 reset — preserves the 17 prototype distros, wipes
OpenClawGateway + AppData + tray device-key.)

**Tray PID 24848:** already terminated by Aaron-22 at the start of this work
(per task brief). No tray process running on the machine right now.

**No new env vars required.** Same launch as Round-5:

```powershell
$env:OPENCLAW_FORCE_ONBOARDING = "1"
$env:OPENCLAW_VISUAL_TEST = "1"
$env:OPENCLAW_VISUAL_TEST_DIR = "<repo>\visual-test-output\bug3-reverify-pathB-2026-05-05-round6"
$env:OPENCLAW_ONBOARDING_START_ROUTE = "LocalSetupProgress"
# OPENCLAW_VISUAL_TEST_LOCAL_SETUP deliberately UNSET — real engine
```

**Stale-build check (mandatory before launch):**

1. Run `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`.
2. `Get-Item …\bin\x64\Debug\…\OpenClaw.Tray.WinUI.dll | Select Name,LastWriteTime` — should be ≥ `2026-05-04 23:59:03`.
3. Identify Aaron-22 binaries by **commit SHA `6e532f7` in `git log --oneline -1`**, not by string scan. The wiring change adds no new diagnostic surface marker.

**Expected timeline on success (Phase 14 PairWindowsTrayNode):**

- Phases 1–13 unchanged from Round-5 (Phase 12 ~35s, Phase 13 sub-ms).
- Phase 14 starts. NodeService attempts connect as node role → first connect throws
  `TimeoutException` (gateway parks role-upgrade pending entry).
- `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` fires for the second time
  in this session (first was Phase 12). Same 3-invocation shape:
  `cat /var/lib/openclaw/gateway-token`, stage-1 preview (exit 1 + valid JSON, treated
  as success per Bug 1 part 6), stage-2 commit (exit 0, mutates `paired.json`).
  Approver picks `a80b5dbe-9ad2-4a32-baa9-d7d93aeb50dc` (or whatever requestId the
  Round-6 drive's connect produces) via `--latest`.
- `paired.json` now contains the tray's deviceId with BOTH operator scopes (from
  Phase 12) AND node scopes (from Phase 14).
- Retry connect succeeds → Phase 14 advances → Phase 15 (VerifyEndToEnd) → Phase 16/17
  (Complete). `setup-state.json.Status = Complete`.

**Failure-mode signals (so we don't misdiagnose if Round-6 is RED):**

- `windows_node_pairing_failed` on Phase 14 with the retry exception's message →
  the approver succeeded but the retry connect still failed. Likely cause: the
  approved entry was the wrong one (e.g. internal operator-repair entry) and the
  node role-upgrade entry is still pending. Switch approver to explicit-requestId
  form using the requestId from the gateway's NOT_PAIRED response.
- `operator_pending_approval_failed` on Phase 14 with stage2 / commit-stage
  diagnostic → approver itself failed at commit. Same diagnosis path as Bug 1
  part 6 failure modes (stderr/stdout/exit surfaced).
- `no_pending_entries` on Phase 14 → the node-role connect failed BEFORE creating
  a pending entry. Likely a gateway-side change in NOT_PAIRED handling. Investigate
  gateway WS protocol logs for the failed connect attempt.

**Branch state:** `feat/wsl-gateway-clean`, HEAD `6e532f7`, local-only (not pushed).
17 prototype WSL distros untouched. No changes to scripts, shared layer, onboarding
XAML, or engine wiring outside the provisioner. Two files modified:
`LocalGatewaySetup.cs` + new `WindowsTrayNodePairingApprovalTests.cs`.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
