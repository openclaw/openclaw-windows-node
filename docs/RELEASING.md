# Releasing OpenClaw Windows Hub

This repo uses **GitVersion + CI** for release versioning. The canonical release
flow is **tag-driven**: merge to `main`, tag `main`, and let GitHub Actions
build/sign/publish release artifacts.

CI computes GitVersion and stable-correction metadata in the independent
`metadata` job. On `main` and tags, x64 and ARM64 publish jobs start from that
metadata in parallel with tests and E2E, and the stable **CI Gate** requires all
selected lanes before a tag can publish. Pull requests do not produce release
artifacts unless packaging, build, installer, release, workflow, or classifier
infrastructure changes. Those fail-closed pull requests run the x64 publish
smoke only; ARM64 portable publish remains required on `main` and tags.
When either release-build lane is selected, CI also builds both architectures
of Dev-signed and unsigned Store MSIX **workflow artifacts**. CI Gate requires
that MSIX job to succeed. Every tag release also attaches the unsigned Store
MSIX bundle, standalone packages, and metadata for manual Partner Center
submission. Dev-signed packages stay in Actions.

## Inno-to-Store migration foundation

Issue #1374's foundation does not advertise or initiate migration. No Store
action, MSIX importer, or completion writer is enabled by this change.

`MigrationPreparation.Prepare` is a non-UI API for a future explicitly consented
Inno action. It inventories existing state without moving the WSL gateway or
Local AI payload. Its protected intent is stored under
`%APPDATA%\OpenClawTray\store-migration\intent.dpapi`. Repeating preparation with
unchanged state reuses the unexpired intent. Intent expires after 30 days; only
new consent may renew it. Corrupt records require explicit recovery rather than
silent replacement. Preparation is serialized and writes via a flushed sibling
temporary file followed by atomic rename.

`completed.dpapi` is a separate receipt, written only after exclusive source
ownership, a fresh strict inventory matching the protected intent, and canonical
operator credential resolution for the active saved gateway. No active gateway
or unresolved credential fails closed without a receipt. The atomic writer never
replaces an existing receipt. Completed-receipt reads are independent of the
current wall clock: neither delayed removal nor a backward clock correction may
re-enable destructive cleanup. New records still reject future creation times,
and intent expiry and renewal checks remain unchanged. Cryptographic, identity,
path, and structural validation still apply to every read. Finalization after verified
Inno removal owns receipt and intent cleanup. A Store preview finalizer rechecks
the exact source registration and then verifies the canonical source executable,
uninstaller, process image, and mutex have disappeared. It observes the mutex
without acquiring it. Process inspection uses limited-query image access across
all sessions; unrelated paths, proven other-user candidates without an image,
and exited processes do not block. Unresolved possible source processes still
fail closed. The finalizer serializes with `prepare.lock`, rereads the
DPAPI completion receipt under that lock, and recaptures the bounded migration
inventory before applying the receipt's saved auto-start preference through the
packaged startup API. It deletes intent followed by the same validated completion
receipt, so a crash after intent deletion retries from the retained receipt. It
fails closed on source inspection, receipt, inventory, startup-preference, or
cleanup failure and leaves the receipt for restart recovery. It never starts
runtime services or invokes uninstall.

Records use a versioned binary envelope protected by current-user DPAPI. They
bind the migration ID, source version, architecture, Windows SID, canonical
install/roaming/local directories, target Store identity, state fingerprint,
startup preference, timestamps, and record kind. Intent contains inventory
metadata; completion additionally requires the validated target version.
The migration directory has a protected ACL for the current user, SYSTEM, and
Administrators. This is a same-user boundary, not an authenticity guarantee
against a malicious process already running as that user.

`MigrationRecordCodec.cs` is deliberately C# 5/.NET Framework compatible:
the same parser and validation policy runs in the app and in Windows PowerShell
5.1 during uninstall. Inno ships the source and `Test-InnoMigration.ps1`, waits
for the read-only check to exit, and does not load the tray executable for
uninstall. The check returns 10 for validated completion, 0 only when no receipt
is present, and 2 when a receipt exists but cannot be validated or the check
could not run. A present-but-unverifiable receipt (DPAPI failure, schema drift
from the frozen codec, or binding mismatch) preserves state rather than
guessing, so it never authorizes destructive cleanup. The check also bounds
itself with a watchdog so a stalled `Add-Type` cannot hang uninstall.

