# Hard clean for a fresh OpenClaw retest

This is an agent-guided procedure with a standalone script for demo devices. Use it
when ordinary uninstall leaves native Gateway, Local AI, or isolated test state.
Do not run the dev-build engine uninstall first unless WSL removal is also in the
approved scope. No build is needed to remove an installed native package.

## Standalone script (no Copilot required)

Copy just `scripts\clean-uninstall.ps1` to the test device. No repository checkout,
.NET SDK, Python, or agent is needed. Run **Windows PowerShell 5.1**, as the user
whose install is being removed, not a different administrator account:

```powershell
# Preview only. Does not stop processes, launch uninstallers, or write reports.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\clean-uninstall.ps1

# After reviewing the plan, apply the same scope.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\clean-uninstall.ps1 -ConfirmDestructive
```

The default selects release Companion/native Gateway packages, supported Inno
Companion installations, and ordinary release roaming/local profiles. Native
MXC teardown runs before package removal. Inno uninstall stays interactive:
choose **No** to preserve WSL. Cancelling or failing an uninstall stops cleanup.
Known Inno AppId keys, publisher, versioned display names, and uninstaller paths
must agree. Selected live install registrations are never swept away as profile
data; unrelated installers' metadata is not interpreted as a cleanup target.

WSL registrations are inventoried with their storage paths. If a preserved
distro's storage overlaps any selected profile or uninstall directory, preview
and apply both stop before teardown. The usual
`%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway` location therefore requires
explicit `-RemoveWslGateway` consent, or moving the distro outside cleanup targets
using supported WSL tools first. Selecting **No** in Inno is not sufficient to
protect a disk inside a profile selected for deletion. Storage overlap is checked
again before mutation and profile deletion; preserved registrations and existing
disk files must still be present at completion.

| Option | Additional scope |
| --- | --- |
| `-All` | Shorthand for `-IncludeDev -RemoveCachedModels -RemoveWslGateway`. Still previews by default and respects `-WhatIf`. Extra profile paths must still be supplied explicitly. |
| `-IncludeDev` | Dev Companion package, ordinary dev profiles/startup entries; also the dev distro when `-RemoveWslGateway` is supplied. |
| `-RemoveCachedModels` | Exact GGUF files referenced by schema-4/schema-5 receipts within selected profiles. Preview prints each path/size; apply verifies SHA-256. Shared cache roots and unrelated blobs are never removed. |
| `-ExcludeCachedModels` | Preserve external shared cached models. Overrides both `-All` and `-RemoveCachedModels`, regardless of argument order. Models inside deleted profiles or WSL filesystems are still removed. |
| `-RemoveWslGateway` | Registered `OpenClawGateway`, plus `OpenClawGateway-Dev` only with `-IncludeDev`. Permanently destroys their filesystems. |
| `-AdditionalProfilePath` | Explicit absolute isolated-data directories. No wildcard, automatic session sweep, source checkout, or package-data root is accepted. Pass arrays when calling from PowerShell directly. |
| `-RemoveIsolatedProfilePath` | Exact Windows isolated-profile directories. Uses `Win32_UserProfile` to remove registration and remaining files, including registrations whose folders were already deleted. Only unloaded, non-special `S-1-5-110` profiles directly under Windows `ProfilesDirectory` qualify. Never implied by `-All`; apply requires elevation as the same affected user. |
| `-ReportDirectory` | A new local directory outside cleanup targets. Default: a unique `%TEMP%\OpenClawCleanReports\<id>` directory. |
| `-WhatIf` | Preview even when `-ConfirmDestructive` is present. |

To include all built-in optional scopes without remembering each flag:

```powershell
.\clean-uninstall.ps1 -All
.\clean-uninstall.ps1 -All -ConfirmDestructive
```

`-All` includes the owned release/dev WSL filesystems and receipt-backed cached
model files, not every directory on the device. It does not discover or sweep
custom test profiles. If needed, add
`-AdditionalProfilePath 'C:\Demo\OpenClawTest'` to both commands.

To select all built-in scopes except external shared cached models:

```powershell
.\clean-uninstall.ps1 -All -ExcludeCachedModels
.\clean-uninstall.ps1 -All -ExcludeCachedModels -ConfirmDestructive
```

### Leftover Windows isolated profiles

Deleting a directory under `C:\Users` manually can leave its Windows profile
registration behind. Native teardown verifies the currently recorded session,
not every profile from an older installation. The script reports unselected
`S-1-5-110` profiles as ownership-unverified; it never assumes they are OpenClaw's.
Only select profiles you have independently identified as disposable leftovers.

```powershell
# Use single quotes to keep the literal $ in generated profile names.
$leftovers = @('C:\Users\Demo_A1-B2_$', 'C:\Users\Demo_C3-D4_$')
.\clean-uninstall.ps1 -All -ExcludeCachedModels -RemoveIsolatedProfilePath $leftovers

# Apply from elevated Windows PowerShell as the same affected user.
.\clean-uninstall.ps1 -All -ExcludeCachedModels -RemoveIsolatedProfilePath $leftovers -ConfirmDestructive
```

This destroys all data inside the selected Windows profiles, including models
stored there. Shared caches outside those profiles remain excluded. The script
rechecks SID/path identity and loaded/special state immediately before removal,
then verifies both registration and directory absence. It refuses ordinary user
profiles, ambiguous identities, reparse-point profile roots, and directories
without a corresponding registration. If both registration and directory are
already gone, repeating the command is harmless. Provider/access failures stop
cleanup; there is no fallback to manually deleting registry keys or directories.
This option removes profile registrations and files, not unrecorded MXC backend
sessions or local accounts, and does not replace supported native teardown.

The apply run saves a target plan, transcript, native teardown output, and
hash-verified copies of existing `openclaw-diagnostics-*.zip` files found in
selected profiles. **It does not create a recoverable state/model backup.**
Copy anything you need to retain before confirming. Reports may contain sensitive
local diagnostic information; do not publish them.

### Downloaded model cache

Current downloaded GGUF weights normally live at
`%USERPROFILE%\.cache\huggingface\hub\models--<owner>--<repo>\snapshots\<revision>\<file>.gguf`.
Cache-root precedence is `HF_HUB_CACHE`, `HUGGINGFACE_HUB_CACHE`, `HF_HOME\hub`,
`XDG_CACHE_HOME\huggingface\hub`, then the default above. The cleanup script uses
the exact paths in selected profiles' `LocalAI\state.json` receipts, not today's
environment settings, so it also finds previously customized cache locations.
Nested artifacts such as `snapshots\<revision>\weights\model.gguf` are supported.
The primary model ID and immutable Hugging Face source URL must agree with the
recorded cache path. Each additional asset is checked against its own source
repository, revision, and relative path before any teardown or hashing.

**Shared cached weights are preserved by default.** Add `-RemoveCachedModels` (or `-All`)
to both preview and apply to delete only the listed receipt-backed files,
including additional schema-5 model assets. `-ExcludeCachedModels` overrides either
removal option. This does not sweep the hub, orphan
downloads, unreferenced snapshots, or shared blobs. Symlinked snapshots are
rejected rather than following links into shared blobs. Older app-owned copies
under `<local-profile>\LocalAI\models` are part of profile removal regardless of
this switch; back them up separately if needed.

The script stops on unknown ownership, unreadable inventory, reparse points,
changed PID identity, failed native teardown, and unsupported uninstall entries.
Close development/test instances outside selected install/profile roots manually
before running; the script will not kill them by name. Clear path override
environment variables and supply extra profile paths explicitly.
Supported quoted startup executables must resolve inside selected ownership
roots, including Run entries. Existing Windows 8.3 aliases are expanded before
path comparisons; unresolved short aliases still stop cleanup. Unrelated UNC
special-folder redirection does not block local cleanup, but UNC deletion targets
and reparse-point paths remain unsupported.
Remove an out-of-scope portable/worktree instance's startup registration through
that instance before cleanup; matching a startup name alone does not authorize
the script to change another instance's registration.

