# Deploy directly from the packaging repository

Read this only when testing packaging itself. For ordinary isolated test
instances, use the helper in
[Prepare an isolated test build](../SKILL.md#prepare-an-isolated-test-build).

The commands here belong to
[openclaw/openclaw-windows-packaging](https://github.com/openclaw/openclaw-windows-packaging),
not this repository. `scripts\Deploy-LocalPackage.ps1` exists only in a
packaging checkout.

## Clone and deploy

Start with a clean task-owned clone outside the user profile, under a trusted
parent readable by the isolated account and not writable by unrelated accounts.
Do not deploy from a main/shared checkout or relax ACLs to make one work. Use
the packaging repo's pinned .NET SDK, NativeAOT C++/Windows SDK tools,
PowerShell 7 and Developer Mode prerequisites. The default payload download
also needs authenticated `gh`; it obtains the bundled Node runtime without a
local Gateway npm/source build.

The minimal upstream examples are `-Patch alpha` then `-Patch beta`. On shared
hosts, qualify both with a unique task identifier and follow the naming rule in
[the skill](../SKILL.md#default-multi-instance-native-gateway-testing).

(packaging checkout)

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
provisions or reuses the session, installs Node in the agent profile and enables
sign-in recovery, but **does not start a listening Gateway**. `-SkipSetup`
leaves that setup work outstanding. A setup failure may leave a registration;
inspect only the owned patch, then retry or clean it up rather than claiming
readiness.

Then follow
[Select, start and verify each instance](../SKILL.md#select-start-and-verify-each-instance)
with this `$packagingRoot` and `$architecture`.

## Serialization and caches

Serialize deployment, rebuild, refresh and unregister operations, including
across agents using the same checkout or cache. The node repo's build helper
enforces a machine-wide build/unregister lock; direct packaging commands may not
have that guard. Once prepared, separate running instances can serve
independent tests concurrently.

Rerunning the same patch reuses valid downloads and unchanged build output.
Current packaging keeps payload and Node caches per patch under
`artifacts\local-package\patches\<patch>\<architecture>`; beta's first
deployment does not reuse alpha's payload download. To share an already
prepared payload, pass the same validated, task-owned immutable
`-PayloadDirectory <absolute-payload-root>` on each deployment. It must contain
`app` and `payload-metadata.json`. Keep it alive until every referencing patch
is unregistered; never borrow a cache generation another patch may refresh or
delete. For npm Latest, Packaged or source-ref selection, use the helper with
explicit unique `-Patch` names and a task-owned `-WorkRoot` instead.

## Remove a directly deployed patch

Close only that task's Companion and test clients. Re-run the registration,
layout and alias checks from the skill immediately before teardown. From the
same packaging checkout and architecture, with `$patch` and `$clawctl` resolved
for the instance being removed:

(packaging checkout)

```powershell
& $clawctl teardown --force
if ($LASTEXITCODE -ne 0) { throw 'Teardown failed. Keep the registration for recovery.' }
.\scripts\Deploy-LocalPackage.ps1 -Unregister -Patch $patch -Architecture $architecture
if (Get-AppxPackage -Name "OpenClawFoundation.OpenClawGateway-$patch") {
    throw 'The owned patch is still registered.'
}
```

Do not use wildcard removal or `-ReplaceExistingInstall`. Teardown removes that
session, its data and its startup task; unregister preserves remaining package
app data and caches. Do not delete the checkout or shared payload until all its
registrations are gone. If unregister happened first, restore the same owned
patch to reach teardown. For helper-built patches, use
[the ownership-checking wrapper](../SKILL.md#remove-it).

## Contract sources

Checked against packaging
[local-development guidance](https://github.com/openclaw/openclaw-windows-packaging/blob/2062d05176bdc2ff6732fc439986fcefe2f3e4fe/docs/local-development.md#side-by-side-patched-identities)
and the
[deployment owner](https://github.com/openclaw/openclaw-windows-packaging/blob/2062d05176bdc2ff6732fc439986fcefe2f3e4fe/scripts/LocalPackage.psm1).
Record the actual packaging commit, payload version/commit, package families,
distinct ports, selected tests and results, and cleanup outcome for each run.
Source inspection is not exercised local or cloud E2E proof.