With valid completion, normal Inno startup shows finish-migration guidance
instead of starting services. The guidance keeps Inno's installer mutex held;
dismissing it exits the app before the user uninstalls. Interactive and silent Inno uninstall skip gateway
cleanup and generated-state deletion, removing only the Inno payload and its
registrations. `Uninstall-LocalGateway.ps1` independently rechecks completion
before destructive work and propagates preservation as exit 10, not cleanup
success. Without valid completion, the existing interactive/silent choices
remain unchanged. Explicit `--uninstall --confirm-destructive` remains a
complete-removal operation and intentionally does not honor migration receipts.

Inno acquires a read-only, read-shared handle on `store-migration\prepare.lock`
before its normal startup receipt check and retains it until process exit, including
failed startup or shutdown. The handle is deliberately not released by managed
shutdown because a failed service disposal could leave state writers alive.
Dev, packaged, and validated browse-only fixture runs do not take this Inno
runtime lease; other isolated profiles use their resolved data directory.
Uninstall also acquires a read-only, read-shared handle before effects and retains
it through payload/registration removal.
The cleanup script joins that lock (and also locks when invoked independently)
before checking the receipt, retaining its handle until cleanup exits. These
handles exclude Store's exclusive preparation/completion/finalization handle.
Only a lock held by an in-progress migration stops uninstall before effects. Any
other unavailable lock means migration state is merely unreadable, so uninstall
continues and suppresses destructive gateway cleanup rather than trapping the user
in an app they cannot remove. Neither path permits an unlocked fallback that would
allow destructive cleanup. Completion re-detects the exact source only after
acquiring the lock,
so an uninstall-first run cannot authorize a receipt from stale source evidence.
Preparation likewise re-detects source evidence under the exclusive file lock.
Both preparation and completion inspect the exact source image across all Windows
sessions under that lock, using the same fail-closed process policy as finalization.
Running sources and busy locks return the close-Inno/Retry decision; uncertain
process inspection blocks migration without publishing a record.

The file handle, not the session-local single-instance mutex, provides continuous
cross-session exclusion for participating Inno releases. Process inspection is a
compatibility backstop for already-running older sources, not a lease preventing
an older binary from starting after the scan. Production's minimum source version
must therefore identify a verified release containing the lifetime-lock safeguard.
Do not enable migration for older nonparticipating releases.

Before rollout, prove exact signed x64 and ARM64 packages, storage/DPAPI access,
restart recovery, current-head guidance, and manual uninstall preservation.
Passing unit or PowerShell contract tests is not signed-package migration proof.

### Store migration consent preview

The Store-side preview adds installation detection, startup admission, and an
explicit consent/close-Inno retry flow, and it writes protected intent and
completion records. It is not the full migration journey: runtime state adoption,
source-version enablement, and the recovery/finalization journey remain out of
scope. **Ordinary
builds remain unchanged.** There is no
production minimum source version, runtime toggle, or environment-variable
bypass. Do not enable migration by choosing the current app version as a
placeholder for a verified safeguard-containing Inno release.

An explicit test build may set `StoreMigrationPreview=true` and
`StoreMigrationPreviewMinimumSourceVersion` to a three- or four-part numeric
test source version. This is accepted only for **Debug MSIX builds with the
production identity**; Release, unpackaged, and Dev-identity preview builds
fail the build. Sign and run these experimental packages only in a disposable
Windows VM. They must not be distributed.

```powershell
dotnet publish .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj `
  -c Debug -r win-x64 --self-contained -m:1 `
  -p:PackageMsix=true -p:DevBuild=false -p:AppxPackageSigningEnabled=false `
  -p:StoreMigrationPreview=true `
  -p:StoreMigrationPreviewMinimumSourceVersion=<verified-test-source-version>
```

The preview:

- Reads only the exact production Inno uninstall registration, not display-name
  matches. Only the default current-user installation at
  `%LOCALAPPDATA%\OpenClawTray` is supported. Custom, machine-wide, ambiguous,
  inconsistent, or unreadable installations block admission.
- Requires coherent source version and executable architecture evidence.
  Prerelease/informational versions do not satisfy the stable version gate.
- Reads existing intent/completion records through the shared DPAPI codec.
  Invalid or inaccessible records block startup. An orphan intent requires
  recovery; completion without Inno requires finalization, not fresh startup.
