# Kickoff prompt: Phase 0 spike for the GUI E2E lane

Copy everything below the line into a fresh session (any capable coding model)
started at the repository root on the branch `dev/plarroy/gui-e2e-plan`.

---

You are implementing **Phase 0 only** of the plan in
`tests\OpenClaw.GuiE2ETests\PLAN.md`. Read that file and
`tests\OpenClaw.GuiE2ETests\IMPLEMENTATION_NOTES.md` first, then `AGENTS.md`.
Do not start Phase 1 work. When the acceptance criteria below are met, commit,
push to the `larroy-fork` remote on the current branch, and stop with a report.

## Goal

Prove that FlaUI (UIA3) can attach to the real `OpenClaw.Tray.WinUI.exe`
running with isolated state, navigate to the Connection page through the
existing deep-link path, find an element by AutomationId, and capture a
non-blank screenshot of the Hub window. This must work locally and on a
GitHub-hosted `windows-latest` runner.

## Scope: files you may create or modify

- `tests\OpenClaw.GuiE2ETests\OpenClaw.GuiE2ETests.csproj` (new; mirror
  `tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj`, add `FlaUI.Core`
  and `FlaUI.UIA3`, no `UseWinUI`, `NU1904` exemption, RIDs `win-x64;win-arm64`).
- `tests\OpenClaw.GuiE2ETests\xunit.runner.json` (`parallelizeTestCollections: false`).
- `tests\OpenClaw.GuiE2ETests\Harness\GuiE2EGate.cs` (`GuiE2EFactAttribute`, gate `OPENCLAW_RUN_GUI_E2E`).
- `tests\OpenClaw.GuiE2ETests\Harness\GuiApp.cs` (port of
  `AccessibilityAppFixture` launch/navigate logic plus `FlaUI.Core.Application.Attach`).
- `tests\OpenClaw.GuiE2ETests\Harness\Wait.cs`, `Harness\ArtifactSink.cs` (minimal).
- `tests\OpenClaw.GuiE2ETests\Pages\AutomationIds.cs` (page inventory from
  `AccessibilityScanTests.PageTestData()` plus the ConnectionPage ids listed in the notes).
- `tests\OpenClaw.GuiE2ETests\Scenarios\SmokeScenarios.cs` with one test.
- `openclaw-windows-node.slnx` (add the project).
- `.github\workflows\gui-e2e-spike.yml` (temporary, `workflow_dispatch` only;
  delete it in Phase 1 when the real `gui-e2e-smoke` job lands in `ci.yml`).

Do **not** modify anything under `src\`, `.github\proof-pools.json`,
`scripts\`, or existing test projects, except adding
`[assembly: InternalsVisibleTo("OpenClaw.GuiE2ETests")]` to
`tests\OpenClaw.E2ETests\AssemblyInfo.cs` if you reuse its internals.

## The one scenario

`SmokeScenarios.HubLaunches_ConnectionPageIsReachable_AndScreenshotIsNotBlank`
tagged `[GuiE2EFact]`, `[Trait("Tier","Fake")]`, `[Trait("Workflow","Smoke")]`:

1. Start the tray with isolated dirs and the seed
   `{"SettingsSchemaVersion":1,"EnableMcpServer":true,"GlobalHotkeyEnabled":false,"AutoStart":false}`
   and env `OPENCLAW_TRAY_DATA_DIR`, `OPENCLAW_TRAY_APPDATA_DIR`,
   `OPENCLAW_TRAY_LOCALAPPDATA_DIR`, `OPENCLAW_SKIP_UPDATE_CHECK=1`,
   `OPENCLAW_LANGUAGE=en-US`, `OPENCLAW_UI_AUTOMATION=1`,
   `OPENCLAW_SUPPRESS_EXTERNAL_BROWSER=1`,
   `OPENCLAW_ACCESSIBILITY_NAVIGATION_SIGNAL=<datadir>\nav.ready`, passing the
   argument `<scheme>://hub/connection` where scheme is `OpenClawTray.AppIdentity.ProtocolScheme`.
2. Wait for the main window handle, attach FlaUI, get the Hub `Window`.
3. Navigate to `hub/settings` via a second exe process, wait for the nav signal
   line `SettingsPage`, then find `SettingsPageMarker` by AutomationId with FlaUI.
   Navigate back to `hub/connection` and find `ConnectionPageMarker` and `AddGatewayHeaderAction`.
4. Capture the Hub window to `TestResults\GuiE2E\<run>\smoke\hub-connection.png`
   and assert it is non-blank (sampled colours ≥ 3, reuse the guard from `AccessibilityAppFixture`).
5. Dump the UIA tree under the Hub window to `uia-tree.txt` (helps Phase 1 write page objects).
6. Kill the process tree and delete temp dirs in `Dispose`.

## Acceptance criteria

- `dotnet build tests\OpenClaw.GuiE2ETests -c Debug -r win-x64` succeeds with zero warnings.
- Without the gate: `dotnet test tests\OpenClaw.GuiE2ETests -c Debug -r win-x64 --no-build`
  reports the scenario as skipped with the reason text, not failed.
- With `OPENCLAW_RUN_GUI_E2E=1`: the scenario passes locally three times in a row.
- The spike workflow passes on `windows-latest` and uploads the PNG, the UIA tree
  dump and the TRX as an artifact. Use the same restore/build steps as the
  `e2etests` job in `.github\workflows\ci.yml` (restore, build Shared,
  SetupEngine, Tray.WinUI with `-r win-x64`, then the test project).
- Required repo validation still passes (`.\build.ps1`, the two `dotnet test`
  commands in `AGENTS.md`).

## Report back

State: exact commit SHA pushed, the workflow run URL, whether AutomationIds
were visible under the WinUI `DesktopChildSiteBridge` node in the UIA dump,
attach/navigation timings, and anything in `IMPLEMENTATION_NOTES.md` that
turned out to be wrong. List open questions for Phase 1. Do not proceed to Phase 1.
