# Accessibility validation

OpenClaw Companion treats accessibility as a CI quality gate for the WinUI app. The Build and Test workflow runs real-process Axe.Windows scans against the tray UI test project after the normal native UI tests.

## What CI enforces

The `.github\workflows\ci.yml` **Run Accessibility Tests (Axe.Windows)** step runs:

```powershell
dotnet test tests\OpenClaw.Tray.UITests --no-build -c Debug -r win-x64 --filter Category=Accessibility
```

Those tests launch the app in a real Windows desktop process and scan pages for WCAG violations through Axe.Windows. Failures are summarized in the GitHub Actions step summary and written to `Accessibility.trx`.

The preceding framework UI lane preserves failed test exit codes and writes
`native-failure.json` with allowlisted Application Error module/exception metadata
and installed Windows App Runtime versions. It does not collect memory dumps,
event messages, command lines, paths or environment values. Missing event access
is reported as unavailable, not successful native diagnosis. Testhost phase logs
distinguish COM-wrapper initialization from Windows App SDK bootstrap.
Watchdog-terminated hangs may have no Application Error event; use the phase logs
and interrupted-test sequence in that case. An instrumented passing run does not
erase an earlier crash or establish its cause.
The test application writes unhandled exception types, HRESULTs and managed method
names directly to the standard error stream. This bypasses per-test console
buffering that can lose the last diagnostic on native fail-fast. Exception messages,
data, file paths and locals are not included, and exceptions remain unhandled.

Failure diagnostics retain rule IDs, bounded rule descriptions and canonical
control types with scan-result ordinals. They omit UIA names, automation IDs
(which can be data-derived), arbitrary properties and desktop focus state.
Only the first 20 violations are printed; the total count remains visible.
Missing or inconsistent scan metadata fails validation, including incomplete
results for excluded rules. Test-authored context labels are bounded to 128
characters; use static page/scenario labels, never user content.

The data-only `FullyQualifiedName~AxeHelperTests` filter exercises failure
reporting and the one-retry workflow without launching Companion or a native UI
fixture. Scripted scan results and a small UI Automation adapter cover refusal
for missing automation IDs, foreign processes, missing/offscreen/empty elements,
and incomplete results, including the second scan. These tests do not replace
real accessibility scans or prove native UI Automation behavior.

## Running locally

From the repository root:

```powershell
dotnet build tests\OpenClaw.Tray.UITests -c Debug -r win-x64
dotnet test tests\OpenClaw.Tray.UITests -c Debug -r win-x64 --no-build --filter Category=Accessibility
```

Run this lane for UI changes that add or modify pages, dialogs, controls, focus behavior, labels, contrast-sensitive visuals, or keyboard navigation.

For owned private launches on a shared host, use
[`scripts\Invoke-PrivateDesktop.ps1`](../scripts/Invoke-PrivateDesktop.ps1).
It creates a new desktop with Default's DACL and mandatory label without
modifying Default or switching the shared input desktop. NULL-security desktop
creation can otherwise deny low-integrity sandboxed children the access needed
to initialize USER32. The
[real MXC desktop regression](../tests/OpenClaw.DesktopHarness/README.md)
checks USER32/PowerShell execution through the current published SDK owner on
both caller-desktop variants. The original 0.8 bridge failure proof is historical;
SDK 1.0.0 can inherit the caller's task desktop. Inspect the actual assigned
desktop rather than treating the requested UI policy as proof of a separate
execution desktop. See the regression guide for current policy assertions and
runtime qualification requirements.

### Isolated desktop startup

Use a private desktop on a shared development machine. Keep its desktop handle
alive until the test process exits. Run the startup regressions with the scans:

```powershell
dotnet test tests\OpenClaw.Tray.UITests -c Debug -r win-x64 --no-build --filter "Category=Accessibility|Category=AccessibilityStartup"
```

`AccessibilityFixtureStartupTests` drives the real fixture constructor and
acknowledged Hub navigation. Its failure-path case also checks that a discovery
failure reaps the owned app and removes its temporary profile. The normal native
UI CI lane includes these startup cases; the Axe step still selects
`Category=Accessibility`.

Private desktops may not implement shell switcher registration. The hidden
runtime anchor never activates and needs no such registration. If the optional
tray icon reports `NotImplementedException`, Companion logs the unavailable tray
and continues opening the requested Hub. Other startup failures still propagate.
Do not increase the fixture's 60-second Hub deadline to hide a failed app launch.

The explicit no-Gateway accessibility fixture supplies sessions and an agent
roster before Workspace creation. Only the enabled isolated fixture treats
that initial snapshot as current. Normal launches still wait for a fresh
Gateway roster. Synthetic selection tests do not prove live connectivity or
pairing.

## Relationship to other validation

### Settings navigation keyboard contract

Back to app and an unhandled Escape leave full Settings for the retained app
destination. Window-owned `SettingsEscapePolicy` preserves native editors,
flyouts, menus and dialogs as the first Escape owners; it does not collapse the
Settings rail. The pane toggle retains the user's saved preference.
`WorkspaceWindowProofTests.SettingsBackToApp_ReturnsToAppDestinationInSameWindow`
covers mouse, Enter and Escape activation, retained chat and notification focus.
`WorkspaceNavigationTests.SettingsEscape_LeavesFullSettingsOnlyWhenNothingElseOwnsEscape`
covers the policy exclusions.

Workspace still has an 800-DIP minimum width. The below-720-DIP rail is not a
supported window-size claim. Width-driven shell embedding and temporary narrow
rail preferences are superseded by the canonical shared foundation. Page-owned
responsive layouts still use their allocated content width.
`SettingsPageInsetTests.Shell_DeliversFullAndEmbeddedInsetsWithoutMovingBackdropOrReplacingContent`
covers the optional content inset and preserves ordinary pages' existing padding.

The integrated Config, Local AI, Default Model, Voice, Notifications, Event
Stream and Diagnostics source adapts to allocated width after rail/sidebar
subtraction. Config measures its editor grid and keeps draft controls mounted
while stacking tree/detail panes. Source guards and XAML parsing do not prove
native layout, focus retention, text scaling or six-locale reachability; these
still require current-build native proof before any window-minimum reduction.

Accessibility scans do not replace the required agent closeout validation in `AGENTS.md`. They are an additional focused lane for WinUI accessibility work and a formal CI gate documented in `docs\TEST_COVERAGE.md`.

## Account menu metadata

Account-menu hosts, versions and connection labels use `AccountMenuItem.Metadata`,
not `KeyboardAcceleratorTextOverride`, which WinUI exposes as UI Automation's
`AcceleratorKey`. Real shortcuts retain the native shortcut field. Saved Gateway
rows expose the active choice through localized `ItemStatus` and HelpText without
adding a radio/check column.
