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

## Relationship to other validation

Accessibility scans do not replace the required agent closeout validation in `AGENTS.md`. They are an additional focused lane for WinUI accessibility work and a formal CI gate documented in `docs\TEST_COVERAGE.md`.
