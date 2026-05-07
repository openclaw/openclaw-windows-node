# Bostick — MSIX Validation Script Record

**Author:** Bostick (Tester/FIDO)  
**Date:** 2026-05-07  
**Status:** Draft — ready for commit-7 execution  
**Script path:** `scripts/validate-msix-storage-paths.ps1`  
**Line count:** 1085  
**Parse status:** 0 syntax errors

---

## Purpose

This script empirically determines whether OpenClawTray's MSIX (`runFullTrust`) writes to real
`%APPDATA%\OpenClawTray\` / `%LOCALAPPDATA%\OpenClawTray\` (Path A) or MSIX-virtualized storage
under `%LOCALAPPDATA%\Packages\<PackageFamilyName>\` (Path B).

The answer determines uninstall surface requirements and whether an in-app pre-removal warning
banner is mandatory.

---

## Verdict-to-Action Mapping

### PathA-OrphanRisk (red)

`Remove-AppxPackage` **does NOT** clean up real APPDATA files.

**Mandatory actions for commit 5:**
- Keep the "Remove Local Gateway" in-tray button as the canonical pre-removal cleanup path.
- **MUST** add in-app warning banner gated on:
  `PackageHelper.IsPackaged() && File.Exists(setupStatePath)`
  so users see it before removing the MSIX.
- Recovery path: `scripts/validate-wsl-gateway-uninstall.ps1 -Scenario Full -ConfirmDestructiveClean`
  remains relevant for orphaned state.
- Inno uninstaller (`Uninstall-LocalGateway.ps1`) targets real paths unconditionally — no
  change needed.

**Artifact Catalog note:** Mark MSIX column as `⚠️ IT-before-removal` for file-based artifacts.

---

### PathB-CleanRemove (green)

`Remove-AppxPackage` cleans file-based artifacts automatically.

**Actions for commit 5:**
- MSIX uninstall section is limited to **WSL distro cleanup only** (steps 2-5 of canonical
  sequence).
- In-app warning banner is optional/informational (WSL distro orphan risk still present since
  distro registration is NOT removed by `Remove-AppxPackage` in either path).
- Update Artifact Catalog MSIX column to `✅` for file-based artifacts.
- Document in PR body that Path B was confirmed by this validation.

---

### Inconclusive (yellow)

**BLOCK commit 5 MSIX claims.**

- Do not ship "MSIX removal is sufficient" language.
- Either re-run the script on a clean VM (interactive session, no dev tool interference) or
  defer MSIX validation to a tracked TODO item in the PR.
- If deferred: downgrade MSIX section to "manual/unvalidated" in the merged PR.

---

## Open Questions for Aaron (pre-commit-5)

### Q1 — Auto-probe feasibility gating

The script's default `-AutoSetup` path launches the installed tray via
`explorer.exe shell:AppsFolder\<PFN>!App` and waits 30 s for a process.  On a normal developer
machine this works.  However:

- On a CI runner (non-interactive, Session 0) MSIX install itself is blocked.  This script is
  a **manual validation tool**, not a CI test.
- If the dev machine has strict policy that blocks `Add-AppxPackage` for sideloaded unsigned
  packages, the `-CertPath` param handles the cert import.  Confirm the sideload cert chain
  from CI is available as a separate `.cer` artifact.

**Decision needed:** If auto-probe is infeasible (e.g., tray doesn't reach initialization in
30 s on the validation VM), do you want to:
  - (A) Extend the probe timeout via a param (easy — add `-ProbeTimeoutSeconds`), or
  - (B) Accept manual UI walk-through as the fallback and treat exit-3 as "pending"?

Default assumption: exit-3 is "pending investigation," not a FAIL.  Aaron should confirm.

### Q2 — PackageHelper.IsPackaged() availability at commit 5

The warning banner logic (`PackageHelper.IsPackaged() && setup-state.json exists`) assumes
`PackageHelper` is available in the Settings page code-behind when commit 5 lands.  Confirm
this API is reachable from the Settings page before writing the banner code.

### Q3 — Unsigned MSIX on test machine

The CI `build-msix` job only signs the MSIX on tag-triggered builds
(`if: startsWith(github.ref, 'refs/tags/v')`).  For branch builds the MSIX is unsigned.  The
script accepts `-CertPath` for the test certificate, but an unsigned MSIX with no corresponding
cert requires enabling Developer Mode or using
`Add-AppxPackage -AllowDevelopment`.  Should the script add `-AllowDevelopment` as a fallback?
(Currently not included to avoid masking real install failures.)

---

## Evidence Layout (for reviewer)

```
<EvidenceDir>/
  pre-appdata.txt
  pre-localappdata.txt
  pre-packages.txt
  pre-appx.json
  install.stdout.txt
  package-info.json
  post-appdata.txt
  post-localappdata.txt
  post-packages.txt
  post-appx.json
  post-uninstall-appdata.txt
  post-uninstall-localappdata.txt
  post-uninstall-packages.txt
  verdict.json                   ← primary deliverable
  summary.json                   ← step log
  [MANUAL-STEP-REQUIRED.txt]     ← only if -SkipAutoSetup
```

---

## Pass/Fail Criteria Summary

| Exit code | Meaning |
|---|---|
| 0 | PASS — non-Inconclusive verdict, all evidence files present |
| 1 | FAIL — Inconclusive verdict OR missing evidence OR unhandled error |
| 2 | PREFLIGHT_BLOCK — process running, missing MSIX path, no interactive session |
| 3 | MANUAL_REQUIRED — `-SkipAutoSetup` used; operator must walk Setup-Locally UI |

---

## Script Safety Notes

- ⚠️ Do NOT run against a live paired tray instance.  Evidence captures `settings.json`.
- ⚠️ Probe window is 30 s.  If the tray has a long first-run initialization (gateway install
  runs on first launch of a setup-complete instance), the probe may only capture startup
  artifacts, not full gateway state.  Sufficient for the storage-path question; not sufficient
  for full uninstall postcondition checking (use `validate-wsl-gateway-uninstall.ps1` for that).
- ⚠️ Script uses PID-based `Stop-Process -Id` only.  Never `Stop-Process -Name`.
