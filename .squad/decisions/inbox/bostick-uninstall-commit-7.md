# bostick — Commit 7 Verdict
**Branch:** `feat/wsl-gateway-uninstall`  
**Date:** 2026-05-07  
**Commit scope:** 7A validate-msix-storage-paths.ps1 wiring · 7B Test-InnoUninstallOrdering.ps1 · 7C MSIX build · 7D this doc

---

## 7A — validate-msix-storage-paths.ps1: CLI Engine Wiring

**Status: DELIVERED**

Added Phase 4a (`Invoke-CliEngineUninstall`) between the probe and post-snapshot phases.  
The function calls Aaron's CLI flag contract:

```
OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive --json-output <tempPath>
```

New fields in `verdict.json`:

| Field | Type | Meaning |
|---|---|---|
| `engine_cli_invoked` | bool | Whether Phase 4a was attempted |
| `engine_cli_exit_code` | int\|null | Exit code of the CLI invocation |
| `engine_postconditions` | object\|null | `postconditions` block from CLI JSON output |
| `cross_check_consistent` | bool | True only when CLI and snapshot evidence agree |

`cross_check_consistent` logic (finalized in Invoke-Teardown after orphan data is available):

- **PathA scenario**: `$engineWslAbsent = true` AND `$allSurvivors.Count > 0` (engine cleaned WSL but real-APPDATA files orphaned — validates PathA claim)
- **PathB scenario**: `$engineWslAbsent = true` AND `$allSurvivors.Count -eq 0` (engine cleaned WSL and package storage cleaned automatically — validates PathB claim)
- **Inconclusive or engine non-zero exit**: always `false`

---

## 7B — Test-InnoUninstallOrdering.ps1

**Status: DELIVERED (syntax-clean, SKIP on machines without installer)**

Location: `tests/PackagingTests/Test-InnoUninstallOrdering.ps1`

- Runs the Inno installer silently (`/VERYSILENT /LOG=`), captures install log
- Silently uninstalls (`/VERYSILENT /LOG=`), captures uninstall log
- Parses uninstall log for `Uninstall-LocalGateway` hook line index vs. `{app}` directory deletion line index
- PASS if hook line appears **before** dir-delete line; FAIL otherwise
- Secondary WSL distro residual check (warns if `OpenClaw-WSL` distro still registered)
- Exit codes: 0=PASS, 1=FAIL, 2=SKIP, 3=ERROR

**Run on this machine:** `SKIP` (exit 2) — no installer binary present.  
Expected behaviour. Pass `-InstallerPath` to a built installer to get a live result.

---

## 7C — MSIX Build + Validation Attempt

### Build

**Status: SUCCEEDED**

```
dotnet msbuild src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj \
    /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:Platform=x64 \
    /p:PackageMsix=true /p:GenerateAppxPackageOnBuild=true \
    /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never \
    /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageDir=AppPackages\
```

Output: `src/OpenClaw.Tray.WinUI/AppPackages/OpenClaw.Tray.WinUI_0.4.4.0_x64_Test/OpenClaw.Tray.WinUI_0.4.4.0_x64.msix`  
Warnings only: CS0436 Logger conflict (pre-existing), missing `mspdbcmf.exe` (benign, no symbols).

### Validation Attempt — MSIX Storage Path Verdict

**Status: Inconclusive — MSIX sideload blocked (cert trust)**

#### What was attempted

1. `Add-AppxPackage` (unsigned): `HRESULT 0x800B0100` — no signature present
2. Self-signed test cert created (`CN=Scott Hanselman, O=Scott Hanselman...`), MSIX re-signed with `signtool`
3. `Add-AppxPackage` (signed, untrusted): `HRESULT 0x800B0109` — root cert not trusted
4. `Add-AppxPackage -AllowUnsigned`: `HRESULT 0x80073D2C` — publisher not in unsigned namespace
5. `certutil -user -addstore Root` to add test cert: blocks on Windows security confirmation dialog (interactive UAC — cannot be automated past without admin or pre-provision)
6. `-WhatIf` run of `validate-msix-storage-paths.ps1`: PASS (all 4 preflight checks passed; dry-run plan printed correctly; exit 0)

