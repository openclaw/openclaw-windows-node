# bostick History

## Summarized
Older entries archived. See history-archive.md.

---

## 2026-05-07 — MSIX Storage Path Validation Script (anticipatory, pre-commit-7)

**Task:** Draft `scripts/validate-msix-storage-paths.ps1` before Aaron's commits 5-7 land, so
the script is ready for commit-7 verification.

**Script created:** `scripts/validate-msix-storage-paths.ps1` (1085 lines)

### What the script does

Empirically determines whether the OpenClawTray MSIX (with `runFullTrust`) writes user-data
files to real `%APPDATA%\OpenClawTray\` / `%LOCALAPPDATA%\OpenClawTray\` paths (Path A —
OrphanRisk) or to MSIX package-virtualized storage under
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\` (Path B — CleanRemove).  The answer controls
which uninstall surfaces and warning banners are required in commit 5.

**Execution phases:**
1. **Preflight** — interactive session check, no OpenClaw* processes running, MSIX file exists, no
   conflicting package installed.
2. **Pre-install snapshot** — capture dir listings + `Get-AppxPackage` JSON to `pre-*.txt/json`.
3. **Install** — `Add-AppxPackage` (with optional `Import-Certificate`); resolve and record
   `PackageFamilyName`, `InstallLocation`, `PackageFullName` → `package-info.json`.
4. **Probe** (AutoSetup mode, default) — write session-ID probe markers to real APPDATA paths,
   launch tray via `explorer.exe shell:AppsFolder\<PFN>!App`, wait up to 30 s for process, kill
   by PID, clean markers.  If `-SkipAutoSetup`: emit `MANUAL-STEP-REQUIRED.txt` and exit 3.
5. **Post-install snapshot** — re-capture same paths → `post-*.txt/json`.
6. **Diff & verdict** — compute new paths in real vs. virtualized storage; write `verdict.json`
   with `msix_writes_to_real_appdata`, `msix_writes_to_real_localappdata`,
   `msix_writes_to_virtualized_storage`, `verdict`, `reasoning`, `package_family_name`.
   Color-coded console output (red=PathA, green=PathB, yellow=Inconclusive).
7. **Teardown** — `Remove-AppxPackage`; post-uninstall snapshot; compute `removal_orphans`;
   append to `verdict.json`.

### Pass/fail criteria

| Condition | Result |
|---|---|
| Non-Inconclusive verdict AND all evidence files present AND no terminating errors | Exit 0 (PASS) |
| Inconclusive verdict | Exit 1 (FAIL) |
| Missing evidence files | Exit 1 (FAIL) |
| Preflight blocked (process running, etc.) | Exit 2 (PREFLIGHT_BLOCK) |
| `-SkipAutoSetup` mode | Exit 3 (MANUAL_REQUIRED) |

### Required evidence files (all must be present for PASS)

`pre-appdata.txt`, `pre-localappdata.txt`, `pre-packages.txt`, `pre-appx.json`,
`post-appdata.txt`, `post-localappdata.txt`, `post-packages.txt`, `post-appx.json`,
`post-uninstall-appdata.txt`, `post-uninstall-localappdata.txt`, `post-uninstall-packages.txt`,
`verdict.json`, `package-info.json`, `summary.json`

### Manual steps that may be required

If `-SkipAutoSetup` is used (or if the default auto-probe path fails because `explorer.exe
shell:AppsFolder` does not launch in the test environment), the operator must:
1. Manually walk through the Setup-Locally flow in the tray UI.
2. Kill the tray by PID.
3. Re-run the script with `-SkipInstall -EvidenceDir <same-dir>` to capture post-setup state
   and proceed to verdict + teardown.

The `-AutoSetup` default path avoids this for most dev machines.  CI runners (non-interactive)
cannot use MSIX install at all — this script is intended for manual validation on a physical
machine or interactive VM.

### Verdict-to-action mapping

See `scripts/validate-msix-storage-paths.ps1` header comment (`## Notes for Aaron`) for full
details.  Summary:

- **PathA-OrphanRisk** → Keep in-tray "Remove Local Gateway" button as canonical cleanup.
  MUST add pre-uninstall warning banner gated on `PackageHelper.IsPackaged() && setup-state.json
  exists`.  Recovery script still relevant.
- **PathB-CleanRemove** → `Remove-AppxPackage` handles file cleanup.  MSIX section limited to
  WSL distro cleanup only.  Warning banner optional.
- **Inconclusive** → Block MSIX uninstall claims in commit 5.  Re-run on clean VM or defer to
  tracked TODO.

### CI artifact consumption

MSIX is produced by the `build-msix` CI job.  Download with:
```
gh run download <run_id> --name openclaw-msix-win-x64 --dir ./msix-drop/
```
Pass the `.msix` path to `-MsixPath`.

### Parse / syntax status

Verified clean (0 syntax errors via `[System.Management.Automation.Language.Parser]::ParseFile`).

### What this gates

Commit 7 verification.  The script must produce a non-Inconclusive verdict before MSIX coverage
claims in the PR are considered validated.
