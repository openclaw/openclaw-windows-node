# Aaron-15 — Bug 1 fix: bootstrap-token operator-pending auto-approve on local-loopback gateway

- **Date:** 2026-05-04
- **Worktree:** `openclaw-wsl-gateway-clean`
- **Base:** `73767c5`
- **Commit:** `fe2de09`
- **Scope tag:** `fix(shared)` — bootstrap-token wire-format consistency between gateway mint and tray pair (Bug 1 from e2e drive)

## Root cause

`MintBootstrapToken` correctly invokes `openclaw qr --json` and the tray sends
the resulting token back to the gateway via `auth.bootstrapToken` exactly as
minted. The token round-trips intact; the bug is **not** a wire-format mismatch
despite the failure surface (`cause:device-auth-invalid reason:device-signature`
on the first two attempts and `cause:pairing-required reason:not-paired` on the
third).

The real problem: on a fresh local-loopback gateway the upstream records the
bootstrap-token connect as a *pending* operator pairing request
(`~/.openclaw/devices/pending.json` entry with `deviceId`, `publicKey`, and
`role: "operator"`) and then rejects the same connect with `pairing-required`
because nothing has approved that pending entry yet. The first two
`device-signature` rejections are the client cycling through its
`HandleRequestError` signature-mode ladder
(V3AuthToken → V3EmptyToken → V2AuthToken → V2EmptyToken in
`OpenClawGatewayClient.cs`); the second attempt is what registers the pending
request server-side, and the third sees it pending → `pairing-required` → engine
gives up.

The upstream `gateway.nodes.pairing.autoApproveCidrs` policy (see
`node-pairing-auto-approve.ts`) checks `if (params.role !== "node") return false`
before its CIDR allow-list, so it does not auto-approve `operator` pairings.
The canonical operator pairing-approval path is `openclaw devices approve`,
which on a remote gateway is driven by a human approver in another UI. On a
local-loopback gateway the tray user IS the approver, so the tray engine must
drive that step itself.

## Code change (1 file, 3 surgical edits)

`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`:

1. **`SettingsOperatorPairingService` (~line 1440)** — added optional
   `IPendingDeviceApprover` constructor parameter. On the
   `PairingRequired` outcome, if the credential `IsBootstrapToken`, the
   gateway URL passes `LocalGatewayApprover.IsLocalGateway`, and an approver
   is wired, the service invokes `ApproveLatestAsync` and retries the connect
   exactly once. A second `PairingRequired` is surfaced as-is (no infinite
   loop). Approval-failure surfaces as a structured error and skips the retry.
2. **New types after `WslGatewayCliBootstrapTokenProvider` (~line 1645):**
   - `PendingDeviceApprovalResult` (record: `Approved`, `ErrorCode`, `Message`).
   - `IPendingDeviceApprover` (`ApproveLatestAsync(state, ct)` contract).
   - `WslGatewayCliPendingDeviceApprover` — invokes
     `openclaw devices approve --latest --json --url <state.GatewayUrl> --token "$(cat /var/lib/openclaw/gateway-token)"`
     via `IWslCommandRunner.RunInDistroAsync`. The gateway-admin token is
     dereferenced inside the distro shell so the value never appears on
     `wsl.exe` argv or in any process listing.
   - `ParseApproveJson` static helper (handles success, structured error, and
     malformed-output cases).
3. **`Build()` factory (~line 2256)** — instantiates the WSL approver and
   passes it to the pairing service constructor. Default null preserves
   remote-gateway behavior unchanged for any caller without a WSL runner.

## Tests

`tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` (NEW, 10 tests, all
green):

- `ApprovesPending_AndRetries_OnLocalGateway_BootstrapToken`
- `PairingRequiredTwice_DoesNotLoop`
- `ApprovalFailure_SurfacesErrorCode_NoRetry`
- `RemoteGateway_DoesNotApprove`
- `NonBootstrapToken_DoesNotApprove`
- `FirstConnectSucceeds_DoesNotInvokeApprover`
- 4 × `ParseApproveJson_*` cases (success / structured error / malformed /
  empty)

The test project pulls `LocalGatewaySetup.cs` directly via
`<Compile Include="..\..\src\..." Link="..." />`, so Tray.Tests build is
sufficient validation that the WinUI compile of the same file is clean.

## Validation

Env: `OPENCLAW_REPO_ROOT=<worktree>`, `OPENCLAW_RUN_INTEGRATION=1`.

| Suite                    | Total | Pass | Fail | Skip |
|--------------------------|-------|------|------|------|
| OpenClaw.Shared.Tests    | 1180  | 1180 | 0    | 0    |
| OpenClaw.Tray.Tests      | 493   | 493  | 0    | 0    |

(Tray.Tests +10 vs. pre-Aaron-15 baseline of 483.)

`./build.ps1` cannot complete the WinUI link step in this session because the
running OpenClaw tray (PID 8240, Mike's live debug session) holds
`OpenClaw.Tray.WinUI.exe` open and the task guardrails forbid stopping it.
`dotnet build` of `OpenClaw.Tray.WinUI.csproj` reports source compilation as
clean — only the post-link copy of the executable fails with `MSB3027` against
that PID. Mattingly's concurrent WIP in `LocalSetupProgressPage.cs`,
`LocalSetupProgressPolicy.cs`, and `OpenClaw.Tray.Tests.csproj` is also still
in flight in this worktree but has been left untouched.

## Redaction

No bootstrap-token or gateway-admin-token *values* appear in source, tests,
commit message, history, or this decision file. Tokens viewed during the
investigation (from `bootstrap.json` and `/var/lib/openclaw/gateway-token`)
have been redacted. All test fixtures use placeholder strings such as
`"redacted-bootstrap-token"`.

## Recommendations / open items

- Mike should re-run the e2e drive against a freshly-reset local gateway
  (clear `~/.openclaw/devices/pending.json` and re-trigger setup) to confirm
  the engine now passes the operator-pairing phase. Aaron-15 could not perform
  this verification because the running app at PID 8240 must remain in its
  broken state for Mike's inspection.
- If a future change moves the gateway-admin-token off
  `/var/lib/openclaw/gateway-token`, the path string in
  `WslGatewayCliPendingDeviceApprover` must move with it. The path is the
  one read by `LocalGatewaySetup`'s admin-token provider as well, so a single
  refactor would cover both call sites.
- Remote-gateway flows continue to surface `PairingRequired` to the user
  exactly as before — the new code path is gated on
  `LocalGatewayApprover.IsLocalGateway`.
