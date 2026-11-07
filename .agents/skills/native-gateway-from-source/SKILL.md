---
name: native-gateway-from-source
description: Run side-by-side named Gateway patches as the default for local and future cloud native-Gateway E2E on capable Windows clients. Build from npm latest, a published packaging release with -GatewayChannel Packaged, or explicit sources; select the patch with OPENCLAW_NATIVE_GATEWAY_DEV_PATCH and remove only owned instances.
---

# Native Gateway from source

`scripts\Build-NativeGatewayFromSource.ps1` packages npm `latest` by default with
`openclaw/openclaw-windows-packaging`, and registers a Developer Mode loose package named
`OpenClawFoundation.OpenClawGateway-<patch>` with aliases `openclaw-<patch>.exe` and
`clawctl-<patch>.exe`. A "patch" is a side-by-side identity slot (upstream
`Deploy-LocalPackage.ps1 -Patch`), not a version: each patch has its own app data, isolated
agent account, and Gateway configuration, and never touches the Store package.
Distinct identities do not automatically allocate distinct listening ports.

Companion uses the patch only when started with `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<patch>`.
Without it, Companion resolves the Store package and installs it with WinGet only when
missing. The local launcher now sets the development patch automatically; installed
Companion behavior is unchanged. See `docs/ONBOARDING_WIZARD.md` and
`docs/ARCHITECTURE.md` (native Gateway section).

## Prerequisites

Check before building; the script fails fast on most of these:

- Windows Developer Mode on, isolated-session-capable Windows (native setup's device check).
- PowerShell 7.4 or later (`pwsh`). Windows PowerShell 5.1 cannot run the script.
- git, .NET SDK 10.0.400 or later, Visual Studio Build Tools with C++ and the Windows SDK
  (the packaging repo builds NativeAOT executables).
- For npm/source builds, Node.js satisfying the selected Gateway's engines requirement
  (currently 24.16 or later) whose `process.platform/process.arch` is `win32/<OS arch>`.
- For Packaged, authenticated GitHub CLI (`gh`). No local Node/pnpm build is needed;
  deployment obtains the Node runtime version recorded in the released MSIX.
- For explicit source builds only, pnpm with the same major as the source's `packageManager` (main uses pnpm 12):
  `npm install -g pnpm@<version>`. The script prints the exact version on mismatch.

## Default multi-instance native Gateway testing

For isolated test builds that need a real native Gateway, use task-owned named
side-by-side patches by default, including single-instance tests, local E2E and
future cloud E2E. Use one authorized, capability-qualified Windows 11 client
host instead of one Hyper-V VM per Gateway. Each patch owns a package family,
app data, isolated session and sign-in recovery task. They still share host CPU/RAM, networking and
desktop resources; cap concurrent work to the host's available capacity.
This is test-target guidance, not a CI migration or evidence that a cloud lane ran.
Developer Mode and actual isolated-session/listener-ownership support are required;
`windows-latest`, Windows Server, or a successful registration alone proves neither.

Keep [fixture-backed app tests](../../../docs/GATEWAY_FIXTURE_TESTING.md) as the
cheaper default for app-only behavior. Use a separate reusable local or approved
cloud VM for clean-image/install/upgrade tests, genuine OS isolation, or a host
without the required capabilities. Development patches do not prove signed
Store/MSIX installation or upgrade behavior. Keep required real MXC containment,
Gateway-to-node invocation and WSL proof; do not substitute patch startup for
`.\scripts\validate-mxc-e2e.ps1` when that suite is required.

### Prepare an isolated test build

Build Companion in the task-owned **openclaw-windows-node** worktree, not the
main/shared checkout. Prepare the real Gateway with the helper's explicit
`-Patch` and `-WorkRoot` before launching Companion. This is the recommended
path for test instances; the direct packaging workflow below is an alternative
when testing packaging itself.

After obtaining runtime authority and checking the prerequisites above, run
from that worktree root. Replace `t42` consistently with a unique task ID:

```powershell
$ErrorActionPreference = 'Stop'
$patch = 't42-alpha'
$workRoot = 'C:\OpenClawDev\t42\native-gateway'
if (Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch") {
    throw 'Choose an unused patch, or verify ownership before reusing this instance.'
}
.\build.ps1
if ($LASTEXITCODE -ne 0) { throw 'Companion build failed.' }
.\scripts\Build-NativeGatewayFromSource.ps1 -GatewayChannel Latest -Patch $patch -WorkRoot $workRoot
```

Expected: Companion output belongs to this worktree and the helper registers
`OpenClawFoundation.OpenClawGateway-t42-alpha` from the task-owned work root.
Use `-GatewayChannel Packaged` for released-payload compatibility, or
`-OpenClawRef <full-sha>` / `-OpenClawSourceDirectory <task-owned-checkout>`
instead of `-GatewayChannel` for explicit source changes. Keep the same unique
patch and work root for each instance's rebuild and ownership-checked removal.

Follow [selection, startup and verification](#select-start-and-verify-each-instance)
before running tests. For helper-built instances, `$packagingRoot` is
`Join-Path $workRoot 'openclaw-windows-packaging'`; use the build's architecture
for the registration/layout checks. Set `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH`
before launching with `-NoBuild -Isolated -DataDir <task-instance-directory>`.
That variable reuses the prepared patch without rebuilding it.

`-Isolated` / `-DataDir` isolates Companion's profile, not its build output or
Gateway package. An unqualified `.\run-app-local.ps1 -Isolated` still selects
the shared `npm-latest` patch (or an inherited patch), not a new task instance.
Do not use it as the isolated real-Gateway test recipe. Build Companion once;
for a second Gateway, prepare `t42-beta` serially with the helper, then launch
it with its own profile after alpha is verified listening. Remove helper-built
instances with [the ownership-checking wrapper](#remove-it), passing the same
`-WorkRoot`.

### Alternative: deploy directly from the packaging repository

The direct command belongs to **openclaw/openclaw-windows-packaging**, not this
repository. Start with a clean task-owned clone outside the user profile, under
a trusted parent readable by the isolated account and not writable by unrelated
accounts. Do not deploy from a main/shared checkout or relax ACLs to make one work.
Use its pinned .NET SDK, NativeAOT C++/Windows SDK tools, PowerShell 7 and Developer
Mode prerequisites. The default payload download also needs authenticated `gh`;
it obtains the bundled Node runtime, without a local Gateway npm/source build.

The minimal examples are `.\scripts\Deploy-LocalPackage.ps1 -Patch alpha`, then
`.\scripts\Deploy-LocalPackage.ps1 -Patch beta`. On shared hosts, qualify both
with a unique task identifier as below. Replace `t42` and its path consistently:
names must be 1 to 15 letters, digits or hyphens, starting and ending with a
letter or digit, with no whitespace. They are lowercased. Do not reuse another
task's patch or rely on the shared helper defaults `npm-latest` / `packaged`.
Authorization must cover registration, session setup, startup-task creation and
eventual teardown, not just building source.

```powershell
$ErrorActionPreference = 'Stop'
$packagingRoot = 'C:\OpenClawDev\t42\openclaw-windows-packaging'
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$patches = @('t42-alpha', 't42-beta')
if (Test-Path -LiteralPath $packagingRoot) { throw 'Choose a new task-owned clone path.' }
git clone https://github.com/openclaw/openclaw-windows-packaging.git $packagingRoot
if ($LASTEXITCODE -ne 0) { throw 'Packaging clone failed.' }
Set-Location $packagingRoot
foreach ($patch in $patches) {
    if (Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch") {
        throw "Patch $patch already exists. Confirm ownership or choose another task name."
    }
}
.\scripts\Deploy-LocalPackage.ps1 -Patch $patches[0] -Architecture $architecture
.\scripts\Deploy-LocalPackage.ps1 -Patch $patches[1] -Architecture $architecture
```

Expected: two `OpenClawFoundation.OpenClawGateway-<patch>` registrations with
their own `openclaw-<patch>` and `clawctl-<patch>` aliases, beside the unchanged
base package and other patches. Deploy normally runs `clawctl setup`: it
provisions/reuses the session, installs Node in the agent profile and enables
sign-in recovery, but **does not start a listening Gateway**. `-SkipSetup` leaves
that setup work outstanding. A setup failure may leave a registration; inspect
only the owned patch, then retry or clean it up rather than claiming readiness.

Serialize deployment, rebuild, refresh and unregister operations, including
across agents using the same checkout/cache. The node repo's build helper also
enforces a machine-wide build/unregister lock; do not assume direct packaging
commands have that same guard. Once prepared, separate running instances can
serve independent tests concurrently. Do not rebuild or remove a layout/payload
while a test uses it.

**Cache scope:** rerunning the same patch reuses valid downloads and unchanged
build output. Current packaging keeps payload and Node caches per patch under
`artifacts\local-package\patches\<patch>\<architecture>`; beta's first deployment
does not automatically reuse alpha's payload download. To share an already
prepared payload, pass the same validated, task-owned immutable
`-PayloadDirectory <absolute-payload-root>` on each deployment. It must contain
`app` and `payload-metadata.json`. Keep it alive until every referencing patch
is unregistered; never borrow a cache generation another patch may refresh/delete.
For npm Latest, Packaged or source-ref selection, use the helper workflows below
with explicit unique `-Patch` names and a task-owned `-WorkRoot` instead.

### Select, start and verify each instance

Resolve aliases from the actual registration, not a guessed publisher suffix,
PATH's base `clawctl`, or a shared default endpoint. In the packaging shell,
repeat this block for each owned patch:

```powershell
$patch = 't42-alpha'
$packages = @(Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch")
$expectedLayout = Join-Path $packagingRoot "artifacts\local-package\patches\$patch\$architecture\layout"
if ($packages.Count -ne 1 -or -not $packages[0].IsDevelopmentMode -or
    $packages[0].InstallLocation.TrimEnd('\') -ine $expectedLayout.TrimEnd('\')) {
    throw 'The expected task-owned development registration was not found.'
}
$package = $packages[0]
$aliasRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($package.PackageFamilyName)"
$clawctl = Join-Path $aliasRoot "clawctl-$patch.exe"
$openclaw = Join-Path $aliasRoot "openclaw-$patch.exe"
if (-not (Test-Path -LiteralPath $clawctl) -or -not (Test-Path -LiteralPath $openclaw)) {
    throw 'Enable this package''s app execution aliases in Windows Settings, then retry.'
}
& $clawctl --version
if ($LASTEXITCODE -ne 0) { throw 'Owned Gateway version check failed.' }
```

Build Companion once from its task-owned **openclaw-windows-node** worktree
before launching tests. In a separate shell at that worktree root, select one
patch and a fresh task/instance-specific data directory:

**Setup-local boundary:** `-DataDir` separates Companion's app profile, but
does not redirect setup-local or Local AI state. Without separate overrides,
those paths still resolve under the user's normal `%LOCALAPPDATA%\OpenClawTray`.
The recipe below covers Gateway setup and client behavior, not isolated Local
AI acquisition, removal or concurrent Local AI setup. Before any such operation,
configure and verify a task-owned `OPENCLAW_TRAY_LOCALAPPDATA_DIR` root;
setup appends `OpenClawTray`. If the legacy `OPENCLAW_TRAY_LOCAL_DATA_DIR` is
also set, align it with that effective directory. Do not use the normal user's
Local AI manifest or runtime state as test-owned data.

```powershell
$env:OPENCLAW_NATIVE_GATEWAY_DEV_PATCH = 't42-alpha'
.\run-app-local.ps1 -NoBuild -Isolated -DataDir "$env:TEMP\oc-t42-alpha"
Remove-Item Env:\OPENCLAW_NATIVE_GATEWAY_DEV_PATCH
```

Choose **Install a local native gateway**, then **Set up gateway**. This path
configures and starts the selected patch. Verify alpha is listening before
launching beta with `t42-beta` and `oc-t42-beta`. Companion chooses an available
port for a new draft, but separate drafts do not reserve ports. Serializing
initial setup/start avoids both choosing the same unused default. Preserve the
variable for any relaunch of that profile; an already-running app cannot switch
patches. Never share profiles, copy real user credentials, or omit selection and
accidentally configure the base Gateway.

For CLI-only testing, configure a distinct `gateway.port` in each owned
instance before `& $clawctl gateway-service start --json`, using the resolved
patched aliases and that scenario's normal configuration flow. Both unconfigured
Gateways otherwise use the upstream default. After either startup path, in the
matching packaging shell:

```powershell
& $clawctl gateway-service status --json
```

This command is a status sample, not a readiness assertion. It can return a
nonzero exit code while still `starting`, and failure JSON can omit `gateway`.
Conversely, `not-started` can return zero. Do not put an immediate throw on every
nonzero exit into a readiness loop. Retry pending startup within the scenario's
configured startup timeout, inspect each JSON result, and fail explicitly with
the last status/error if readiness is not reached. Malformed JSON or an explicit
configuration/ownership error is a blocker, not a pending state to ignore.

Require `integration.kind = isolated-session`, `gateway.state = running`, the
actual `gateway.port` and non-null `gateway.ownership` for each instance. Match
the port/family to that Companion profile and require distinct ports before
parallel tests. `start` may exit zero with `state = starting`; use bounded status
polling, not a successful exit alone. Status can probe/start an already-recorded
session, so it is not a passive host inventory command. A port collision or
missing ownership support is a blocker, not permission to kill another listener.
Keep test artifacts and, when enabled, local MCP endpoints/tokens separate too.

### Remove only the owned patch

Close only that task's Companion/test clients. Re-run the exact registration,
layout and alias checks above immediately before teardown. From the same
packaging checkout and architecture, with `$patch` and `$clawctl` resolved for
the instance being removed:

```powershell
& $clawctl teardown --force
if ($LASTEXITCODE -ne 0) { throw 'Teardown failed. Keep the registration for recovery.' }
.\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch $patch -Architecture $architecture
if (Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch") {
    throw 'The owned patch is still registered.'
}
```

This is `clawctl-<name> teardown --force` followed by
`.\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch <name>`, not wildcard
removal or `-ReplaceExistingInstall`. Teardown removes that session/data and
startup task; unregister preserves remaining package app data and caches.
Do not delete the checkout/shared payload until all its registrations are gone.
If unregister happened first, restore the same owned patch to reach teardown.
For helper-created patches, prefer [the ownership-checking wrapper](#remove-it).

Contracts checked against packaging
[local-development guidance](https://github.com/openclaw/openclaw-windows-packaging/blob/2062d05176bdc2ff6732fc439986fcefe2f3e4fe/docs/local-development.md#side-by-side-patched-identities)
and [deployment owner](https://github.com/openclaw/openclaw-windows-packaging/blob/2062d05176bdc2ff6732fc439986fcefe2f3e4fe/scripts/LocalPackage.psm1).
Record the actual packaging commit, payload version/commit, package families,
distinct ports, selected tests/results and cleanup outcome for each future run.
Source inspection is not exercised local/cloud E2E proof.

## Latest and Packaged development

Use one Companion branch for both Gateway compatibility targets. Do not create or
continually synchronize a `release/extended-stable` fork.
The shorthand commands below describe the launcher's channel defaults for
single-developer compatibility work. For isolated test instances, use
[unique task-owned builds and patches](#prepare-an-isolated-test-build) instead.

```powershell
.\run-app-local.ps1 -Isolated
.\run-app-local.ps1 -Isolated -GatewayChannel Packaged
.\run-app-local.ps1 -Isolated -GatewayChannel Packaged -PackagingRelease v2026.9.9-msix.0
```

The first command resolves npm `latest` and builds/registers patch `npm-latest`.
The second selects the latest published packaging release and uses patch `packaged`.
The third pins a specific published release. GitHub publication does not establish
Store availability; select the known shipping release explicitly when needed. Both
retain MSIX isolation and package through `openclaw/openclaw-windows-packaging`.
The launcher uses separate worktree/channel data directories automatically and
restores its environment after launch. The Store package is not updated or replaced.
`-NoBuild` skips the Companion build, not Gateway resolution/packaging.

To prepare only the native Gateway:

```powershell
.\scripts\Build-NativeGatewayFromSource.ps1
.\scripts\Build-NativeGatewayFromSource.ps1 -GatewayChannel Packaged
.\scripts\Build-NativeGatewayFromSource.ps1 -OpenClawNpmVersion 2026.8.35 -Patch pinned
```

Latest is resolved to an exact npm version on every build. Archive SHA-512
integrity, package version and embedded build commit are verified before packaging.
`source.json` records the selector, exact version, commit, npm integrity and archive
SHA-256. For reproducible reruns, specify that exact npm version and a packaging
commit with `-PackagingRef`. Cached archives are reverified before payload reuse.
An incompatible or unavailable release fails explicitly, never falls back to Store
or the other channel.

Packaged verifies the published architecture-specific MSIX SHA-256 and reads its
embedded package version, build commit/build ID and Node runtime. Never derive the
Gateway version from the MSIX tag: packaging can ship an older payload under a newer
MSIX identity. The helper reuses released `app` bytes, checks each file against the
release inventory on extraction and cache reuse, and writes `packaging-release.json`.
It checks out the exact packaging tag commit to build the side-by-side development
launcher. This is released Gateway payload proof, not installation of the original
signed MSIX. `-PackagingRef` and `-PackagingDirectory` cannot override Packaged's
provenance. Pin with `-PackagingRelease`; `-Force` re-registers without changing
the released payload bytes.

Use `-UseStoreGateway` on the local launcher to test the installed Store runtime.
An existing `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH` selects that patch without rebuilding
it. Like Companion, the launcher trims and lowercases patch names and treats blank
values as unset. Clear the variable before explicitly using `-GatewayChannel`.
Neither flag changes WSL setup defaults. For a WSL version-specific setup, explicitly
set `Gateway.Version` to the exact version.

WSL CI follows npm Latest only, matching normal WSL setup. It resolves Latest once
per workflow and runs each selected setup/connect, revocation and network recovery
job once at that exact version via `OPENCLAW_E2E_GATEWAY_VERSION`. Each job's
artifact contains `gateway-release.json`. CI does not download a native MSIX or
duplicate WSL tests against Packaged.

Native Latest/Packaged runtime CI is not implemented. Existing unit/helper and
packaging checks do not prove live isolated-session startup, pairing or recovery;
native runtime proof needs a capability-qualified Windows client host. See the
Gateway development and CI matrix in the repository's `DEVELOPMENT.md`.

## Build and register an explicit source ref

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
$env:OPENCLAW_FORCE_ONBOARDING = '1'
.\run-app-local.ps1 -NoBuild -DataDir "$env:TEMP\oc-devgw-source"
```

In setup choose **Install a local native gateway**, then **Set up gateway**. Expected:
all four native steps complete and **Connect your AI** loads from the source-built Gateway.
`<data-dir>\gateways\native-setup-draft.json` records
`PackageFamilyName = OpenClawFoundation.OpenClawGateway-source_rfcbke2p71se2`, and
`clawctl-source gateway-service status` reports `[ok] listening` on the recorded port.
No `winget.exe` should start. Clear both variables afterwards.

The variable must be set in the shell that launches Companion; a running Companion does not
pick it up. Profiles created this way resolve only while the variable names the same patch.

## Remove it

```powershell
pwsh -File .\scripts\Build-NativeGatewayFromSource.ps1 -Unregister -Patch source
```

Use `-Patch npm-latest` or `-Patch packaged` for the channel builds.
Unregister always requires the explicit patch. This runs the selected patch's
`clawctl-<patch> teardown --force` (removes the isolated session, Gateway records and
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
