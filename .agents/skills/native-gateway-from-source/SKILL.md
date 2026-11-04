---
name: native-gateway-from-source
description: "Build, register, select, and remove task-owned side-by-side native Gateway dev patches (npm latest, Packaged release, or source) when Companion testing needs a real native Gateway on a capable Windows client."
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
missing. The local launcher sets the development patch automatically; installed
Companion does not. See [`docs\ONBOARDING_WIZARD.md`](../../../docs/ONBOARDING_WIZARD.md)
and the [native Gateway architecture rows](../../../docs/ARCHITECTURE.md#native-gateway-msix-lifecycle-and-shared-wizard-handoffs).

## Prerequisites

Check before building; the script fails fast on most of these:

- Windows Developer Mode on, isolated-session-capable Windows (native setup's device check).
- PowerShell 7.4 or later (`pwsh`). Windows PowerShell 5.1 cannot run the script.
- git, .NET SDK 10.0.400 or later, Visual Studio Build Tools with C++ and the Windows SDK
  (the packaging repo builds NativeAOT executables).
- For npm/source builds, Node.js satisfying the selected Gateway's engines requirement
  (currently 24.16 or later) whose `process.platform/process.arch` is `win32/<OS arch>`.
- For Packaged, authenticated GitHub CLI (`gh`). No local Node/pnpm build is needed.
- For explicit source builds only, pnpm with the same major as the source's
  `packageManager`. The script prints the exact version on mismatch.

## Default multi-instance native Gateway testing

For isolated test builds that need a real native Gateway, use task-owned named
side-by-side patches by default, including single-instance tests. Use one
authorized, capability-qualified Windows 11 client host instead of one Hyper-V
VM per Gateway. Each patch owns a package family, app data, isolated session and
sign-in recovery task. They still share host CPU/RAM, networking and desktop
resources; cap concurrent work to the host's capacity. Developer Mode and actual
isolated-session/listener-ownership support are required; `windows-latest`,
Windows Server, or a successful registration alone proves neither.

Patch names must be 1 to 15 letters, digits or hyphens, starting and ending
with a letter or digit, and are lowercased. Qualify them with a unique task ID.
Do not reuse another task's patch or rely on the shared helper defaults
`npm-latest` / `packaged`. Authorization must cover registration, session
setup, startup-task creation and eventual teardown, not just building source.

Keep [fixture-backed app tests](../../../docs/GATEWAY_FIXTURE_TESTING.md) as the
cheaper default for app-only behavior. Use a separate reusable local or approved
cloud VM for clean-image/install/upgrade tests, genuine OS isolation, or a host
without the required capabilities. Development patches do not prove signed
Store/MSIX installation or upgrade behavior. Keep required real MXC containment,
Gateway-to-node invocation and WSL proof; do not substitute patch startup for
`.\scripts\validate-mxc-e2e.ps1` when that suite is required.

### Native packaged Gateway proof

The WSL E2E fixture installs npm `latest`. `OPENCLAW_E2E_GATEWAY_VERSION`
selects a candidate version, not a native package test. A WSL run does not
prove Store acquisition, package identity, MXC session setup, or the packaged
Gateway lifecycle. Native package E2E needs a disposable, MXC-capable host and
a verified MSIX from
[`openclaw-windows-packaging`](https://github.com/openclaw/openclaw-windows-packaging).
Record its provenance and digest. Missing provenance, isolation, a capable
runner, or session endpoint integration is a blocker, not a passing skip or an
npm fallback.

### Prepare an isolated test build

Build Companion in the task-owned **openclaw-windows-node** worktree, not the
main/shared checkout. Prepare the real Gateway with the helper's explicit
`-Patch` and `-WorkRoot` before launching Companion. To test packaging itself,
see [direct packaging deployment](references/packaging-direct-deploy.md).

After obtaining runtime authority and checking the prerequisites above, run
from that worktree root in `pwsh`. Replace `t42` consistently with a unique
task ID:

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
instead of `-GatewayChannel` for [explicit source changes](#build-and-register-an-explicit-source-ref).
Keep the same unique patch and work root for each instance's rebuild and
ownership-checked removal.

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

### Select, start and verify each instance

Resolve aliases from the actual registration, not a guessed publisher suffix,
PATH's base `clawctl`, or a shared default endpoint. Repeat this block for each
owned patch:

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

In a separate shell at the worktree root, select one patch and a fresh
task/instance-specific data directory.

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
patches, and a profile resolves only while the variable names the same patch.
Never share profiles, copy real user credentials, or omit selection and
accidentally configure the base Gateway.

For CLI-only testing, configure a distinct `gateway.port` in each owned
instance before `& $clawctl gateway-service start --json`, using the resolved
patched aliases and that scenario's normal configuration flow. Both unconfigured
Gateways otherwise use the upstream default. After either startup path:

```powershell
& $clawctl gateway-service status --json
```

This is a status sample, not a readiness assertion: its exit code does not track
readiness, and failure JSON can omit `gateway`. Poll within the scenario's
startup timeout, inspect each JSON result, and fail with the last status if
readiness is not reached. Malformed JSON or an explicit configuration/ownership
error is a blocker. `start` may exit zero with `state = starting`. Status can
probe or start an already-recorded session, so it is not a passive inventory
command.

Require `integration.kind = isolated-session`, `gateway.state = running`, the
actual `gateway.port` and non-null `gateway.ownership` for each instance. Match
the port/family to that Companion profile and require distinct ports before
parallel tests. A port collision or missing ownership support is a blocker, not
permission to kill another listener. Keep test artifacts and, when enabled,
local MCP endpoints/tokens separate too.

## Channels and launcher shorthand

Channel behavior, WSL CI and the missing native runtime CI are owned by
[RELEASING.md](../../../docs/RELEASING.md#gateway-compatibility-channels) and the
[Gateway development and CI matrix](../../../DEVELOPMENT.md#gateway-development-and-ci-matrix).
The launcher's `-GatewayChannel` shorthand there is for single-developer
compatibility work; isolated test instances use
[task-owned patches](#prepare-an-isolated-test-build). `-NoBuild` skips the
Companion build, not Gateway resolution or packaging.

Helper rules that matter when choosing inputs:

- For an exact npm rerun, pass
  `-OpenClawNpmVersion <exact-version> -PackagingRef <commit> -Patch <task patch>`.
  An incompatible or unavailable release fails explicitly; it never falls back
  to the Store or the other channel.
- Never derive the Gateway version from the Packaged MSIX tag. Pin with
  `-PackagingRelease`; `-PackagingRef` and `-PackagingDirectory` cannot override
  Packaged's provenance.
- `-UseStoreGateway` on the launcher tests the installed Store runtime. An
  existing `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH` selects that patch without
  rebuilding; the launcher trims and lowercases it and treats blank as unset.
  Clear it before explicitly using `-GatewayChannel`. Neither flag changes WSL
  setup; set `Gateway.Version` for a WSL version-specific install.

## Build and register an explicit source ref

Use a task-owned patch and work root, as for any test instance:

```powershell
$patch = 't42-src'
$workRoot = 'C:\OpenClawDev\t42\native-gateway'
pwsh -File .\scripts\Build-NativeGatewayFromSource.ps1 -OpenClawRef main -Patch $patch -WorkRoot $workRoot
```

- The first run clones both repos under the work root and takes roughly 25 to
  30 minutes (`pnpm build` dominates). Run it in the background and do not poll.
- Success ends with `Registered OpenClawFoundation.OpenClawGateway-<patch>_<hash>` and
  `OpenClaw <version> (<full sha>)`. Compare the sha with
  `git ls-remote https://github.com/openclaw/openclaw refs/heads/main`.
- `main` of both repos moves often; each rerun fetches and may re-register. For a
  reproducible rerun, pin both with `-OpenClawRef <full sha> -PackagingRef <full sha>`.
  A pinned rerun prints `Reusing payload` (this script) and
  `Already up to date` (packaging `Deploy-LocalPackage.ps1`) without rebuilding.
- See the script's help for `-OpenClawSourceDirectory`, `-OpenClawPackageDirectory`
  and `-Force`. Relative `-WorkRoot` paths are anchored to the invoking
  PowerShell location.
- Only one source build or unregister can run on a machine at a time, even across
  patches and work roots; a concurrent invocation fails before changing files.
- Prebuilt packages must include valid commit/hash/version metadata. The archive
  hash is checked before payload-cache reuse.

Verify the registration from the actual package family:

```powershell
$package = Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch"
$package | Format-List PackageFamilyName, Version, Publisher, IsDevelopmentMode, InstallLocation
if (-not $package.PackageFamilyName.EndsWith('_rfcbke2p71se2')) { throw 'Unexpected publisher.' }
$aliasRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($package.PackageFamilyName)"
& (Join-Path $aliasRoot "clawctl-$patch.exe") --version   # Payload line shows the source sha
```

To run Companion against it, follow
[selection, startup and verification](#select-start-and-verify-each-instance)
with this patch. To force the first-run wizard, also set
`$env:OPENCLAW_FORCE_ONBOARDING = '1'` before launch and clear it afterwards.
Expected: all four native steps complete, **Connect your AI** loads from the
source-built Gateway, `<data-dir>\gateways\native-setup-draft.json` records
`PackageFamilyName` equal to `$package.PackageFamilyName`, and no `winget.exe`
starts.

## Remove it

```powershell
pwsh -File .\scripts\Build-NativeGatewayFromSource.ps1 -Unregister -Patch $patch -WorkRoot $workRoot
```

Unregister always requires the explicit patch. It runs that patch's
`clawctl-<patch> teardown --force` (removes the isolated session, Gateway
records and agent profile), then unregisters. The Store package stays. Payloads
and checkouts under the work root are kept; delete them manually only after
unregistering. Pass the same `-WorkRoot`/`-PackagingDirectory`/`-Architecture`
used to register, or the ownership check refuses before teardown. Do not
unregister a shared `npm-latest` or `packaged` patch you did not create. For a
patch deployed directly from packaging, see
[direct packaging removal](references/packaging-direct-deploy.md#remove-a-directly-deployed-patch).

## Pitfalls

Error messages and their fixes are in [references\troubleshooting.md](references/troubleshooting.md).

- **Never put the work root or packaging checkout under the user profile.** The
  isolated agent account must read the payload through every parent folder;
  under `C:\Users\<you>` Node fails with `EPERM ... lstat` and setup reports
  that it could not read the effective Gateway configuration. The script refuses
  such paths. Do not "fix" it with ACL grants.
- **Build trees must not be writable by other accounts.** New work directories
  get protected ACLs (current user, Administrators and SYSTEM write; others
  read/execute). Existing trees, supplied checkouts, prebuilt packages, linked
  targets and replaceable ancestors are checked, and unsafe paths are rejected
  without changing their ACLs. Use a fresh root under a trusted parent. For an
  existing registration, have an administrator verify and repair it before
  retrying unregister; do not run potentially modified aliases to bypass the check.
- **Never move or delete the work root while registered.** The layout links the
  payload with a junction, and moving keeps the old ACLs. Unregister, then
  rebuild in the new location. Do not rebuild or remove a payload while a test
  uses it.
- **Do not run native setup without the variable on a host whose Store Gateway
  is in use.** It pairs a new device with that live Gateway. Prove Store
  resolution without side effects instead (unit tests, or call
  `NativeGatewayPackageResolver` from a throwaway program).
- Payloads are architecture-specific. An x64 payload on ARM64 registers but
  `clawctl setup` fails there; build on the target architecture.

## Proof for PRs

For PRs that depend on a source-built Gateway, follow
[openclaw-proof-validation](../openclaw-proof-validation/SKILL.md): report the
registered family, `clawctl-<patch> --version`, the commit tested, and focused
app-window screenshots. Never publish `gateways.json`, setup drafts beyond
family and port, tokens, or identity files.
