---
name: native-gateway-from-source
description: Build the native (MSIX, isolated-session) OpenClaw Gateway from the latest openclaw/openclaw sources (or any ref, local checkout, or prebuilt openclaw.tgz), register it side by side with the Microsoft Store Gateway, and run Companion against it with OPENCLAW_NATIVE_GATEWAY_DEV_PATCH. Use when the user asks to test Companion with Gateway main, an unreleased Gateway change, or a source-built native Gateway, or to remove such a build.
---

# Native Gateway from source

`scripts\Build-NativeGatewayFromSource.ps1` builds OpenClaw from source, packages it with
`openclaw/openclaw-windows-packaging`, and registers a Developer Mode loose package named
`OpenClawFoundation.OpenClawGateway-<patch>` with aliases `openclaw-<patch>.exe` and
`clawctl-<patch>.exe`. A "patch" is a side-by-side identity slot (upstream
`Deploy-LocalPackage.ps1 -Patch`), not a version: each patch has its own app data, isolated
agent account, and Gateway port, and never touches the Store package.

Companion uses the patch only when started with `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<patch>`.
Without it, Companion resolves the Store package and installs it with WinGet only when
missing. See `docs/ONBOARDING_WIZARD.md` and `docs/ARCHITECTURE.md` (native Gateway section).

## Prerequisites

Check before building; the script fails fast on most of these:

- Windows Developer Mode on, isolated-session-capable Windows (native setup's device check).
- PowerShell 7.4 or later (`pwsh`). Windows PowerShell 5.1 cannot run the script.
- git, .NET SDK 10.0.400 or later, Visual Studio Build Tools with C++ and the Windows SDK
  (the packaging repo builds NativeAOT executables).
- Node.js 24.16 or later whose `process.platform/process.arch` is `win32/<OS arch>`.
- pnpm with the same major as the source's `packageManager` (main uses pnpm 12):
  `npm install -g pnpm@<version>`. The script prints the exact version on mismatch.

## Build and register the latest Gateway

```powershell
pwsh -File .\scripts\Build-NativeGatewayFromSource.ps1 -OpenClawRef main -Patch source
```

- First run clones both repos under `%SystemDrive%\OpenClawDev\native-gateway-source` and takes
  roughly 25 to 30 minutes (`pnpm build` dominates). Run it in the background and do not poll.
- Success ends with `Registered OpenClawFoundation.OpenClawGateway-source_<hash>` and
  `OpenClaw <version> (<full sha>)`. Compare the sha with
  `git ls-remote https://github.com/openclaw/openclaw refs/heads/main`.
- `main` of both repos moves often. Each rerun fetches them again; a new commit in either
  repo builds and re-registers a new payload. To get a reproducible rerun, pin both:
  `-OpenClawRef <full sha> -PackagingRef <full sha>`. A pinned rerun prints
  `Reusing payload` and `Already up to date` without rebuilding. ACL validation still
  walks the checkouts and selected payload; large dependency trees take longer.
- Other sources: `-OpenClawSourceDirectory <checkout>` builds a local checkout as-is
  (uncommitted changes allowed, always a fresh payload). `-OpenClawPackageDirectory <dir>`
  takes an existing `openclaw.tgz` + `source.json` and only builds the payload (about
  2 to 3 minutes); previous builds leave these under `<WorkRoot>\packages\<id>`.
- `-Force` builds a new payload and re-registers even when nothing changed.
- Relative `-WorkRoot` paths are anchored to the invoking PowerShell location before
  any directory changes.
- Only one source build or unregister can run on a machine at a time, even across
  different patches and work roots. A concurrent invocation fails before changing files.
  Payload installation also uses a fresh per-run staging directory, removed on exit.
- Prebuilt packages must include valid commit/hash/version metadata. The archive hash
  is checked before payload-cache reuse, not just on a fresh build.

Verify the registration:

```powershell
Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway-source |
    Format-List PackageFamilyName, Version, Publisher, IsDevelopmentMode, InstallLocation
$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\OpenClawFoundation.OpenClawGateway-source_rfcbke2p71se2"
& "$alias\clawctl-source.exe" --version        # Payload line shows the source sha
& "$alias\clawctl-source.exe" status --json    # integration.kind = isolated-session
```

## Run Companion against it

Use isolated tray data so the user's real settings and gateways are untouched:

```powershell
.\build.ps1
$env:OPENCLAW_NATIVE_GATEWAY_DEV_PATCH = 'source'
.\run-app-local.ps1 -NoBuild -DataDir "$env:TEMP\oc-devgw-source"
```

In setup choose **Install a local native gateway**, then **Set up gateway**. Expected:
all four native steps complete and **Connect your AI** loads from the source-built Gateway.
`<data-dir>\gateways\native-setup-draft.json` records
`PackageFamilyName = OpenClawFoundation.OpenClawGateway-source_rfcbke2p71se2`, and
`clawctl-source gateway-service status` reports `[ok] listening` on the recorded port.
No `winget.exe` should start. Clear the patch variable afterwards. Use a fresh
isolated profile to enter onboarding; leave `OPENCLAW_FORCE_ONBOARDING` unset
for restart proof so the replacement process can dispatch the completion handoff.

The variable must be set in the shell that launches Companion; a running Companion does not
pick it up. Profiles created this way resolve only while the variable names the same patch.

## Remove it

```powershell
pwsh -File .\scripts\Build-NativeGatewayFromSource.ps1 -Unregister -Patch source
```

This runs `clawctl-source teardown --force` (removes the isolated session, Gateway records and
agent profile), then unregisters. The Store package stays. Payloads and checkouts under the
work root are kept; delete them manually only after unregistering. Pass the same
`-WorkRoot`/`-PackagingDirectory`/`-Architecture` used to register, or the ownership check
refuses before teardown.

## Pitfalls

- **Never put the work root or packaging checkout under the user profile.** The Gateway runs as
  a separate isolated agent account that must read the payload through every parent folder;
  under `C:\Users\<you>` Node fails with `EPERM ... lstat 'C:\Users\<you>\AppData'` and setup
  reports "OpenClaw could not read the effective Gateway configuration". The script refuses
  such paths. Do not "fix" it with ACL grants.
- **Build trees must not be writable by other accounts.** Newly created work directories
  have protected ACLs: the current user, Administrators and SYSTEM can write; other
  authenticated accounts, Users and application-package groups get read/execute only.
  Existing trees, supplied checkouts and
  prebuilt packages are checked, including linked targets and replaceable ancestors.
  Unsafe paths are rejected without changing their ACLs. Use a fresh root under a trusted
  parent (for example `C:\OpenClawSourceSafe`), not another child of an unsafe directory.
  For an existing registration, have an administrator verify its files and repair its
  permissions before retrying unregister. Do not execute potentially modified aliases
  merely to bypass the check.
- **Never move or delete the work root while registered.** The layout links the payload with a
  junction. Moving keeps the old ACLs (the agent account loses access); `icacls /reset /T`
  over the openclaw checkout loops through pnpm symlinks for a long time. Unregister, then
  rebuild in the new location.
- **A cancelled source build leaves `openclaw\.artifacts\dist-artifacts.lock`.** The next
  `pnpm build` refuses with "Could not acquire ... dist-artifacts.lock". After confirming no
  build process is still running, remove that directory and rerun.
- **Do not run native setup without the variable on a host whose Store Gateway is in use.** It
  pairs a new device with that live Gateway. Prove Store resolution without side effects
  instead (unit tests, or call `NativeGatewayPackageResolver` from a throwaway program).
- Payloads are architecture-specific. An x64 payload on ARM64 registers but `clawctl setup`
  fails there (upstream note); build on the target architecture.

## Error messages and what they mean

| Message | Cause | Action |
|---|---|---|
| `The development Gateway package ...-<patch> is not registered ...` (new setup) | Variable set, patch not registered | Run the build, or clear the variable to use the Store Gateway |
| `This Gateway profile uses the development Gateway package ...-<patch>. Set OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<patch> ...` | Saved profile bound to a patch, variable unset or different | Restart Companion with that variable |
| `This Gateway profile uses ... which is not registered ...` | Saved profile bound to a patch that was unregistered | Re-register the patch, or remove the gateway in Connection settings and set up again |
| `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH must be 1 to 15 letters ...` | Invalid variable value | Use lowercase letters, digits, hyphens |
| `-WorkRoot '...' is inside your user profile ...` | Profile path passed | Use a path such as `C:\OpenClawDev` |
| `The package-qualified aliases were not found ...` | App execution aliases disabled | Enable them in Settings > Apps > Advanced app settings > App execution aliases |
| `Another native Gateway source build or unregister is running ...` | Shared build/registration resources are locked | Wait for that operation to finish, then retry |
| `Native Gateway build path ... grants write or replacement access ...` | Another account can modify code or replace a parent | Use a fresh root under a trusted parent, or have an administrator verify and repair the existing tree |
| `Prebuilt package metadata ... requires ...` | Missing, malformed or truncated provenance | Recreate `source.json` with the package producer; do not invent hashes |

## Proof for PRs

For PRs that depend on a source-built Gateway, follow `openclaw-proof-validation`: report the
registered family, `clawctl-<patch> --version`, the commit tested, and focused app-window
screenshots. Never publish `gateways.json`, setup drafts beyond family and port, tokens, or
identity files.