Already-uninstalled native packages with orphan `session.json` records need the
matching package repaired/reinstalled for supported teardown. The script does not
guess MXC users or delete accounts directly. Old renamed backups, unrelated test
registry keys, standalone manually installed gateway CLIs, and unspecified
isolated profiles are not automatically removed. Its success message concerns
only the printed scope, not the entire machine.

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File
.\scripts\test-clean-uninstall.ps1` in the repository for isolated safety tests.
These use temporary fixtures and mocked package/process/WSL operations, not a real
uninstall.

## Sources and the gap this closes

The October 2, 2026 hard clean from the "avoid premature timeouts" session used
this order: preserve diagnostics, inventory, stop the exact Companion instance,
run native `clawctl teardown`, remove Gateway and Companion packages, remove
reviewed residual state and receipt-linked model files, then verify absence.
The user subsequently reinstalled successfully. Its old PIDs, ports, package
versions, account name, and profile paths must never be reused as current targets.

[Pedro's OpenClaw Windows node wiper](https://gist.github.com/larroy/23f0758401c2ac75ae28fe71888dbbd6)
is useful prior art for dry-run-first inventory, installed-app removal before
deleting the uninstaller's directory, and residual AppData/registry cleanup.
Do not download and execute it as this skill's implementation. The version
reviewed here matches processes/packages broadly, does not invoke native MXC
teardown, and does not cover receipt-linked Hugging Face weights or arbitrary
isolated profiles. It lists scheduled tasks in its description but has no task
removal implementation. It deliberately preserves WSL, unlike the dev engine.

Current ownership references:

- `src\OpenClaw.Connection\NativeGateway\NativeGatewayPaths.cs`: per-gateway
  `gateways\<id>\native-gateway` state and package-qualified CLI aliases.
- `src\OpenClaw.Connection\LocalAi\LocalAiManifest.cs`: LocalAI directories and
  schema-4/schema-5 cached-model receipts, including additional model assets.
- `src\OpenClaw.SetupEngine\TrayArtifactCleanup.cs`: selected artifact cleanup,
  not removal of every profile or native package.
- `src\OpenClaw.Shared\WindowsStartupTaskRegistration.cs`: scheduled startup task.

Native package implementation and lifecycle contract belong to
[openclaw-windows-packaging](https://github.com/openclaw/openclaw-windows-packaging).
Check the installed CLI's help for compatibility rather than inventing teardown
flags or assuming that stopping a gateway also removes its MXC account.

## 1. Inventory without mutation

Read-only discovery can use broad matches; destructive operations cannot.
For example, these commands only list candidates:

```powershell
Get-AppxPackage | Where-Object { $_.Name -like '*OpenClaw*' } |
    Select-Object Name, PackageFullName, PackageFamilyName, InstallLocation
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -match '^(OpenClaw.*|llama-server|openclaw-wsl-keepalive)\.exe$' } |
    Select-Object ProcessId, ParentProcessId, CreationDate, ExecutablePath
