# Aaron — Pairing flow design audit (Mike's two questions)

**Branch audited:** `feat/wsl-gateway-clean` @ `6e532f7` (worktree `openclaw-wsl-gateway-clean`)
**Upstream audited:** `openclaw/openclaw` @ `0c977cd` / `10725c9` (HEAD of `main` at audit time)
**Date:** 2026-05-05
**Author:** Aaron (backend / infra)
**Scope:** Design audit only — no code changes, no remote pushes, no tray/distro touches.

---

## Q1 — Does QR auto-pair already give us BOTH tokens? Are we re-pairing for the node role for no reason?

### TL;DR

**Yes. Mike is right. We drifted.**

The QR mints **one** `bootstrapToken`, but it carries the **dual-role profile `["node","operator"]`**, and on approval the gateway mints **device tokens for BOTH roles** and sends them back in a single `hello-ok.auth.deviceTokens[]` array on the **same** connect handshake. The shared client (`OpenClawGatewayClient`) has the code to harvest both — but the tray's operator-pairing path passes `bootstrapPairAsNode: false`, which suppresses the node-token branch and drops the node token on the floor. Phase 14 then has no node device token, falls back to the bootstrap (already redeemed → server treats it as `isRepair=true` role-upgrade pending), and Bug 3's auto-approver kicks in to manually approve the second pending request.

**The two-stage approve + role-upgrade approach is doing work the QR already did for us.**

### Code citations

#### 1. The QR mint (`openclaw qr --json`) returns ONE bootstrap token + URL — not two role tokens

Tray invocation: `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:1555-1572` (`WslGatewayCliBootstrapTokenProvider.MintAsync`):

```csharp
"exec",
ShellQuote(_commandName),    // openclaw
"qr",
"--json",
"--url",
ShellQuote(state.GatewayUrl)
```

Upstream handler — `openclaw/openclaw` `src/cli/qr-cli.ts` lines ~196-204:

```ts
defaultRuntime.writeJson({
  setupCode,
  gatewayUrl: resolved.payload.url,
  auth: resolved.authLabel,
  urlSource: resolved.urlSource,
});
```

The payload that backs `setupCode` is — `src/pairing/setup-code.ts:30-33`:

```ts
export type PairingSetupPayload = {
  url: string;
  bootstrapToken: string;   // ← single token
};
```

…minted via `issueDeviceBootstrapToken({ ..., profile: PAIRING_SETUP_BOOTSTRAP_PROFILE })` (`setup-code.ts:336-345`).

#### 2. …but the bootstrap profile is dual-role

`openclaw/openclaw` `src/shared/device-bootstrap-profile.ts:22-25`:

```ts
export const PAIRING_SETUP_BOOTSTRAP_PROFILE: DeviceBootstrapProfile = {
  roles: ["node", "operator"],
  scopes: [...BOOTSTRAP_HANDOFF_OPERATOR_SCOPES],
};
```

#### 3. On approval the gateway mints a device token for EVERY role in the profile

`openclaw/openclaw` `src/infra/device-pairing.ts:725-739` (`approveBootstrapDevicePairing`):

```ts
const tokens = existing?.tokens ? { ...existing.tokens } : {};
for (const roleForToken of approvedRoles) {        // ["node","operator"]
  const existingToken = tokens[roleForToken];
  const tokenScopes =
    roleForToken === OPERATOR_ROLE
      ? resolveBootstrapProfileScopesForRole(roleForToken, approvedScopes)
      : [];
  tokens[roleForToken] = buildDeviceAuthToken({
    role: roleForToken,
    scopes: tokenScopes,
    existing: existingToken,
    now,
    ...
  });
}
```

So **`paired.json` ends up with both `device.tokens.operator` and `device.tokens.node` after a single approve.** Mike's hypothesis confirmed at the gateway layer.

#### 4. The `hello-ok` handshake response carries BOTH tokens to the client in a single message

