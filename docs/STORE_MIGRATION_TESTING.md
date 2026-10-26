# Testing current-policy Inno to Store migration

Use this guide to test a genuine signed Inno source moving to an eligible
production-identity Store target on a **disposable Windows machine**. It covers
artifact selection, helper preflight, the visible handoff, and recovery. It is
not evidence that a particular build has completed migration.

The recommended path is to use an already-eligible source, not relabel an alpha.
[`Enable-StoreMigrationTest.ps1`](../scripts/Enable-StoreMigrationTest.ps1)
checks local prerequisites and provides guarded reset, quarantine, and label
recovery operations. It neither changes signed binaries nor relaxes product
migration policy.

## 1. Select compatible artifacts and prepare the machine

Take a VM checkpoint or use a machine you can re-image. The migration uses
per-user production registration and real application data, and its normal
handoff uninstalls the Inno source. Do not run this procedure on a shared
developer host or assume that environment overrides isolate an installer.
Use the same Windows user for source installation, helper operations and Store
launch. Have a separate backup of any test state you cannot afford to lose.

Select both artifacts before making changes:

| Artifact | Required evidence |
| --- | --- |
| Inno source | Genuine signed release-identity payload at the default per-user `%LOCALAPPDATA%\OpenClawTray` location. Its complete `DisplayVersion`, matching `DisplayName`, `FileVersion`, and `ProductVersion` must satisfy the current version policy below. The migration payload must be present. |
| Store target | Same architecture as the source, production package identity, migration enabled, and a compiled minimum source version no greater than the source. Record the target's exact version, build revision and migration policy metadata; a package version alone does not prove that migration is enabled. |

Use a Microsoft Store release meeting those conditions. For an unpublished
target, the [migration test MSIX exporter](../scripts/Export-MigrationTestMsix.ps1)
produces a test-signed production-identity package from an eligible unsigned
submission package. Follow its generated `INSTALL.txt` in the disposable VM.
This package is **not side-by-side with the Store release**. The Dev identity
does not exercise production migration, and an unsigned submission package is
not directly installable. Exporter fixture tests are not installation proof.

Current version compatibility is owned by
[`MigrationVersionPolicy`](../src/OpenClaw.Connection/Migration/MigrationVersionPolicy.cs):

| Source label | Compatible executable metadata |
| --- | --- |
| Three-part numeric, for example `2026.9.5` | Numerically equal `FileVersion`, normalizing an absent fourth part to zero. If `ProductVersion` is present, its numeric release must also match. |
| Four-part numeric, for example `2026.9.5.7` | Matching numeric file and product release versions, including the fourth component. |
| Numeric stable correction, for example `2026.9.5-2` | Base `FileVersion` `2026.9.5.0` and exact product release `2026.9.5-2`. Corrections are positive integers without leading zeros. |
| Alpha or other prerelease product metadata | Not eligible. A numeric `FileVersion` does not make an alpha binary a stable release. |

Product versions may carry valid `+build.metadata`; the release portion still
must match. Registry labels do not accept that metadata suffix. Never remove a
correction suffix or change signed binary metadata to force admission.

Record artifact provenance and hashes in your test evidence. If compatible
signed artifacts are unavailable, report that prerequisite as blocked rather
than substituting an alpha relabel.

## 2. Close the relevant apps and install the source

Use **Exit** on the relevant Inno and Store tray apps, not just the window's
close button. Wait for any migration or gateway uninstall operation to finish.
The helper does not enumerate processes, terminate them, or manage gateways.

Install the selected Inno source interactively so prompts and failures remain
visible. For an x64 source downloaded to the current directory:

```powershell
$installLog = Join-Path (Get-Location) 'migration-install.log'
Start-Process -Wait -FilePath .\OpenClawCompanion-Setup-x64.exe `
  -ArgumentList "/LOG=`"$installLog`""
```

Use the corresponding ARM64 artifact for an ARM64 test. Close the source tray
again if installation launched it. [`installer.iss`](../installer.iss) uses
`AppMutex` coordinated with the app's build-identity mutex. Do not diagnose
every installer exit code 1 as a process-name collision. Inspect the installer
log and the resulting installation instead of treating silent output as success.
Prerequisite prompts depend on the machine; unattended installation is not
assumed here.

You can test with fresh state or deliberately prepared gateway/identity data.
Onboarding is not itself a helper prerequisite. Existing state under
`%APPDATA%\OpenClawTray` affects inventory and recovery, so a reinstall is not
equivalent to a fresh VM checkpoint.

## 3. Run preflight without changing a valid source label

From the repository, first preview:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -WhatIf
```

If `prepare.lock` does not yet exist, the preview reports that it will not create
it and cannot perform the authoritative scan. After reviewing that result,
run normally:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1
```

