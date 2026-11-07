# Accessibility validation

OpenClaw Companion treats accessibility as a CI quality gate for the WinUI app. The Build and Test workflow runs real-process Axe.Windows scans against the tray UI test project after the normal native UI tests.

## What CI enforces

The `.github\workflows\ci.yml` **Run Accessibility Tests (Axe.Windows)** step runs:

```powershell
dotnet test tests\OpenClaw.Tray.UITests --no-build -c Debug -r win-x64 --filter Category=Accessibility
```

Those tests launch the app in a real Windows desktop process and scan pages for WCAG violations through Axe.Windows. Failures are summarized in the GitHub Actions step summary and written to `Accessibility.trx`.

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

The Settings pane toggle exposes **Expand navigation** or **Collapse navigation**
according to its state. Escape closes an expanded pane only while focus is in
its navigation controls and returns focus to the toggle. Page controls keep
their own Escape behavior; native search flyouts, menus and ContentDialogs
handle dismissal before the shell. Collapsing the pane does not change the page.
`WorkspaceWindowProofTests.SettingsNavigation_EscapeReturnsFocus_WithoutClosingFromPageOrSearch`
checks the mounted Settings boundary in Light and Dark themes.

Workspace still has an 800-DIP minimum width. The mock's below-720-DIP rail is
not a supported window-size claim: pinned pages reserve a 256-DIP Chat sidebar,
and existing page controls can require more space. Supporting smaller windows
requires a cross-route layout change, not only reducing the minimum or changing
the Settings rail. `WorkspaceSettingsLayout` prepares that shared seam using
the host window width, not an embedded page's smaller content width. Below 720
it projects a compact Settings rail and presents pins through the same retained
frame, without the Chat sidebar. Narrow expansion is temporary; returning to
wide restores the saved pane preference and requested embedding. This branch
is not reachable through normal window resizing while the minimum remains 800.

The integrated adaptive Config, Local AI, Default Model, Voice, Notifications,
Event Stream and Diagnostics source uses each page's allocated width after
rail/sidebar subtraction. Config additionally measures its editor grid and
keeps draft controls mounted while stacking tree/detail panes. Source guards
and XAML parsing do not establish native layout, focus retention, text scaling
or six-locale action reachability; those assertions still require an isolated
current-build proof slot before any window-minimum reduction.

Accessibility scans do not replace the required agent closeout validation in `AGENTS.md`. They are an additional focused lane for WinUI accessibility work and a formal CI gate documented in `docs\TEST_COVERAGE.md`.