`OpenClawGatewayClient.cs:1192-1218` reads from `auth.deviceTokens[]`:

```csharp
if (!string.IsNullOrWhiteSpace(preferredRole) &&
    authPayload.TryGetProperty("deviceTokens", out var deviceTokens) &&
    deviceTokens.ValueKind == JsonValueKind.Array)
{
    foreach (var entry in deviceTokens.EnumerateArray())
    {
        ...
        if (entry.TryGetProperty("role", out var role) && ...
            string.Equals(role.GetString(), preferredRole, StringComparison.OrdinalIgnoreCase) &&
            entry.TryGetProperty("deviceToken", out var roleToken) ...)
        {
            return roleTokenValue;
        }
    }
    ...
}
```

#### 5. …and the client KNOWS how to harvest both — but only when `_bootstrapPairAsNode == true`

`OpenClawGatewayClient.cs:813-835` (the `hello-ok` handler):

```csharp
if (_bootstrapPairAsNode)
{
    var nodeDeviceToken = TryGetHandshakeDeviceTokenCore(payload, "node", allowDirectDeviceTokenFallback: true);
    if (!string.IsNullOrWhiteSpace(nodeDeviceToken))
    {
        var nodeDeviceTokenScopes = TryGetHandshakeDeviceTokenScopesCore(payload, "node", allowDirectDeviceTokenFallback: true);
        _deviceIdentity.StoreDeviceTokenForRole("node", nodeDeviceToken, nodeDeviceTokenScopes);
        _logger.Info("Node device token stored for Windows tray node reconnect");
    }
}

var newDeviceToken = _bootstrapPairAsNode
    ? TryGetHandshakeDeviceTokenCore(payload, OperatorRole, allowDirectDeviceTokenFallback: false)
    : TryGetHandshakeDeviceTokenCore(payload, preferredRole: null);   // ← scalar fallback only
```

#### 6. …but our operator path explicitly turns that off

`LocalGatewaySetup.cs:1355` (`SettingsGatewayOperatorConnector.ConnectAsync`):

```csharp
using var client = new OpenClawGatewayClient(
    gatewayUrl, token, _logger, tokenIsBootstrapToken,
    bootstrapPairAsNode: false);   // ← node token harvest disabled
```

Result: when Phase 12 runs `bootstrap connect → approve → hello-ok`, the **node deviceToken is sitting in the `auth.deviceTokens[]` array of the very same `hello-ok` we just processed, and we throw it away**. Phase 14 then has no node deviceToken, falls back to `(operatorToken, bootstrapToken)`, and the bootstrap-token retry hits the gateway's `isRepair=true` branch (`device-pairing.ts:515` — `const isRepair = Boolean(state.pairedByDeviceId[deviceId])`), which parks the connect on the pending list. Bug 3's auto-approver (`LocalGatewaySetup.cs:2156-2196`) was the symptomatic fix.

### How we drifted

The journey, in one paragraph: Round-2 work focused on Bug 1 (operator pairing) in isolation. We knew the bootstrap token returned by `openclaw qr` was a *single* token — the QR JSON literally has one `bootstrapToken` field — and we treated each role's connect as if it had to drive its own pairing event. The original prototype `OpenClawGatewayClient` was actually designed to harvest both tokens in a single connect (lines 813-835 — that code predates this work), but it gates the harvest on `_bootstrapPairAsNode`, which is set by *whichever role drives the bootstrap connect first*. Our new flow drives operator first with `bootstrapPairAsNode: false`, so the symmetric path (operator-first connect harvests the node token too) was never wired. When Phase 14 then surfaced as "role-upgrade pending" (Bug 3, 2026-05-05), it looked like an upstream gateway state issue and we extended the existing approver to fix the symptom rather than questioning the structural premise. The CLI v2026.5.3-1 changes (preview-only `--latest`, deterministic exit-1) are real and orthogonal — they are the cause of Bug 1's two-stage approve dance, but they didn't change the QR's payload or the dual-role profile. Those have been stable.

