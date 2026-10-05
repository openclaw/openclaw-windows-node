# Testing the Inno -> Store migration flow

How to exercise the Inno to Store migration flow end to end on a developer
machine, using genuine signed binaries.

This is the Inno-side counterpart to
[`scripts/Export-MigrationTestMsix.ps1`](../scripts/Export-MigrationTestMsix.ps1),
which solves the same problem from the Store side. Both exist because neither
shipping artifact can be used for migration testing as published.

## Why a workaround is needed right now

The migration payload shipped in #1519, which is on `main`. Every release that
contains it is a `2026.9.5-alpha.*` prerelease. The Store app refuses to migrate
from a prerelease-labelled Inno registration, so installing the alpha and
launching the Store app produces the startup block, not the consent screen.

Until a stable `2026.9.5` ships (or the prerelease-source fix lands), the only
faithful test is to relabel the registration. The binaries stay genuine and
signed; only two registry strings change.

## Prerequisites

- The Store build installed (Microsoft Store, or a sideloaded MSIX).
- `OpenClawCompanion-Setup-x64.exe` from any `v2026.9.5-alpha.*` release, or a
  local build from [`scripts/build-inno-local.ps1`](../scripts/build-inno-local.ps1).
- A clone of this repository, for
  [`scripts/Enable-StoreMigrationTest.ps1`](../scripts/Enable-StoreMigrationTest.ps1).
- A machine you can re-image, or accept that the Inno app gets uninstalled.
  Settings, gateway, and device identities are preserved; this was verified.

Run everything as the user who installed the Inno app. The registration, the
device identities, and the captured state are all per-user.

You do not need to onboard the Inno app. Migration captures state from
`%APPDATA%\OpenClawTray`, which persists across installs. If you already have a
gateway and device identities, that is a richer test than a fresh onboard.

## Steps

### 1. Close the Store app, then install the alpha Inno build

Inno's "close all instances" check matches on process name, so the **Store**
app's tray counts as an instance of the app being installed. With `/VERYSILENT`
that becomes a silent abort with exit code 1 and no installation.

```powershell
Get-Process | Where-Object ProcessName -like '*OpenClaw*' |
  ForEach-Object { Stop-Process -Id $_.Id -Force }

Start-Process -Wait -FilePath .\OpenClawCompanion-Setup-x64.exe `
  -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',"/LOG=$env:TEMP\oc-install.log"
```

Approve the one UAC prompt for the bundled Visual C++ redistributable. The
install cannot be fully unattended because of it.

### 2. Run the relabel script

```powershell
.\scripts\Enable-StoreMigrationTest.ps1
```

It stops the tray app, verifies the installation can actually migrate, then
relabels the registration as stable `2026.9.5`. If it refuses because the
gateways folder contains stray files, re-run with `-MoveBlockingFiles`.

Use `-Revert` to undo the relabel if you abandon the test before clicking
Migrate.

#### What the relabel does, and why it is faithful

Only `DisplayVersion` and `DisplayName` change, and they must change together:
the detector validates `DisplayName` against `DisplayVersion`, so editing one
alone fails with "does not identify the production publisher and application."
`Publisher`, `InstallLocation`, and `UninstallString` keep the values the real
installer wrote, and no binary is modified.

`GitVersion.yml` uses `assembly-versioning-scheme: MajorMinorPatch`, so an alpha
binary is already stamped `FileVersion 2026.9.5.0`. A genuine stable `2026.9.5`
would be identical in every field the detector inspects.

### 3. Launch the Store app and walk the flow

Expected sequence, confirmed in `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`:

| Screen | Log line |
| --- | --- |
| "Move to the Store version" consent | `startup admission: ConsentRequired (receipt: False)` |
| After Migrate | `Store migration preparation completed` |
| | `Store migration completion recorded` |
| "Remove the previous app" | `startup admission: FinalizationRequired (receipt: True)` |
| After manual uninstall + Retry | `Store migration finalized` |

Uninstall the entry named **OpenClaw Companion version 2026.9.5** when prompted,
then press Retry.

**Point of no return: clicking Migrate.** Before it, `-Revert` backs you out
cleanly. After it, a receipt is written and the Inno app can no longer start
normally, so you must finish the flow.

## Verifying preservation

Before uninstalling, confirm the uninstaller will take the preservation path:

```powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$env:LOCALAPPDATA\OpenClawTray\Test-InnoMigration.ps1" `
  -AppRoot "$env:LOCALAPPDATA\OpenClawTray" -Architecture x64