- Stops before production instance forwarding, settings, gateway/node/MCP
  services, updates, or startup-task reconciliation when migration is needed.
  All normal launch, protocol, and startup-task activations use this gate.
- Shows a localized consent decision through native **Yes / No** buttons.
  The body refers directly to those buttons, without a separate action legend.
  A valid Inno intent does not replace that Store-side consent.
  Before acceptance, every locale discloses protected migration records, the
  normal Inno startup block after successful validation, and the required manual
  uninstall followed by reopening Store. It explains preservation, no automatic
  uninstall, and the option to leave without starting migration. Retry guidance
  permits uninstall only after the preview reports recorded completion.
- After consent, checks the production Inno mutex. While Inno is running, the
  preview instructs the user to close it, choose **Yes** to retry, or choose
  **No** to exit the preview. It never force-closes the source process.
- When Inno is closed, the preview acquires exclusive migration ownership,
  rechecks exact source evidence, and writes a protected, DPAPI-bound inventory
  intent. It then captures the inventory again, requires its fingerprint to
  match the intent, and requires canonical operator credential resolution for
  the active saved gateway before atomically writing a protected completion
  receipt. This is not state adoption. It does not start gateway, node, or MCP
  services; provision or repair gateways; delete source state; invoke uninstall;
  or finalize migration. The receipt blocks normal Store startup and tells the
  user to uninstall Inno manually. On a later Store start, only an exact
  `NotInstalled` result plus absent canonical source payload/process/mutex
  evidence permits finalization. The finalizer does not acquire the Inno-visible
  mutex, serializes on `prepare.lock`, rereads and matches the durable completion
  receipt, recaptures the inventory fingerprint, applies the saved auto-start
  preference through the tray adapter, and clears records only after that call
  succeeds.
- Preserves normal fresh-install behavior when there is no exact Inno
  registration and no pending migration record.

This admission result is not an authorization to uninstall. The consent workflow
reacquires exclusive ownership, revalidates source state, and writes completion
only after its active-gateway credential check succeeds. Runtime state adoption,
source-version enablement, and the complete recovery/finalization journey remain
release gates.

## Release checklist

1. Start clean on current `main`.

   ```powershell
   git switch main
   git fetch origin main --prune
   git reset --hard origin/main
   git clean -fd
   git status --short --branch
   ```

2. Confirm the release workflow contains the intended release policy.

   ```powershell
   Select-String .\.github\workflows\ci.yml -Pattern `
     "Verify Release Binary Signing Policy", `
     "OpenClaw.Tray.WinUI.exe", `
     "build-msix:", `
     "Stage Store MSIX release assets"
   ```

3. Create a new stable, stable correction, or prerelease tag from `origin/main`.
   Never move a previously published tag.

   ```powershell
   # Stable: vX.Y.Z
   # Stable correction on the current Windows latest line: vX.Y.Z-N
   # Prerelease: vX.Y.Z-alpha.N
   $tag = "vX.Y.Z"
   if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) {
       throw "HEAD is not origin/main; do not tag."
   }
   git tag -a $tag -m "OpenClaw Windows Hub $tag"
   git push origin $tag
   ```

4. Watch the tagged workflow.

   ```powershell
   gh run list --repo openclaw/openclaw-windows-node `
     --workflow "Build and Test" `
     --limit 10
   ```

5. Confirm the workflow used the exact tag SemVer. Tagged builds fail before
   publishing if GitVersion disagrees with the tag name.

   ```powershell
   $version = $tag -replace '^v', ''
   .\scripts\Get-OpenClawVersion.ps1 -Variable SemVer
   # Expected: $version
   ```

