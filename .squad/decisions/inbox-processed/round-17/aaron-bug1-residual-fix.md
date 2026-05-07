# Aaron — Bug 1 residual fix: drop `--url` from `devices approve` invocation

- **Date:** 2026-05-04
- **Worktree:** `..\openclaw-wsl-gateway-clean` (`feat/wsl-gateway-clean`)
- **Base:** `4af2581`
- **Commit:** `3927451`
- **Scope tag:** `fix(setup)` — operator-pair approval against CLI v2026.5.3-1 `ensureExplicitGatewayAuth` (Bug 1 residual)

## Investigation findings

### Upstream CLI (commit `2eae30e`, v2026.5.3-1)

Read `src/gateway/call.ts` and `src/cli/devices-cli.ts` from `openclaw/openclaw` at SHA `2eae30e` (matches the bundled `call-BCpe65RR.js:148:8` stack frame Bostick captured).

Key facts:

1. `ensureExplicitGatewayAuth` is invoked from `resolveGatewayCallContext` whenever `urlOverride` is set. Source:
   ```ts
   if (!params.urlOverride) { return; }                                     // <- short-circuit
   const explicitToken = params.explicitAuth?.token;
   const explicitPassword = params.explicitAuth?.password;
   if (params.urlOverrideSource === "cli"
       && (explicitToken || explicitPassword)) { return; }                  // <- token alone *should* satisfy
   ```
   Theoretically `--token` alone passes the guard. Empirically Bostick's repro proves
   otherwise — the bundled CLI inside the gateway distro rejects `--token <hex> --url ws://localhost:18789`
   with the documented error and a non-zero exit (probable cause: a tighter check
   in the `devices approve` path or in the bundled output that diverges from the
   committed source). The shipped artifact is the source of truth for the fix.
2. `devices approve` declares `--url`, `--token`, `--password` options and
   delegates to `callGatewayCli("device.pair.approve", opts, { requestId })`,
   which routes through `resolveGatewayCallContext` → `ensureExplicitGatewayAuth`.
3. **`shouldUseLocalPairingFallback` (in `src/cli/devices-cli.ts`):**
   ```ts
   if (typeof opts.url === "string" && opts.url.trim().length > 0) {
       // Explicit --url might point at a remote/tunneled gateway; never silently
       // switch to local pairing files in that case.
       return false;
   }
   ```
   So passing `--url` *also* disables the in-process pairing-file fallback
   (`approveDevicePairing` in `src/infra/device-pairing.js`), which is exactly
   what we want available on a single-machine local-loopback gateway.