wsl.exe --list --verbose
```

Record command failures as unknown, not "absent". Do not print raw command lines,
environment blocks, gateway configuration, tokens, keys, or entire receipts into
chat or public logs.

Produce an explicit plan with one row per target: kind, exact path/package/task
or PID plus executable and creation time, ownership evidence, proposed action,
size if relevant, and preserve/remove decision. Include:

| Surface | What to inspect |
| --- | --- |
| MSIX packages | Current-user Companion and native Gateway separately. Verify exact Name, publisher/family, and PackageFullName. Do not expand scope to other users or provisioned packages. |
| Win32 installs | Exact registered uninstall entries in HKCU/HKLM, including the 32-bit view. Preserve the uninstaller until it has completed. Do not execute every entry containing "OpenClaw". |
| Ordinary profiles | `%APPDATA%\OpenClawTray`, `%LOCALAPPDATA%\OpenClawTray`, and explicitly selected `-Dev` equivalents. |
| Packaged profiles | `%LOCALAPPDATA%\Packages\<verified-family>\LocalCache\Roaming\OpenClawTray`, redirected local data, and Gateway `LocalState\OpenClawGatewayMSIX`. Resolve from the selected installed package. |
| Native runtime | The package's session record, MXC account identity, gateway IDs, and observed listeners. Read required fields locally without disclosing credentials. |
| Local AI | Each selected local profile's `LocalAI\state.json`, runtime, downloads, logs, legacy models, active cached model, and additional assets. |
| Isolated profiles | Paths printed by launchers or explicitly supplied by the user, including selected children of `%TEMP%\OpenClawTray` or a session's `files` directory. Never discover targets by recursively sweeping all sessions. |
| Startup | Exact Run values `OpenClawTray` / `OpenClawTray-Dev`; tasks `OpenClaw Companion` / `OpenClaw Companion (Dev)`. Verify task action and principal. Inventory services without deleting by name prefix. |
| WSL | Exact distro names and whether any target is in use. Native teardown does not imply WSL removal. |
| Test leftovers | `OpenClawTrayTest`, `OpenClawTests`, and HKCU `Software\OpenClawTests` / `Software\OpenClawDetectorTests` only if specifically identified and included. |

Check path overrides before assuming defaults: `OPENCLAW_APP_IDENTITY`,
`OPENCLAW_TRAY_DATA_DIR`, `OPENCLAW_TRAY_APPDATA_DIR`,
`OPENCLAW_TRAY_LOCALAPPDATA_DIR`, `OPENCLAW_TRAY_LOCAL_DATA_DIR`,
`OPENCLAW_STATE_DIR`, and `OPENCLAW_CONFIG_PATH`. The current shell's environment
does not establish another running app's profile. Use its launch provenance.
Do not automatically delete any directory merely because an override names it.

Resolve cached model paths **before** removing LocalAI receipts. A schema-4
`CachedModelPath` or schema-5 `AdditionalModelPaths` entry identifies a candidate,
not exclusive ownership. Check receipt provenance, canonical snapshot path,
file type, and size. Hugging Face cache roots may be customized.

## 2. Preserve evidence and get approval

Offer to retain diagnostics outside every deletion target; copy the selected
diagnostic ZIP and compare SHA-256 hashes before removing its source. Treat ZIPs
and state backups as sensitive, keep them local, and report their location rather
than their contents. If recovery matters, back up the selected profile before
removal. State clearly when the user elects to delete without a recoverable copy.

Present the plan and ask for explicit destructive confirmation. Call out loss of
pairings/identities, local conversations/configuration, downloaded models and
redownload sizes, and any entire WSL filesystem. Default to preserving shared
model caches, WSL, unrelated profiles, and test harnesses. Ask separately about
unclear scope; "clean retest" alone is not permission to remove every cache or
another session's data.

For the original hard clean, external model deletion was restricted to two
explicitly approved receipt-linked GGUF files. It did not delete the Hugging Face
hub, model repositories, or unrelated blobs. Reproduce that boundary, not those
historical filenames. Never sweep `*.gguf`.

## 3. Execute in ownership order

Stop on failure. Keep an action log outside the targets with exact targets,
outcomes, and exit codes, but no secrets. Do not keep deleting after a failed
teardown or uninstall: the remaining state may be needed for recovery.

1. **Stop selected Companion/Local AI instances.** Prefer graceful exit. If needed,
   re-query each approved PID and verify executable path and creation time
   immediately before `Stop-Process -Id <confirmed-pid>`. Use force only after
   approval. A matching process name is not ownership proof. Do not kill all
   `node.exe`, `llama-server.exe`, or `OpenClaw*` processes. Companion supplies the
   Windows node feature; a node reset does not require uninstalling system Node.js.
2. **Tear down native Gateway while its CLI and session metadata still exist.**
   Resolve `clawctl.exe` from the selected package's qualified alias under
   `%LOCALAPPDATA%\Microsoft\WindowsApps\<verified-family>\clawctl.exe`, not an
   arbitrary PATH executable. Check `teardown --help`. After confirmation, the
   previously verified package contract was:

   ```powershell
   # $clawctl is the already reviewed package-qualified executable.
   & $clawctl teardown --force --json
   if ($LASTEXITCODE -ne 0) { throw 'Native Gateway teardown failed. Preserve remaining state.' }
   ```

   Inspect the structured result as well as the exit code. Require successful
   session/gateway teardown, then verify that the recorded MXC local account,
   its exact profile, owned processes, and listeners are gone. `gateway-service
   stop` is not equivalent. On hosts where `Get-LocalUser` is unavailable, use
   `Get-CimInstance Win32_UserAccount -Filter "LocalAccount = True"` to inspect the
   recorded account. If the package/CLI is already missing or the contract is
   unsupported, report native cleanup blocked and repair through the package's
   supported tooling. Do not guess account names or manually sweep `C:\Users`.
3. **Remove only the approved installed apps.** Use `Remove-AppxPackage -Package
   <exact-PackageFullName> -ErrorAction Stop` for the current user, Gateway before
   Companion. For Win32, run the reviewed registered uninstaller interactively;
   silent Inno uninstall can remove WSL without a separate prompt. Wait for actual
   unregistration, not just launcher exit, since Inno can hand off to a temp
   process. Cancellation, timeout, or a remaining registration is not success.
   Let Windows remove package data; never manually delete WindowsApps or Packages
   roots. Do not delete shared MSIX dependencies.
4. **Remove approved startup remnants.** Delete only the exact reviewed HKCU Run
   values and tasks with verified actions/principals. For an unexplained service
   or task, stop and resolve ownership rather than deleting a prefix match.
5. **Handle WSL only if separately approved.** Use only the exact reviewed owned
   distro (`OpenClawGateway` or `OpenClawGateway-Dev`). Explain that
   `wsl.exe --unregister <exact-name>` permanently deletes its filesystem. Never
   use `wsl --shutdown`, wildcard selection, or an unrelated default distro.
   Check exit code and re-list after unregistering. Preserve shared Tailscale
   sessions and unrelated gateway installs.
6. **Remove approved residual profiles and files last.** Re-inventory after
   uninstall; some paths will already be absent. Apply the path guards below
   before any `Remove-Item -LiteralPath <reviewed-path> -Recurse -Force
   -ErrorAction Stop`. External model assets are individual file removals,
   without `-Recurse`. Never pipe discovery results directly into deletion.

### Mandatory path guards

Every deletion target must be a fully qualified, normalized literal path already
listed in the approved plan. Reject empty paths, wildcards, filesystem/home/
AppData/TEMP roots, repository/worktree roots, `.copilot` roots, session roots,
and shared cache roots. A known profile child is eligible; its containing root is
not. Preserve builds/source, proof harnesses, diagnostics, and unrelated backups.
Only explicitly reviewed isolated-data children within a harness may be removed.

Inspect each existing target and its ancestors for junctions/symlinks/reparse
points. For recursive targets, walk children without following reparse points
and reject any such entry before descending. Reject inaccessible or unresolvable
paths. Re-check immediately before deletion; do not traverse or delete a link's
destination. This is an operator workflow for quiescent, trusted local profiles,
not a race-resistant cleanup service for directories an adversary can modify.

Preview the exact file count and bytes before approval. If scope or path identity
changes, stop and obtain a new decision. Locked files are a failure to investigate,
not grounds for broader process kills, taking ownership, or silent skipping.

## 4. Verify before launching a retest

Re-run read-only discovery and compare to the approved plan:

- Selected package and Win32 registrations, startup entries, and CLI aliases
  are absent, or explicitly retained. Resolve any remaining alias to its owner.
- No selected Companion/llama/Gateway processes or listeners remain. Use the
  observed pre-clean ports, not historical defaults; never kill a new port owner.
- Native teardown removed the recorded MXC account/profile and session. A missing
  package alone does not prove that teardown succeeded.
- Approved profile trees and individual model files are absent. Preserved
  profiles, model files, diagnostic copies, and harness source still exist.
- WSL inventory matches the approved preserve/remove decisions.

Report removed, preserved, and failed/unknown items separately. If any check is
blocked, say "partial cleanup", not "machine clean". Do not automatically relaunch
or reinstall. For a fresh retest, use a new explicitly isolated profile and do not
restore old credentials, settings, or setup receipts into it. Preserved shared
models mean a fresh setup-state retest, not a cold-download retest.