6. Confirm the GitHub release channel matches the tag. Stable tags should be
   non-prerelease releases; alpha tags should be prereleases and not latest.

   ```powershell
   gh release view $tag --repo openclaw/openclaw-windows-node `
     --json tagName,isPrerelease,isLatest,url,assets
   ```

## Release channel policy

Stable, stable-correction, and alpha tags use the same signed CI release pipeline:

- `vX.Y.Z` creates a normal release eligible to become latest.
- `vX.Y.Z-N` creates a stable correction release eligible to become latest. The
  numeric correction suffix intentionally follows the OpenClaw release
  convention and is not treated as a SemVer prerelease. Windows Hub release tags
  are their own version domain: a correction is a correction of the Windows
  latest release, not a mirror of any other repository's release. CI enforces
  that in `scripts\Test-OpenClawStableCorrectionRelease.ps1`, which requires the
  candidate to stay on the same `X.Y.Z` base line as the current Windows latest
  release and to carry a strictly greater numeric correction. Same, older, and
  different-line corrections fail closed, so GitHub's Latest release marker can
  never move backward. The validator also refuses a candidate that already has a
  published Windows release, and refuses to order against a draft, prerelease,
  or unpublished latest release, so a published tag can never be reused. Every
  numeric-suffix tag, including malformed ones such as `-0` and `-03`, is routed
  through the validator rather than silently classified by GitVersion.
- `vX.Y.Z-alpha.N` creates a prerelease that stable updater checks do not offer.
  It also includes unsigned x64/ARM64 Store submission MSIX files and their
  metadata, not Dev-signed installers. The pre-release is visible on GitHub's
  Releases page but is not promoted as Latest.
  The daily workflow evaluates the default branch at 2:00 PM Pacific, skips a
  head already represented by a published release, and defers while an
  unpublished non-alpha tag points at the head. After each successful alpha
  publication, CI removes canonical alpha release objects and assets older than
  30 days so daily builds do not overwhelm the Releases page. Their Git tags
  remain as GitVersion history. If the new release is not yet visible through
  the Releases API, cleanup defers until the next alpha publication.

An authenticated Gateway on `extended-stable` may defer an ordinary Windows
companion update only when the official GitHub release body contains exactly
one explicit classification marker:

- `<!-- openclaw-update: ordinary -->` permits extended-stable deferral.
- `<!-- openclaw-update: security-critical -->` keeps the update visible.

Add the applicable marker when reviewing the generated release notes. Missing,
duplicated, conflicting, or malformed markers are unverified and keep the
update visible. Release names and prose are not scanned for security keywords.

The validator has no dependency on another repository's release API. Run it
offline against an explicit current release to preview a decision:

```powershell
# Accepted: same 2026.7.1 line, correction 3 > 2
.\scripts\Test-OpenClawStableCorrectionRelease.ps1 `
  -Tag v2026.7.1-3 -CurrentWindowsTag v2026.7.1-2

# Rejected: reuse of a published tag
.\scripts\Test-OpenClawStableCorrectionRelease.ps1 `
  -Tag v2026.7.1-2 -CurrentWindowsTag v2026.7.1-2