$LASTEXITCODE
```

| Exit | Meaning |
| --- | --- |
| 10 | Completed migration validated. Preserve generated state and gateway. |
| 11 | Store app registered but receipt missing. Preserve anyway. |

After uninstalling, confirm nothing was lost:

```powershell
wsl.exe --list --quiet
Get-ChildItem "$env:APPDATA\OpenClawTray\gateways" -Directory | Select-Object Name
Get-Item "$env:APPDATA\OpenClawTray\gateways.json" | Select-Object LastWriteTime
```

WSL distros, gateway directories, and `gateways.json` must be unchanged.

## Known rough edges

- **"Migration source has an unexpected file type."** Capture requires every
  entry under `%APPDATA%\OpenClawTray\gateways` to be a directory, but native
  gateway setup writes `native-setup-draft.json` into that same folder. Any
  user who ran native setup hits this, and the message names neither the folder
  nor the file. The script checks for it; `-MoveBlockingFiles` clears it.
- **Installing the exe aborts with exit code 1.** The Store app's tray was
  running. Close all OpenClaw processes first.
- **"Could not exclude source processes across sessions."** Means the previous
  app is running. Close it and press Retry. The message does not say this.
- **Silent uninstall can no-op invisibly.** `/VERYSILENT` prints nothing and
  leaves every file if a tray process is alive. Always pass `/LOG=` and verify
  the registry key and install directory are actually gone.
- **The About page still shows the alpha version.** That string comes from the
  compiled `AssemblyInformationalVersionAttribute`, which the migration detector
  never reads. Cosmetic only.

## Cleanup

Uninstalling the Inno app removes the registry key, so the relabel cleans itself
up. No manual revert is needed after a completed migration.

If you abandoned the test before clicking Migrate, run
`.\scripts\Enable-StoreMigrationTest.ps1 -Revert` to restore the real alpha
label.

If `-MoveBlockingFiles` moved anything, it is in
`%APPDATA%\OpenClawTray\gateways-blocking-<timestamp>\`. Nothing was deleted.

## Repeating the test

The flow is repeatable with no manual state reset. `StoreMigrationStartupCoordinator`
has no "already migrated" latch; admission is a pure function of detector output
and record status. Finalization consumes the receipt, so a freshly installed and
relabelled Inno registration returns `ConsentRequired` again.

To run it a second time, reinstall the alpha exe and re-run the script. The
leftover `store-migration\prepare.lock` is a cross-session lock file, not state,
and does not need clearing.

That holds for a run you finished. A run interrupted between consent and
finalization leaves a `consent.dpapi` or `intent.dpapi` behind, and the next
attempt resumes from it instead of starting at the consent screen. Clear it
with:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState
```

That closes the tray, since both lock files are held open while it runs, then
deletes the records in `%APPDATA%\OpenClawTray\store-migration\`. It leaves the
folder itself and anything it does not recognise alone, and it works whether or
not an Inno installation is still present.

It also leaves `completed.dpapi` alone. That file is the receipt a finished
migration writes, and `Test-InnoMigration.ps1` reads it to decide whether a WSL
gateway belongs to a migrated Store install. With a valid receipt the gateway
uninstaller preserves the distro. With the receipt gone and the Store app
unregistered, it is free to run `wsl --unregister` and take the generated state
with it. If you genuinely need a cold start, ask for it:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -IncludeCompletionReceipt
```

Do not leave that machine in a half-uninstalled state afterwards.

The reset refuses to run if the `store-migration` folder, or anything above it,
is a junction or other reparse point. Following a link there would delete the
receipt out of whatever the link targets, which is the one deletion with real
consequences. `scripts\test-store-migration-test-script.ps1` holds that guard
in place:

```powershell
.\scripts\test-store-migration-test-script.ps1
```

Nothing else clears these records. They sit in real roaming AppData rather than
the package-virtualized location, so `Remove-AppxPackage` walks past them, and
`TrayArtifactCleanup` never names the folder, so `--uninstall` leaves it too.