### What the simpler design looks like

Two surgical changes (estimated ~30-60 LOC net deletion):

1. **Make `OpenClawGatewayClient` always harvest all `auth.deviceTokens[]` entries** on `hello-ok`, regardless of `_bootstrapPairAsNode`. The flag stays useful for which role to *connect as first*, but token storage becomes role-agnostic. Cite: `OpenClawGatewayClient.cs:813-835` — restructure the conditional so both `"node"` and `"operator"` entries are stored when present.

2. **`SettingsWindowsTrayNodeProvisioner.PairAsync`** (`LocalGatewaySetup.cs:2139-2199`) connects with the **stored node deviceToken** (already on disk after the change above) — not with `_settings.Token` / `_settings.BootstrapToken`. Then **delete the entire role-upgrade auto-approve fallback** (lines 2156-2195) — there's no more pending request because the node connect authenticates with a real, already-approved deviceToken on the first try.

`WindowsNodeClient` already has stored-token reconnect plumbing (`HasStoredNodeDeviceToken`, `LocalGatewaySetup.cs:2208`). Net effect: Phase 14 becomes a single, deterministic `connect-with-deviceToken` — no second approval, no `isRepair`, no fallback retry.

### Q1 recommendation: **small follow-up PR before merge**

- Risk of shipping as-is: works (Bostick-11 Round-5 verified the role-upgrade auto-approver). But it codifies an unnecessary second pending-approval into the canonical local-loopback flow, doubling the surface area of the same race we're already retrying around in Bug 1.
- Risk of the simpler fix: the multi-deviceToken harvest path is untested in the operator-first ordering. Needs a focused integration test (Phase 12 → assert both `device.tokens.operator` and node deviceToken stored on Windows side → Phase 14 connect succeeds with no pending request created).
- Estimated effort: ~half a day. Worth it before merge — the role-upgrade branch is going to bitrot.

---

## Q2 — Why does operator pairing need retries? Should it not be deterministic in a fresh local-loopback install?

### TL;DR

**The 750 ms stage-1 retry is now mostly vestigial.** After Aaron-21's gate-fix (`4d36dcd`) — "treat parseable preview JSON as success regardless of exit code" — the deterministic CLI-v2026.5.3-1 happy path **does not enter the retry branch at all**, so on the common case the retry costs zero latency. The Bostick-11 race it was originally written for ("first `--token` call auto-bootstraps the internal Linux operator and exits non-zero with empty stdout") has not been observed reproducing since the part-5 token-read fix (`f2dec42`) AND the part-6 gate-fix landed. There's no documented post-Aaron-21 case where attempt 1 is unparseable and attempt 2 succeeds. **Mike's intuition is right: in a fresh local-loopback install operator pairing should be deterministic.**

### Code citations

The current retry guard — `LocalGatewaySetup.cs:1789-1825` (`RunStage1WithRetryAsync`):

```csharp
var first = await _wsl.RunInDistroAsync(...);
// Bug 1 part 6: treat exit-0 OR a parseable preview JSON as stage-1 success.
if (first.Success || ParsePreviewJson(first.StandardOutput).Success)
{
    return new Stage1Outcome(first, FirstResult: null);   // ← happy path, no retry
}

// Bug 1 part 4: failed first call may have triggered auto-pair side effect; retry once.
if (_stage1RetryDelay > TimeSpan.Zero)
{
    try { await Task.Delay(_stage1RetryDelay, cancellationToken); }
    catch (TaskCanceledException) { return new Stage1Outcome(first, FirstResult: first); }
}
var second = await _wsl.RunInDistroAsync(...);
return new Stage1Outcome(second, FirstResult: first);
```

The CLI happy-path returns exit=1 + valid preview JSON. `ParsePreviewJson(first.StandardOutput).Success == true`, so the early-return at line 1800-1803 fires and the 750 ms backoff is never hit on the deterministic happy path. This is exactly what the part-6 comment claims (lines 1795-1799) and is consistent with the Bostick-11 Round-4 evidence in `bostick-bug1-reverify.md`.