```

`scripts\test-stable-correction-release-validator.ps1` runs the full accept and
reject matrix deterministically and is also enforced in CI.

The live `v2026.7.1-2` annotated tag dereferences to commit `f46400aa`, which is
the correction-aware implementation ("Support upstream stable correction release
versions", #1266). Release run 33221425381 built and signed that release's
assets, so `v2026.7.1-2` is the current correction-aware Windows latest release.
Never move, rebuild, or reuse that tag; the next correction on this line is a new
`v2026.7.1-3` tag.

Clients on older unsuffixed `2026.7.1` builds predate the correction-aware
update path. They use Updatum's default parser, which drops the numeric
correction suffix before comparison, so they may need a manual transition to a
correction release. Clients already on `2026.7.1-2` are correction-aware: even
though Updatum 1.3.4's default parsing does not rank `2026.7.1-3` above
`2026.7.1-2`, the `OpenClawReleaseVersion` fallback in the update check pipeline
compares releases under OpenClaw correction ordering and discovers `2026.7.1-3`.

Gateway versions are a separate domain. Managed setup resolves npm `latest`
independently and verifies that the protocol-v4 handshake reports the installed
package version. A Windows Hub correction release does not select a Gateway
package version; see
[`adr/0001-gateway-release-policy.md`](adr/0001-gateway-release-policy.md).

```powershell
git tag -a vX.Y.Z-alpha.N -m "OpenClaw Windows Hub vX.Y.Z-alpha.N"
git push origin vX.Y.Z-alpha.N
```

Current release artifacts are:

- Inno setup installers:
  - `OpenClawCompanion-Setup-x64.exe`
  - `OpenClawCompanion-Setup-arm64.exe`
- Portable ZIP payloads for Updatum:
  - `OpenClawTray-<version>-win-x64.zip`
  - `OpenClawTray-<version>-win-arm64.zip`

Every stable, correction, and prerelease additionally contains:

- `OpenClaw.msixbundle` (recommended Partner Center submission input)
- `OpenClaw-x64.msix` and `OpenClaw-arm64.msix`
- `OpenClaw-x64.msix-metadata.json` and
  `OpenClaw-arm64.msix-metadata.json`

These are **unsigned Store submission inputs, not installers**. Upload the
bundle to Partner Center for one architecture-selecting submission. The
standalone packages remain available for inspection or fallback. Microsoft
signs accepted Store submissions. The release step checks both
architectures' clean source provenance, identity, version, and package hashes,
then proves that the bundle embeds those exact bytes. It fails rather than
publishing a partial or mismatched set.

Dev-signed tester MSIX packages, public certificates, and instructions remain
Actions artifacts only. No production signing step is applied to the unsigned
Store packages.

Store distribution remains paused: automatic Partner Center submission,
Store-signed retrieval and publication, and official lifecycle acceptance
remain follow-up work in #1375. Release submission artifacts do not clear those
rollout gates.

Store versions still end in `.0`. Official tagged builds now reserve distinct
package versions as described below; reruns reuse the same reservation.
See [CI MSIX downloads](../DEVELOPMENT.md#ci-msix-downloads) for Dev certificate
handling, preview limitations, and installation instructions.

## MSIX version allocation

Application release tags and assembly versions remain GitVersion-owned.
MSIX uses `X.Y.(Z * 100 + packaging revision).0` for app base `X.Y.Z`, with
packaging revisions 0-99 shared across alpha, stable, and correction tags on
that base. The correction suffix is not a separate encoded digit. A different
patch or year/month starts its own range. Windows' 65535 component limit still
applies, including to the last partial range.

`.github/msix-version-baseline.json` imports already-used versions. It marks
`2026.9.400.0` used and records the workflow run, source commits, artifact IDs,
and package hashes that substantiate that migration floor. This file is
migration state, not a value to bump for each release. New `2026.9.5` releases
start at `2026.9.500.0`.

The canonical ledger now contains the first live reservation for this release
line at `refs/tags/msix-package/2026.9.4/401`. It targets the commit behind
`v2026.9.4`; an identical second allocator run reused the same reservation
instead of consuming another number. Therefore the next unreserved
`2026.9.4` candidate is `2026.9.402.0`.

PR/main metadata jobs resolve the latest published stable Windows release from
the canonical upstream repository and call
`scripts\Resolve-MsixPackageVersion.ps1` in read-only mode against that release
line and the canonical reservation ledger. While Latest is `v2026.9.4`, their
Store preview is `2026.9.402.0`, even if GitVersion on main or the PR has moved
to a `2026.9.5` development line. This selection affects only MSIX manifests;
assemblies, EXE/ZIP artifacts, GitVersion output, and release tags are unchanged.

Only the upstream `reserve-msix-version` job, guarded to tagged
`push`/`workflow_dispatch` runs and scoped to `contents: write`, passes
`-Reserve`. The matrix consumes its one shared JSON result, avoiding independent
x64/ARM64 allocation. All other jobs retain their existing permissions.

Reservations live in annotated tags
`refs/tags/msix-package/<app-base>/<package-counter>`, targeting the source
commit and containing the allocation JSON. The allocator uses atomic ref
creation, not mutable release assets or the expiring Actions artifact store.
If another run wins the same number, it verifies the competing record and
retries. The same source release tag must always resolve to the same commit
and reservation; moving a source tag is an error.

Do not delete or force-update these records. Failed or cancelled builds keep
their reservations and must be retried with the same source tag. Alpha release
retention deletes release objects/assets, not the allocation tags. They do not
begin with `v` and therefore do not trigger tag-driven release builds. The
active `Protect MSIX package reservations` tag ruleset blocks deletion and
non-fast-forward updates under `refs/tags/msix-package/**/*`; preserving that
ruleset is part of the release contract.

The canonical reservation ledger is an intentional, fail-closed dependency of
the release workflow. If allocation, authentication, permissions, or ledger
validation fails, the tagged release stops before publishing EXE/ZIP assets.
Maintainers must repair and rerun the same source tag rather than bypassing the
allocator or publishing a partial release.

API, authentication, malformed-state, exhaustion, and retry-limit errors fail
closed. No workflow should replace such a failure with a guessed version.

Package metadata contains `msixVersionAllocation`, including source version,
source commit/ref, package base, allocation kind, and reservation ref.
Preview candidates never reserve a number and can change between reruns;
they must not be treated as official Store submissions.

The release stager requires `-VersionInfoPath` for the exact reserved result. It
checks the app version, source commit, reserved allocation, package
version, and both architectures' metadata before copying any assets:

```powershell
.\scripts\Stage-StoreMsixReleaseAssets.ps1 `
  -ArtifactDirectory 'artifacts\msix-release' `
  -OutputDirectory 'msix-release' `
  -Version $appVersion -ExpectedSourceCommit $sourceCommit `
  -VersionInfoPath $reservedVersionInfoPath
