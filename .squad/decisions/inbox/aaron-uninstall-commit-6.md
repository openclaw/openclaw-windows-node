# Aaron — Uninstall Commit 6 Record

**Date:** 2026-05-07  
**Author:** Aaron (Backend / Infrastructure Engineer)  
**Commit:** `1cfc1ee`  
**File:** `scripts/validate-wsl-gateway-uninstall.ps1` (1000 lines)

---

## What Was Delivered

`validate-wsl-gateway-uninstall.ps1` — empirical postcondition verifier for
`LocalGatewayUninstall`. Three modes, exit-coded, token-redacted, dot-sources
`_uninstall-helpers.ps1`, mirrors `validate-wsl-gateway.ps1` style.

**Structure:**
- Dot-sources `_uninstall-helpers.ps1` — no helper duplication
- `Get-StateSnapshot` — pre/post state capture to JSON
- `Get-Postconditions` — evaluates 7 postconditions from C# model
- `Get-Verdict` — PASS / PARTIAL / FAIL based on 5 required postconditions
- `Invoke-UninstallSteps` — 13-step PS mirror of `LocalGatewayUninstall.cs`
- `Write-SummaryMarkdown` / `Write-ColorVerdict` — output helpers

---

## Verdict Modes in Practice (This Machine)

Machine has `OpenClawGateway` registered (live WSL install).

| Mode                     | Verdict         | Exit | Notes                                                |
|--------------------------|-----------------|------|------------------------------------------------------|
| `PreflightOnly`          | `PreflightOnly` | 0    | Distro registered; pre-state.json written            |
| `Full -DryRun`           | `DryRunComplete`| 0    | All 13 steps DryRun; no mutations                    |
| `PostconditionOnly`      | `PARTIAL`       | 1    | wsl_distro_absent=false, setup_state_absent=false    |
| `-Help`                  | n/a             | 0    | Usage printed cleanly                                |
| `Full` (no -Confirm)     | BLOCKED         | 2    | Correct safety gate                                  |
| `-DistroName Ubuntu`     | BLOCKED         | 2    | Correct distro-name guard                            |

**On a machine after a successful live uninstall,** `PostconditionOnly` should
produce `PASS` / exit 0 — all 5 required postconditions true.

**Sample verdict.json (PostconditionOnly on live machine):**
```json
{
  "mode": "PostconditionOnly",
  "dry_run": false,
  "distro_name": "OpenClawGateway",
  "preflight_passed": false,
  "destruction_executed": false,
  "postconditions": {
    "wsl_distro_absent": false,
    "autostart_cleared": true,
    "setup_state_absent": false,
    "device_token_cleared": false,
    "device_key_file_preserved": true,
    "mcp_token_preserved": true,
    "keepalives_absent": true
  },
  "verdict": "PARTIAL",
  "errors": []
}
```

---

## Integration Gaps — Commit 5 Must Address

1. **No CLI `--uninstall` flag on the tray app yet.**  
   The PS script replicates the 13 steps in PowerShell. Once commit 5 adds the
   CLI flag (`-- --uninstall --confirm-destructive --json-output <path>`), the
   `Invoke-UninstallSteps` function should be replaced with a call to that CLI
   entry-point to eliminate duplication with the C# engine.  
   **Action for Commit 5:** Add `--uninstall` CLI handler to App.xaml.cs or a
   separate CLI entry point. The PS script currently has a `# TODO` comment in
   the mode documentation pointing at this.

2. **Script does NOT remove the VHD parent directory explicitly.**  
   The C# engine relies on `wsl --unregister` to auto-remove the VHDX. The
   `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway\` parent directory may
   remain if `wsl --unregister` fails mid-flight. The PS script does not add a
   separate `Remove-Item $vhdDirPath` step, matching the C# engine exactly.  
   **Flag for Bostick:** Verify on the MSIX path whether the VHD dir is cleaned
   up automatically or whether a separate `Remove-Item` is needed.

3. **`run.marker` is not deleted by this script.**  
   The C# engine includes deletion of `run.marker` (plan step 11). The C# code
   I reviewed didn't show this as a separate step (it may be in a part of the
   engine I didn't see). The PS script follows what's visible in the C# source.
   If `run.marker` remains after uninstall, `PostconditionOnly` won't flag it
   since it's not a tracked postcondition.  
   **Flag for Commit 5:** Confirm whether `run.marker` deletion is in the engine
   or needs to be added. Add it as a postcondition if so.

---

## For Bostick (Commit 7 — Packaging Test)

To validate this script end-to-end with a real uninstall:

```powershell
# Step 1: Record pre-state
.\scripts\validate-wsl-gateway-uninstall.ps1 -Mode PreflightOnly

# Step 2: Run full uninstall via tray button (or script)
.\scripts\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive

# Step 3: Or, after using the in-tray button, verify postconditions only
.\scripts\validate-wsl-gateway-uninstall.ps1 -Mode PostconditionOnly
```

The `Full -ConfirmDestructive` mode executes the 13 PS steps directly, which is
useful for Inno Setup packaging tests where the tray isn't running. The
`PostconditionOnly` mode is useful after the in-tray button is clicked.

Output artifacts are in `.\uninstall-validation-output\<utc-timestamp>\`.
