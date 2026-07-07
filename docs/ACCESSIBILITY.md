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

## Relationship to other validation

Accessibility scans do not replace the required agent closeout validation in `AGENTS.md`. They are an additional focused lane for WinUI accessibility work and a formal CI gate documented in `docs\TEST_COVERAGE.md`.