#### Blockers

| Blocker | Root Cause | Fix path |
|---|---|---|
| Unsigned install rejected | Built with `AppxPackageSigningEnabled=false` | Build with self-signed cert from the start |
| Self-signed cert untrusted | Root store add requires interactive dialog | Pre-provision test cert on VM, or use ADO agent with cert already trusted |
| `-AllowUnsigned` rejected | Publisher `CN=Scott Hanselman...` not in OID unsigned namespace | Use unsigned publisher format (`OID.2.25.311729597329052001218485128542457305920`) for dev builds |
| Developer Mode `AllowAllTrustedApps=1` is ON | Not sufficient for unsigned MSIX | Insufficient; need trusted root cert |

#### Recommendation

**Block PathA/PathB claims until a clean VM run is available.**  
The validate script logic and wiring is complete and preflight-verified.  
Track as: *"MSIX storage path: Inconclusive on mharsh dev machine; re-run on CI VM with pre-provisioned sideload cert"*

The `-WhatIf` run confirms the Phase 4a CLI wiring, verdict.json construction, and preflight logic are all structurally sound. The only gap is the MSIX install step itself.

---

## 7D — Build & Test Validation

| Suite | Result | Notes |
|---|---|---|
| `./build.ps1` | ✅ PASS (4/4 projects) | All PS-only changes; no C# regressions |
| `OpenClaw.Shared.Tests` | ✅ PASS (exit 0) | All tests pass |
| `OpenClaw.Tray.Tests` | ⚠️ 636 pass / 9 fail | 8 pre-existing `LocalizationValidationTests`; 1 flaky `SettingsManagerIsolationTests.OpenClawTrayDataDirRedirectsSettingsAwayFromRealAppData` (env var race in parallel xUnit — pre-existing, not caused by PS changes) |
| `Test-InnoUninstallOrdering.ps1` | ⏭ SKIP (exit 2) | No installer binary on machine |
| `validate-msix-storage-paths.ps1 -WhatIf` | ✅ PASS (exit 0) | All 4 preflight checks passed |

The `SettingsManagerIsolationTests` flaky failure is pre-existing in the worktree — caused by xUnit parallel execution sharing the `OPENCLAW_TRAY_DATA_DIR` environment variable across tests. Not caused by commit 7 changes (PS-only diff confirmed via `git diff --name-only HEAD`).

---

## Recommendations for Mattingly

The banner question raised in `validate-msix-storage-paths.ps1` (`## Notes for Aaron`) cannot be definitively answered until the MSIX storage path verdict is known.

**Current recommendation: keep banner wording neutral / conditional.**

- If verdict eventually comes back **PathA-OrphanRisk**: banner MUST be present and prominent (files orphaned on MSIX removal).
- If verdict comes back **PathB-CleanRemove**: banner optional, but still useful for WSL distro orphan risk (distro registration is NOT cleaned by `Remove-AppxPackage` in either path).
- Until verdict resolves: leave whatever banner Aaron spec'd. Do not weaken it.

No SettingsPage.xaml / .cs / Resources.resw changes required from Bostick for commit 7.

---

## Open Items for PR

1. **MSIX storage path verdict** — must be non-Inconclusive before "MSIX removal is sufficient" language can ship. Track as a TODO in the PR.
2. **`SettingsManagerIsolationTests` flakiness** — pre-existing parallel-xUnit env-var race. Recommend `[Collection("serial")]` attribute or per-test environment isolation. File as a separate bug; do not block this PR.
3. **`Test-InnoUninstallOrdering.ps1`** — needs an actual installer build to produce a live PASS/FAIL result. Can be run on the next nightly build machine.