```

This does not bypass CI Gate or signing approvals, move existing application
tags, or automate Store submission.

## Manual alpha releases

After the workflow change is on the default branch, a maintainer with Actions
write access can use **Actions > Daily Alpha Release > Run workflow**, or:

```powershell
gh workflow run daily-alpha-release.yml `
  --repo openclaw/openclaw-windows-node --ref main
```

This is a request to release the **current default branch**, not the selected
feature branch. It bypasses only the scheduled time-of-day check. All existing
change, published-head, pending non-alpha tag, canonical GitVersion, and tag
ownership checks remain active. If the head is already published, it skips;
it does not replace the release, move the tag, or force a new version.

When there is an eligible new head, the workflow creates or reuses its
unpublished `vX.Y.Z-alpha.N` tag and dispatches **Build and Test** on that tag.
The full CI Gate and release-signing environment still gate publication.
The release stays a public pre-release with `make_latest: false`.
Existing 30-day alpha retention applies to its submission assets too.

Running **Build and Test** manually on a branch is still build-only. Running
it on an eligible alpha tag uses the same tagged release path. No new
unreviewed-branch or MSIX-only version allocator is introduced.

## Binary signing policy

Only OpenClaw-owned binaries should be signed by the OpenClaw release signing
identity.

OpenClaw-owned binaries:

- `OpenClaw.Tray.WinUI.exe`
- `OpenClaw.Tray.WinUI.dll`
- `OpenClaw.Chat.dll`
- `OpenClaw.Connection.dll`
- `OpenClaw.SetupEngine.UI.dll`
- `OpenClaw.SetupEngine.dll`
- `OpenClaw.Shared.dll`
- `OpenClawTray.FunctionalUI.dll`

Third-party/runtime executables that must not be OpenClaw-signed:

- `tools\mxc\<arch>\wxc-exec.exe`
- `createdump.exe`
- `RestartAgent.exe`
- `SetupEngine\RestartAgent.exe`

CI enforces this with `scripts\Test-ReleaseExecutableSignatures.ps1`. The
verifier inspects every shipped `.exe` and `.dll`, fails closed on unknown
executables and unknown OpenClaw-named binaries, and rejects an OpenClaw
signature on third-party/runtime binaries. When release signing is required,
every allowlisted OpenClaw binary must have a valid signature from the expected
OpenClaw release signer; a valid signature from another publisher is rejected.

CI also checks native runtime dependencies before release packaging. Both the
x64 and ARM64 portable payloads must ship `vcruntime140.dll` in the payload
root for the native speech stack. Both build legs source their loose VC runtime
DLLs from the Visual Studio install on the CI runner (resolved via `vswhere` in
the repo-root `Directory.Build.targets`, consumed by `src\Directory.Build.targets`).
This ensures the bundled CRT is new enough for `onnxruntime`. Local x64 builds
and test hosts use the same resolution whenever the Visual Studio install's
current redist is at least 14.38 and contains the requested architecture; the
`VCRuntime.CefSharp.140` NuGet (14.29) is the warned fallback when that compatible
runtime is missing, stale, or architecture-incomplete. The release validation
script enforces a minimum VC++ runtime version floor (currently 14.38) to
prevent regressions, and the x64 verifier load-probes the native TTS stack
(`onnxruntime.dll`, `sherpa-onnx.dll`, and `sherpa-onnx-c-api.dll`) from the
published payload so app-local runtime mismatches are caught before release.
The release job must Authenticode-verify Microsoft's x64 and ARM64 Visual C++
Runtime redistributables before passing the
architecture-matching redistributable to Inno. The installer runs the
redistributable before launching the tray so clean or stale Windows hosts can
repair the runtime before native speech components initialize, and it
skips the post-install tray launch if the runtime installer fails.

The current Azure Artifact Signing resource is:

- Account: `openclaw`
- Certificate profile: `openclaw`
- Endpoint: `https://eus.codesigning.azure.net/`
- Public trust certificate subject:
  `CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US`

