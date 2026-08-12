# Codex Catalog Permission Hardening Design

## Goal

Make Codex catalog revocation fail closed at dispatch time and persistence time, while preventing production callers from bypassing the interactive permission boundary.

## Design

`NodeCapabilityRegistry` owns a revocable access generation for every advertised Codex capability. A generated Codex capability links each execution to that generation's cancellation token. Rebuilding or refreshing to a mode that removes/replaces Codex first cancels the prior generation, then atomically publishes the replacement snapshot. Transport code already propagates cancellation and will not deliver a normal result after cancellation.

Raw App Server construction and read methods become internal to `OpenClaw.Shared`. The Tray assembly and test assemblies retain explicit friend access, while production access remains through `NodeCapabilityRegistry` and `CodexSessionCapability` only.

`SettingsManager.UpdateAndSave` reports persistence success. `SettingsStore` and `SettingsPageViewModel` preserve the UI-safe no-throw setter behavior but do not refresh the runtime catalog or claim a save when the Codex setting change was not durably persisted. The in-memory setting is restored to its previous value on a failed Codex access save.

## Tests

- Hold a deferred Codex operation, revoke access, and prove cancellation prevents completion/delivery.
- Compile/source-contract tests prove raw resolver/client/list APIs are internal and only the Tray/test assemblies have friendship.
- Force a Codex access persistence failure and prove the stored and runtime values remain ReadOnly, no refresh occurs, and the setter remains safe for two-way binding.
- Add a behavioral local settings API denial assertion for `CodexSessionAccess`.

## Constraints

- Preserve exactly the two bounded read commands.
- Never enable ReadAndSteer or change Gateway `allowWriteControls`.
- No caller-selected executable, environment, or Codex home override.
- Keep existing non-security settings save behavior unchanged unless the tests require a narrow Codex-access change.