The retry branch only fires when **stdout is genuinely empty/unparseable AND exit non-zero**. The race documented at lines 1659-1664 (Round-3) showed both attempts producing empty stdout — i.e., the retry was NOT actually saving us; the part-5 token-read fix did. After part-5 + part-6 landed, the retry has had no visible firing mechanism documented.

### Underlying race assessment

The Bostick-11 Round-2 race ("CLI auto-bootstraps internal Linux operator on first `--token` call → CLI exits non-zero") is real and orthogonal to the gateway-token shell-quoting bug. With the part-6 gate, that race is now a **non-event**: even if the CLI does an internal bootstrap on first call and exits 1, it still emits the preview JSON (the bootstrap is a side-effect of `--latest` selection, not of the JSON serializer), so `ParsePreviewJson` succeeds and we proceed to stage 2 immediately. There is no longer a user-visible failure mode I can find evidence for.

There is one residual scenario the retry could still defend against: a freshly-restarted gateway where state hasn't loaded by the time stage 1 runs (truly empty stdout, no preview to parse). This is rare in the canonical flow (Phase 11 starts the gateway, Phase 12 polls health, Phase 13 mints, only THEN Phase 14 approves) — but not impossible.

### Q2 recommendation: **ship as-is, file follow-up**

- The retry costs zero latency on the deterministic happy path (gate-fix bypass at line 1800).
- Removing it now is risky without telemetry: we don't know if there's a low-volume race that the retry is silently masking, because the failure path doesn't log the "first failed, second succeeded" transition distinctly from the happy path.
- **Follow-up to file post-merge:** add a counter / log line at line 1820 (`_logger.Info($"stage-1 first attempt failed (exit={first.ExitCode} stdout-empty={...}), retrying after {delay}ms")`). Run for one release cycle. If telemetry shows zero retry-saves in the wild, delete the retry branch in a single-commit cleanup PR.
- Mike's intuition that operator pairing should be deterministic is correct for the *canonical* local-loopback flow on a fresh install — and it now is, post-gate-fix. The retry is belt-and-suspenders insurance, not active fault-tolerance.

---

## Summary

| Question | Mike's hypothesis | Verdict | Recommendation |
|---|---|---|---|
| Q1 — QR returns both tokens? | YES | **Confirmed.** Single bootstrap → dual-role profile → both deviceTokens minted on approval and shipped in one `hello-ok`. Tray drops the node token on the floor (`bootstrapPairAsNode: false`), forcing Phase 14 into a needless second approval. | **Small follow-up PR before merge.** ~30-60 LOC net deletion. Harvest both deviceTokens at Phase 12; remove the role-upgrade approver fallback at Phase 14. |
| Q2 — Why retries on operator pairing? | Intuition that it shouldn't be needed | **Mostly correct.** Post Aaron-21 + Aaron-22 fixes the deterministic happy path bypasses the retry entirely (zero latency). The retry is vestigial defensive code from before the gate-fix; no documented post-fix saves. | **Ship as-is.** File follow-up: instrument the retry firing path; delete branch after one release cycle if telemetry shows zero saves. |

### Owning the Q1 drift directly

I missed it. The QR's dual-role profile and the symmetric `auth.deviceTokens[]` harvesting in `OpenClawGatewayClient` were both already in the tree when I started Round-2 work on Bug 1. I focused on the in-distro CLI exit-code semantics (the real CLI-v2026.5.3-1 surprise) and didn't re-examine the role pairing topology. When Bug 3 surfaced as "role-upgrade pending" on 2026-05-05, the most expedient fix was to extend the same approver — which is what `6e532f7` does. It works, but it codifies a workaround for a self-inflicted client-side gap. That's a smell. We should fix it before merge so the canonical pairing flow doesn't bake in a redundant approval round-trip.