For a compatible source, expect **No relabel necessary**, not a fabricated
stable label. The default keeps the complete installed version, including a
stable correction. The helper checks registration labels, publisher, canonical
source and uninstall paths, required ordinary payload files, exact release
identity, both executable versions, and gateway-root entry types.

This is a scoped preflight, not full admission. The Store independently checks
registration views, executable structure/architecture, minimum source version,
pending records, and complete data inventory. File presence is not signature or
uninstaller-provenance validation. A successful helper run does not establish
that captured JSON, credentials or gateway ownership are valid.

Before its authoritative scan or mutations, the helper obtains
`store-migration\prepare.lock` with `FileAccess.ReadWrite` and `FileShare.None`
and keeps it open until completion. The app's
[`MigrationOperationLock`](../src/OpenClaw.Connection/Migration/MigrationOperationLock.cs)
and [gateway uninstaller](../scripts/Uninstall-LocalGateway.ps1) use the same
operation lock. A reader in another process or Windows session blocks the
helper even if no tray is visible. Close the relevant apps yourself and wait;
do not delete the lock or stop unrelated processes. A leftover, unheld
`prepare.lock` is normal and is never reset state.

### If the gateway-root check refuses

Current [`MigrationInventory`](../src/OpenClaw.Connection/Migration/MigrationInventory.cs)
accepts ordinary `gateways\native-setup-draft.json` and exact atomic-write
siblings named `.native-setup-draft.json.<32 lowercase hex digits>.tmp`. The
helper leaves these in place. It does not validate the draft's JSON or contents
and does not imply that setup is safe to resume.

Unknown ordinary files are blockers by default. Inspect them before opting
into quarantine:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -MoveBlockingFiles -WhatIf
.\scripts\Enable-StoreMigrationTest.ps1 -MoveBlockingFiles -Confirm
```

Approved files move, without overwrite, to a unique
`%APPDATA%\OpenClawTray\gateways-blocking-<id>` directory. Allowed native setup
files do not move. Junctions/reparse points, including redirected ancestors,
are refused rather than quarantined. A directory using an allowed draft-file
name is also refused. If confirmation is declined, neither quarantine nor
relabel proceeds.

## 4. Exercise and capture the Store handoff

Launch the selected Store target. With eligible source metadata and no pending
recovery state, expect the migration consent surface. Capture its visible state
with the exact source/target versions before choosing **Migrate**.

After Migrate, preparation and completion can persist migration records. Do not
treat `-Revert` or reset as a rollback of that handoff. If completion exists,
follow the Store app's instructions to remove the previous Inno application,
then return and choose Retry. Remove the **source Inno entry**, not the Store
target. If source-process exclusion fails, verify that the relevant source app
has exited in every test session before retrying; other inspection failures
can require investigation rather than process termination.

Corroborate the visible flow with current-run diagnostic markers:

| Stage | Expected diagnostic marker |
| --- | --- |
| Eligible source, no completed handoff | `Store migration startup admission: ConsentRequired` |
| Preparation succeeds | `Store migration preparation completed` |
| Completion receipt persisted | `Store migration completion recorded` |
| Completed receipt, source still installed, on re-evaluation | `Store migration startup admission: AwaitingInnoRemoval` |
| Completed receipt, source removed, on re-evaluation | `Store migration startup admission: FinalizationRequired` |
| Successful finalization | `Store migration finalized` |

Use the current test's app diagnostics, normally
`%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`. A marker alone is not proof of
preservation or a rendered state. Record failures and sanitize evidence; do not
publish credentials, private settings or complete arbitrary logs.

Before source uninstall, the installed read-only preservation checker can
corroborate the handoff. For the x64 example:

```powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$env:LOCALAPPDATA\OpenClawTray\Test-InnoMigration.ps1" `
  -AppRoot "$env:LOCALAPPDATA\OpenClawTray" -Architecture x64
$LASTEXITCODE
```

| Exit | Meaning |
| --- | --- |
| 10 | Validated completed migration: preserve generated state and gateway. |
| 11 | Store package registered, receipt absent: preserve state; this is not proof of completed migration. |
| 2 | State cannot be established: cleanup must not start. |
| 0 | No receipt and no Store registration: cleanup may be authorized. Not the expected preservation result for this handoff. |

After uninstall and Retry, compare your prepared test inventory with the actual
remaining settings, gateway registry, device identity metadata and owned test
gateway. For example, inspect names and timestamps without dumping secrets:

```powershell
wsl.exe --list --quiet
Get-ChildItem "$env:APPDATA\OpenClawTray\gateways" -Directory | Select-Object Name
Get-Item "$env:APPDATA\OpenClawTray\gateways.json" | Select-Object LastWriteTime
```

These are supplemental observations, not a guarantee that data was preserved.
Include actual installer/uninstaller results and visible Store startup in the
proof report. No live migration or preservation proof was performed by the
helper's fixture suite.

## Recovery and repeating a test

Prefer restoring the disposable checkpoint for a new independent run.
Successful finalization clears consent, intent and completion records, with the
completion receipt removed last. Failed finalization can retain records for
retry; do not infer a clean slate from a fresh install or app uninstall.

For an intentionally abandoned, interrupted test, close the relevant apps and
preview the standalone reset. It works even after the source registration is
gone:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -WhatIf
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -Confirm
```

