# Codex Catalog Permission Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three Task 8 Important Codex catalog permission findings without widening the catalog surface.

**Architecture:** Registry-owned cancellation revokes in-flight capabilities; raw App Server surfaces are internal; Codex permission persistence is transactional from the ViewModel's perspective.

**Tech Stack:** .NET 10, C#, xUnit, existing Node capability/MCP/Gateway transports.

## Global Constraints

- Keep exactly `codex.appServer.threads.list.v1` and `codex.appServer.thread.turns.list.v1`.
- Do not enable ReadAndSteer or modify `allowWriteControls`.
- Follow RED/GREEN for every production behavior change.

### Task 1: Revoke active catalog executions

**Files:** `NodeCapabilityRegistry.cs`, `NodeCapabilityRegistryTests.cs`.

- [ ] Write a held-execution test that revokes ReadOnly and observes cancellation/no success delivery.
- [ ] Run the focused registry test and observe the missing generation cancellation RED.
- [ ] Add one registry-owned cancellation generation, cancel it before publishing a Codex-free/replacement snapshot, and link it in `DeferredCodexSessionCapability.ExecuteAsync`.
- [ ] Run the focused registry test GREEN.

### Task 2: Close raw policy bypasses

**Files:** `CodexAppServerClient.cs`, `CodexExecutableResolver.cs`, `CodexSessionCatalogService.cs`, assembly friendship configuration, focused source/API tests.

- [ ] Write a source/API contract test requiring raw resolver, client connection, and raw list methods to be internal.
- [ ] Run it RED against the current public declarations.
- [ ] Internalize the raw surface, retaining only required Tray and test friend assemblies.
- [ ] Run Shared/Tray focused tests GREEN.

### Task 3: Make access revocation persistence fail closed

**Files:** `SettingsManager.cs`, `SettingsStore.cs`, `SettingsPageViewModel.cs`, settings tests.

- [ ] Write a failing test that injects a save failure while changing ReadOnly to Off and asserts no runtime refresh or durable-mode mismatch.
- [ ] Run it RED.
- [ ] Add a narrow success result/rollback path for the Codex permission update; preserve safe two-way binding behavior.
- [ ] Run settings tests GREEN and add the local `app.settings.set` denial regression.

### Task 4: Re-review and closeout

- [ ] Run focused tests, the required build, and all three required suites.
- [ ] Dispatch scoped security and code-quality re-reviews, fix any Critical/Important issues with RED/GREEN loops.
- [ ] Update Task 8 reports and attempt interactive proof only if the security gates are clean.