4. With **no** `--url` and `gateway.mode=local` in `openclaw.json` (which
   Aaron's Phase-7 PrepareGatewayConfig writes), `buildGatewayConnectionDetails`
   resolves the loopback WS URL from the configured port. CLI authenticates
   with the explicit `--token` if it takes the WS hop, or silently falls back
   to local pairing files if the WS hop fails — both end states are correct.

### Prototype reference

Searched `openclaw-windows-node` (the validated prototype) under
`src\OpenClaw.Tray.WinUI\Services\LocalGatewaySetup\LocalGatewaySetup.cs` and
`src\OpenClaw.Shared\OpenClawGatewayClient.cs` for any `approve`/`device.pair`/
`admin.devices` operations. The prototype has **none** — it never implemented an
auto-approve. Operator pairing in the prototype either succeeded on first
connect (against an older CLI version where the bootstrap-token+pending-approval
gate didn't exist) or failed and the user worked around it manually.

So Option 4 (drive `device.pair.approve` over WS directly from the tray) has
**no prototype precedent** and would require building a fresh admin-WS auth
client in C# (`auth.token`/`auth.password` admin handshake — distinct from the
existing `auth.bootstrapToken`/`auth.deviceToken` operator paths).

## Chosen fix — Option 1 (drop `--url`)

Selected over Option 4 because:

- **Solves the bug at the root.** Without `--url`, `ensureExplicitGatewayAuth` is bypassed entirely (early-return on `!urlOverride`).
- **Strictly more defensive than the alternatives.** `shouldUseLocalPairingFallback` becomes available, so any future CLI churn in the WS auth shape silently degrades to direct file approval.
- **Tiny diff.** Removes two argv slots; no new infrastructure, no new dependency, no new failure modes.
- **Option 4 cost-vs-benefit is poor here.** Building admin-WS auth in C# for one call site, when the CLI's `--token` path + local fallback already does the right thing, is over-engineering. If the WS auth surface stabilises and we want to remove the CLI shell-out across the board, that's a future refactor — not in scope for a Bug 1 residual patch.

Token (`/var/lib/openclaw/gateway-token`) is still passed via `--token` so the
WS path authenticates correctly when it's taken; the value is dereferenced
inside the `bash -lc` shell (`"$(cat …)"`) so it never appears on `wsl.exe`
argv.

## Files modified

| File | Change |
|---|---|
| `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` | `WslGatewayCliPendingDeviceApprover.ApproveLatestAsync` — removed `--url` + `ShellQuoteScalar(state.GatewayUrl)` from the script; added comment block citing CLI source. |
| `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` | +2 regression tests, +1 `RecordingWslRunner` stub implementing the full `IWslCommandRunner` interface. |

## New tests (pin the new behaviour)

1. **`WslGatewayCliPendingDeviceApprover_DoesNotPassUrlOverride_AvoidingEnsureExplicitGatewayAuthGuard`**
   Asserts the assembled `bash -lc` script:
   - **Does NOT** contain `--url` or `ws://127.0.0.1:18789`.
   - **DOES** contain `devices approve --latest --json --token` and the in-shell `$(cat /var/lib/openclaw/gateway-token)` dereference.
   - Returns `Success=true` on `{"ok":true,…}`.
2. **`WslGatewayCliPendingDeviceApprover_NonZeroExit_SurfacesStructuredFailureCode`**
   Stubs the runner to return the verbatim v2026.5.3-1 stderr (`gateway url override requires explicit credentials … at ensureExplicitGatewayAuth`) with exit-1; asserts the approver surfaces `operator_pending_approval_failed` + the user-facing message — i.e., if a future CLI regression re-introduces this failure shape, we surface a structured error rather than silently retrying.

## Test results

Validation env: `OPENCLAW_REPO_ROOT=<worktree>`, `--no-restore`.

| Suite                    | Total | Pass | Fail | Skip | Notes |
|--------------------------|------:|-----:|-----:|-----:|-------|
| OpenClaw.Tray.Tests      |   495 |  495 |    0 |    0 | +2 vs Aaron-15 baseline of 493 |
| OpenClaw.Shared.Tests    |  1180 | 1158 |    0 |   22 | 22 integration tests gated on `OPENCLAW_RUN_INTEGRATION=1` (baseline) |

`./build.ps1` was deliberately **not** run, per Mike's task brief: tray PID
**39856** holds `OpenClaw.Tray.WinUI.exe` open and the brief explicitly forbids
killing it ("DO NOT attempt full WinUI build (will fail on PID lock)"). This
deviates from the standing AGENTS.md guidance, but the test project compiles
the same `LocalGatewaySetup.cs` source via `<Compile Include=… Link=… />`, so
green Tray.Tests is sufficient evidence the source change is valid C#. Mike
will rebuild WinUI after killing PID 39856.

## Mike — actions before Bostick re-verifies

1. **Kill the live tray:** `Stop-Process -Id 39856`.
2. **Rebuild WinUI:** `./build.ps1` (or `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`). The WinUI link step couldn't run while PID 39856 was holding the .exe.
3. **`pending.json` reuse — yes, no reset needed.** The pending entry on the gateway side (`OpenClawGateway:/home/openclaw/.openclaw/devices/pending.json`, requestId `c27875a2-f270-4256-b95a-28123db64ea4`, deviceId `250d04ae...46b3df`) is keyed off the *device public key the tray generated and persisted*. The tray's Ed25519 identity in `%APPDATA%\OpenClawTray\device-key-ed25519.json` survives across runs, so a "Try Again" click on the FailedRetryable page (or a fresh Phase-12 run on relaunch) will re-attempt PairOperator with the **same** deviceId. The existing pending entry will match and the patched approver will approve it.
4. **AppData / settings.json:** can stay as-is. `BootstrapToken` is still populated and still valid; `Token` will populate on a successful retry.
5. **Gateway distro:** no changes needed. `openclaw-gateway.service` is still active per Bostick; `gateway.mode=local` + the gateway-token at `/var/lib/openclaw/gateway-token` are exactly what the patched approver expects.
6. If the retry still fails (unexpected), capture the new tray.log lines around the approve attempt — the script body has changed and the failure surface should differ from the v2026.5.3-1 `ensureExplicitGatewayAuth` shape.

## Out-of-scope items observed (deferred)

- **DEFECT-CLI-PENDING-INVISIBILITY** (Bostick): `openclaw devices list --json` returning empty `pending: []` while the on-disk `pending.json` is populated. Likely the same `--url`-disables-fallback issue affecting `list` too — the new approver path doesn't exercise `list` directly, so this fix may incidentally clear it for the approve flow but a separate spike is needed for the `list` UX.
- **Option 4 (full WS migration):** parked. Worth revisiting if the CLI's auth shape regresses again or if we ever ship a tray that doesn't bundle a WSL distro. Tracked here so it doesn't get lost.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