Reset removes `consent.lock`, `consent.dpapi`, `intent.dpapi` and record-write
`.<32 hex digits>.tmp` files. It retains `prepare.lock`, unknown entries and
`completed.dpapi`. Missing-state and receipt-only default resets do nothing.
Reparse points at the state directory, its ancestors, lock or entries are
refused. The authoritative record scan and deletions happen under the exclusive
operation lock. If a deletion fails, the error stops further deletion; prior
confirmed deletions are not rolled back. Resolve the reported file or operation
conflict before retrying.

> [!WARNING]
> The next command deletes the completion receipt. Without that receipt **and**
> without Store registration, a later gateway uninstall can unregister the WSL
> distro and delete generated state. Use only for an explicitly disposable cold
> start, never as ordinary recovery of a completed handoff.

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -IncludeCompletionReceipt -WhatIf
# Only after reviewing the disposable cold-start plan:
.\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -IncludeCompletionReceipt -Confirm
```

The named opt-in uses normal `ShouldProcess`/`WhatIf`/`Confirm`; it is rejected
without `-ResetMigrationState`. Reset cannot be combined with relabel, revert,
or quarantine options. Removing a Store package is not a reset of the real
roaming `store-migration` directory.

### Equivalent-label testing and saved-label recovery

Relabeling is optional and usually unnecessary. `-TargetVersion` refers to the
source's registry label, not a Store version. It permits only source/target
labels compatible with the unchanged executable, for example equivalent
three/four-part labels for a stable numeric binary:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -TargetVersion '2026.9.5.0' -WhatIf
```

When applied, the helper saves original `DisplayVersion` and `DisplayName` under
the same uninstall key before updating either label. It will not overwrite
existing backups. To undo a label-only experiment before completed migration:

```powershell
.\scripts\Enable-StoreMigrationTest.ps1 -Revert -WhatIf
.\scripts\Enable-StoreMigrationTest.ps1 -Revert -Confirm
```

Revert takes exclusive ownership and refuses if a completion receipt exists,
saved labels are incomplete, or current labels/binary metadata drifted. It can
restore a historical alpha label only when the saved label matches the
unchanged binary's product release and the current label is the original
numeric core. That is cleanup, not alpha migration support. Registry writes
are not transactional: on a write failure, inspect both current labels and
saved values. Partial or unexpected labels require manual reconciliation with
the original signed artifact; do not force a guessed overwrite.

Source uninstall normally removes its registration and the helper's backup
values, so a successfully removed source needs no label revert.

### Recovering historically quarantined native drafts

Older helper versions moved `native-setup-draft.json` with other non-directory
entries into `gateways-blocking-<timestamp>`. Current inventory accepts the
draft and its exact atomic siblings, so this is no longer a reason to move
them. Keep historical quarantine folders until you have reviewed their contents
against any newer draft. With relevant apps closed, restore only known
test-owned files, without overwriting newer setup state; otherwise retain the
quarantine for investigation.

The former claims that all payload builds were alpha, that a numeric file
version made an alpha relabel faithful, and that the About version was cosmetic
for admission are retired. The current detector checks **both** file and product
versions. Old observations without a recorded source/target revision are not
current-policy proof.

## Automated coverage and its limits

```powershell
.\scripts\test-store-migration-test-script.ps1
```

This fixture suite exercises the actual helper with in-memory registration and
executable metadata, real test-owned files/junctions and real lock contention.
Its process adapters forbid enumeration, launch and termination. Files and all
data-root environment overrides are isolated and cleaned up, even when a
guard fails. It covers current-policy admission, native draft preservation,
quarantine, receipt opt-in, lock preservation, relabel/revert drift, previews
and failure paths.

CI runs it beside the migration MSIX regression on Windows PowerShell 7.
These fixtures do **not** establish Authenticode trust, installer/UAC behavior,
real Store admission, gateway preservation or rendered UI. Those require the
disposable artifact procedure above and the broader
[migration validation lanes](TEST_COVERAGE.md#formal-validation-paths).
