# Aaron fix ABC summary

Date: 2026-05-11T11:25:00-07:00
Requested by: Mike Harsh
Branch: fix/bootstrap-injector-properly
PR: #312

## What changed

- Bug A: `GatewayConnectionManager` now supports an active-gateway-aware node-start predicate. `App.ShouldInitializeNodeService(GatewayRecord, string)` suppresses the PR #304 manager-owned `NodeConnector` when the active gateway is local and the canonical local `NodeService` already owns a stored node token under `IdentityDataPath`. The remote/no-local-setup path is preserved.
- Bug B: `BootstrapMessageInjector.InjectAsync(...)` now has an in-process in-flight guard and re-checks `HasInjectedFirstRunBootstrap` after the initial delay, before executing JavaScript.
- Bug C: bootstrap status handling now distinguishes `sent` from `rendered`; only `sent` consumes `HasInjectedFirstRunBootstrap`. `rendered` logs that the composer was filled but send was not confirmed, leaving the gate open for retry.

## Tests added/updated

- Manager-owned connector is suppressed for a local gateway when the local node owner is active.
- Manager-owned connector still starts for a remote gateway.
- Concurrent bootstrap injections execute JavaScript exactly once.
- `rendered` does not consume the bootstrap one-shot gate.

## Manual smoke still required

Mike must complete the real WSL wizard path and verify: A) tray opens Hub Chat after wizard completion, B) Chat connects to the gateway, C) hatching prompt renders and submits with an assistant reply, D) no second pairing notification appears after Chat opens, and E) `wsl -d OpenClawGateway -- cat ~/.openclaw/devices/paired.json` contains exactly one Windows-node entry.