GitHub Actions authenticates with Azure through OIDC, not a stored client
secret. The release job runs in the `release-signing` environment and requires:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Do not add `AZURE_CLIENT_SECRET` back to the release workflow. The Entra app
registration should have a federated credential for:
`repo:openclaw/openclaw-windows-node:environment:release-signing`.

## How CI signs payload binaries

The release workflow does not recursively sign every `.exe`. Instead it creates
temporary signing input directories with hardlinks to only the OpenClaw-owned
executables and DLLs from the x64 and ARM64 payloads, then runs Azure Artifact
Signing on those allowlists. Because these are NTFS hardlinks, signing the
staged file signs the real payload file.

After signing, CI verifies the actual payload directory, not the staging folder.
If hardlink signing does not affect the payload, the verifier fails before
release artifacts are created.

## Expected release workflow jobs

For release tags, the **Build and Test** workflow should run:

- `change-classification` with the `full` result
- `fast-validation`
- `test`
- `e2etests` shards: `setup-connect`, `revocation-recovery`, and `network-recovery`
- `build` matrix entries shown by GitHub as `build (win-x64)` and `build (win-arm64)`
- `CI Gate`
- `release`

The `setup-connect` E2E shard contains the MXC proof tests for the gateway ->
Windows node -> `system.run` path and validates that the expected proof test
names appear in the TRX output. GitHub-hosted runners may report those MXC
proofs as skipped when the host is not MXC-capable; use
`.\scripts\validate-mxc-e2e.ps1` for required local/self-hosted MXC merge
validation. Release tags cannot enter the `release` job until **CI Gate**
confirms classification, fast validation, tests, E2E, and release builds all
succeeded. The `build-msix` and `build-msix-bundle` jobs must also succeed
whenever release metadata is required. The release job downloads and attaches
its unsigned Store bundle, standalone packages, and metadata for every
canonical tag. Dev tester distribution stays workflow-only.

The release job should:

1. Download x64/ARM64 tray payload artifacts.
2. Authenticate to Azure with OIDC in the `release-signing` environment.
3. Sign only the OpenClaw-owned EXEs and DLLs in both payloads.
4. Verify binary signing policy.
5. Create the portable x64 and ARM64 ZIPs.
6. Build Inno installers.
7. Sign installers.
8. Stage the validated unsigned Store MSIX
   bundle, standalone packages, and metadata.
9. Create a GitHub release whose prerelease flag matches the tag, with installer
   and portable ZIP assets plus the Store submission assets.

## Post-release verification

After the release exists, download an installer and both portable ZIPs and
verify:

```powershell
$tag = "v0.6.12" # replace with the tag being verified
gh release view $tag --repo openclaw/openclaw-windows-node `
  --json tagName,isPrerelease,isLatest,url,assets
```

Expected:

- Stable tags: `isPrerelease` is `false`.
- Alpha tags: `isPrerelease` is `true` and `isLatest` is `false`.
- Installer EXEs are signed.
- In ZIP payload:
  - `OpenClaw.Tray.WinUI.exe` is OpenClaw-signed.
  - All listed OpenClaw-owned DLLs are OpenClaw-signed.
  - `wxc-exec.exe`, `createdump.exe`, and `RestartAgent.exe` are not
    OpenClaw-signed.

## If a tag build fails

Do not move a published tag. After the fix is merged to `main`, create a new
tag: increment `alpha.N` for a prerelease, or choose the next intended stable
version.

Use these commands to inspect state:

```powershell
git status --short --branch
git rev-parse HEAD
git rev-parse origin/main
$tagPrefix = "vX.Y.Z" # use the stable or prerelease version family being fixed
git ls-remote --tags origin "refs/tags/$tagPrefix*"

gh run list --repo openclaw/openclaw-windows-node `
  --workflow "Build and Test" `
  --limit 10
```

Only tag when `HEAD == origin/main`.

## Versioning rules

- Do not manually bump project or manifest versions for routine releases.
- Do not add csproj `<Version>` release fallbacks; product versions come from
  GitVersion/tag history.
- Release versions come from the tag (`vX.Y.Z` or `vX.Y.Z-alpha.N`).
- Untagged `master` builds are prerelease builds. After `vX.Y.Z-alpha.N`, an
  untagged commit may resolve to the next alpha prerelease, for example
  `X.Y.Z-alpha.(N+1)`.
- CI computes GitVersion outputs for artifact naming, while product builds use
  GitVersion-backed assembly metadata.
