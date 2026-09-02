---
name: gui-e2e
description: "Run and extend the automated GUI end-to-end lane (FlaUI over the real OpenClaw tray, fake or real gateway, Azure Windows via Crabbox). Use when a change touches tray, Settings, onboarding, chat, permissions, sandbox, connection UI, or when the user asks for GUI E2E coverage."
---

# GUI end-to-end lane

Status: **planned, not yet implemented.** The design lives in
`tests\OpenClaw.GuiE2ETests\PLAN.md`; verified facts for implementers are in
`tests\OpenClaw.GuiE2ETests\IMPLEMENTATION_NOTES.md`; the Phase 0 starting
prompt is `tests\OpenClaw.GuiE2ETests\KICKOFF_PROMPT.md`. Fill in the sections
below as each phase lands and remove this status line in Phase 3.

## When to use

- A change touches any hub page, the setup wizard, tray menu, chat surface,
  dialogs, or connection/pairing UI.
- A PR declares the `windows-winui-interactive` or `windows-wsl-gateway-e2e`
  proof pool and the automated `run-gui-e2e` command exists.
- The user asks to add or run a GUI scenario.

## Run locally (Phase 1+)

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\scripts\gui-e2e\Invoke-GuiE2E.ps1 -Tier Fake
```

Results: `TestResults\ProofPools\gui-e2e-fake\gui-e2e-fake.trx`, screenshots and
redacted logs under `TestResults\GuiE2E\<run>\`. The script refuses zero-test runs.

## Run on Azure via Crabbox (Phase 2+)

Requires the Crabbox CLI and Azure auth as described in
`.agents\skills\crabbox\SKILL.md`. UI tests need a desktop lease
(`warmup --desktop`); the script launches the suite inside the interactive
session through a scheduled task and polls for completion.

```powershell
.\scripts\gui-e2e\Invoke-GuiE2E-Crabbox.ps1 -Tier Fake
```

Always report the provider and lease id, and confirm the lease was stopped.

## Add a scenario (Phase 1+)

1. Add or verify AutomationIds in `src` (PascalCase `<Surface><Element>[Action|Toggle|Input|Marker]`).
2. Add or extend a page object in `tests\OpenClaw.GuiE2ETests\Pages\`.
3. Write the test in `Scenarios\<Workflow>Scenarios.cs` with `[GuiE2EFact]` and `Tier`, `Workflow`, `Pool` traits.
4. Add the entry to `Catalog\gui-e2e-catalog.json`; run `dotnet test --filter FullyQualifiedName~Catalog`.
5. Regenerate the docs table with `.\scripts\gui-e2e\Export-GuiE2ECatalog.ps1`.

## Rules

- Never run against real `%APPDATA%\OpenClawTray`; the harness always isolates state.
- Capture app windows only; never copy `settings.json`, `gateways.json`, or device key files into artifacts.
- A skipped or quarantined scenario is not proof. Report blockers explicitly.
