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

Issue #1374 is delivered in two PRs: the preservation/startup foundation, then
the complete migration experience and release enablement. There is no planned
third PR. The Store product ID is **`9NFPR3BGDRR5`**. The selected first supported
Inno release is **`v2026.9.5`**, the planned next patch after `v2026.9.4`, provided
both PRs ship together. The fixed source floor is pinned to **`2026.9.5.0`**.
`MigrationProductionEnabled` is checked in as `true` so the coordinated release
and its CI artifacts carry migration; publication stays gated on the tag-time
acceptance listed under [Coordinated production release](#coordinated-production-release)
below. A listing ID is not evidence that a Store-distributed
package has passed migration acceptance.

`MigrationPreparation.Prepare` is the non-UI inventory API used after explicit
consent and verified source shutdown. It inventories state without moving the WSL gateway or
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
packaged startup API. It deletes explicit consent, intent, then the same validated completion
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

Explicit consent is allowed while Inno holds its runtime reader: consent reads
and grants take a read-shared `prepare.lock` handle, which still excludes Store's
exclusive preparation, completion, and finalization. Grants also take an exclusive
`consent.lock` to serialize writers across sessions. A read never creates either
lock. A durable consent reader can finish while another writer is waiting, and
atomic replacement refuses to overwrite a read-locked record. Consent alone
never permits state adoption or preservation-mode uninstall. Finalization removes
the consent writer lock before deleting consent, intent, and completion, keeping
the completion receipt last.

The file handle, not the session-local single-instance mutex, provides continuous
cross-session exclusion for participating Inno releases. Process inspection is a
compatibility backstop for already-running older sources, not a lease preventing
an older binary from starting after the scan. Production's minimum source version
must therefore identify a verified release containing the lifetime-lock safeguard.
Do not enable migration for older nonparticipating releases.

Before rollout, prove exact signed x64 and ARM64 packages, storage/DPAPI access,
restart recovery, current-head guidance, and manual uninstall preservation.
Passing unit or PowerShell contract tests is not signed-package migration proof.

### Coordinated production release

`src\OpenClaw.Tray.WinUI\Migration.Build.props` owns the common Inno and Store
build contract. The tray project imports it for both x64 and ARM64 publishing,
including CI's installer and Store package payloads:

| Property | Checked-in value | Shipping requirement |
|---|---|---|
| `MigrationStoreProductId` | `9NFPR3BGDRR5` | Verify the listing and installed production package identity. |
| `MigrationMinimumSourceVersion` | `2026.9.5.0` | Verify that the selected first stable Inno release contains all preservation, lifetime-lock, and startup safeguards. |
| `MigrationProductionEnabled` | `true` | Enabled for the coordinated `2026.9.5` release. Set to `false` to ship a release without migration. |

Production builds emit `PRODUCTION_MIGRATION` and protected-flow configuration
metadata only for non-Dev **Release** builds targeting `win-x64` or `win-arm64`.
Enabling production without a valid, nonzero three- or four-part numeric source
floor fails the build. Debug and Dev builds remain outside this production gate;
Debug previews require their separate explicit flags. There is no runtime or
environment-variable activation switch.

The same coordinated release can introduce the safeguards and the full migration
flow. A separate preliminary Inno release is not required. Users on older Inno
versions must **update Inno first**, then migrate: Store installation cannot repair
an older uninstaller or give an older running binary the lifetime lock.

The minimum is the **fixed first supported Inno version**, not the current target
app version, GitVersion output, or reserved MSIX package version. Do not raise it
on each subsequent release. If the first release changes before shipping, update
the single pinned value and repeat acceptance with those exact artifacts.

Before tagging the coordinated release or claiming issue #1374 complete:

- Verify the selected `v2026.9.5` release contains both PRs and make that supported
  Inno update available to existing users, with update-first guidance for older
  sources. If the release number or contents change, revise the pin before shipping.
- Prove exact production identity, Store listing handoff, DPAPI/storage access,
  and signed x64 and ARM64 packages through the real distribution path.
- Exercise fresh consent, running-Inno graceful shutdown, manual close/Retry,
  restart recovery, older-source rejection, and ordinary non-migration startup.
- Verify normal interactive and silent Inno removal preserves the completed
  migration, while explicit full cleanup retains its separate destructive contract.
- Verify saved gateway credentials, a real WSL or remote gateway connection,
  Local AI state, and startup preference survive; Store services start only after
  exact source removal and finalization.
- Collect current UI evidence for longer consent, light/dark themes, compact
  windows, scaling, keyboard navigation, and screen-reader behavior. Unit tests,
  fixture screenshots, and locally test-signed packages do not replace missing
  signed-package, ARM64, accessibility, or gateway continuity proof.

These are tag-time gates, not merge-time gates. The switch is checked in as `true`
so the coordinated release and its CI artifacts carry migration, but a tag must not
be published until each item above is satisfied against that tag's real artifacts.
If acceptance fails before the Store package is published, set
`MigrationProductionEnabled` to `false` and retag rather than shipping an unverified
launch gate. A synthetic source floor supplied for disposable package testing is not
approval of a production release version.

The switch is a pre-publication gate, not a recall. Once a Store package carrying
`PRODUCTION_MIGRATION` is published, the floor and the entire flow are compiled into
that package: a later Inno release built with the switch off cannot disable migration
for users who already have the Store build, and that Inno release still sits above the
pinned floor. Turning the switch off only prevents *new* migration-capable Store
builds. To stop migrations already reaching users, publish a corrected Store package.

Two things keep the Store app from starting. A handoff holding a completion receipt
keeps it from starting because that state has data that must not be abandoned, and the
receipt decides this rather than the state name: a receipt still protects the handoff
when the source looks unsupported, when the recorded source version no longer matches,
or when the record cannot be decoded. Separately, a previous app that is positively
detected as installed keeps the Store app inactive even with no receipt, because issue
#1374 permits only one active production client. That block requires payload evidence,
not just an uninstall registration, so an interrupted uninstall that leaves an orphan
registry key cannot strand the user without a working client. The two blocks differ in
how long they last, and the difference matters when supporting a user. The receipt block
is durable: it records that data has already moved, so a later pass that fails or cannot
read the record does not release it. The installed-source block is only ever as good as
the pass that measured it, so it is recomputed every time and never carried forward.
Removing the previous app is precisely how a user ends that block, and a stale copy of it
would keep the Store app closed after the previous app was already gone. The remaining
unhappy paths, a failed inspection or a record needing recovery, inform the user and then
continue to normal startup, because refusing to launch cannot repair either one.
Still confirm before the tag that the released Inno installer registers
`DisplayVersion` `2026.9.5` and `DisplayName` `OpenClaw Companion version 2026.9.5`:
a prerelease suffix or a mismatched name is rejected as an unsupported installation,
so that user is told migration is unavailable instead of being offered it.

### Developer migration test package

Neither standard MSIX artifact can exercise migration. The unsigned Store package
is a submission asset and cannot be installed, and the Dev package is refused by
the Store migration guard because its identity is not the production one.

CI therefore also publishes `openclaw-msix-dev-migration-test-<arch>` from
`scripts\Export-MigrationTestMsix.ps1`: the production-identity Store package,
test-signed with a disposable certificate whose subject matches the production
publisher. "Dev" in the artifact name means *for developers*; the package identity
is production.

That identity is what makes it useful and what makes it dangerous:

- It shares a package family name with the Store release, so it is **not**
  side-by-side. Uninstall the Store build before installing it.
- Migration runs against real data directories. The Store guard rejects data path
  redirection, so it cannot be aimed at a scratch profile.
- With an Inno installation present, it will adopt and then uninstall it.

Use a disposable machine or VM. The exporter refuses to sign an already-signed
package and refuses any package built without migration enabled, so the artifact
cannot silently ship disabled. It stays workflow-only and must never be published.

### Migration experience and Debug previews

The Store-side preview hosts a dedicated WinUI migration window before normal
services start. Release non-Dev builds now ship this experience by default;
Debug and Dev builds do not. Do not enable migration by choosing the current app
version as a placeholder for a verified safeguard-containing Inno release.
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

The optional unpackaged Inno handoff is independently gated:

```powershell
dotnet publish .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj `
  -c Debug -r win-x64 --self-contained -m:1 `
  -p:PackageMsix=false -p:DevBuild=false -p:InnoMigrationPreview=true `
  -p:MigrationPreviewStoreProductId=<explicit-12-character-Store-product-ID>
```

Use that payload only in a disposable, exact current-user Inno fixture.
Release, packaged, and Dev-identity Inno previews fail the build. No preview
product ID is supplied by default. Without one, Settings hides **Install Store version
and migrate**. The ID accepts only 12 uppercase ASCII letters/digits and opens
`ms-windows-store://pdp/?ProductId=...`; do not substitute a guessed listing.
The shutdown receiver can still be exercised without a Store listing.

The Settings action confirms explicit consent before saving `consent.dpapi`
and opening the listing. This is a distinct `consent` record in the shared
current-user DPAPI contract, not an inventory intent. It binds the exact
source version, user, architecture, canonical paths and target package identity,
expires after 30 days, and rejects a future creation time. Missing, expired,
invalid or source-mismatched consent requires Store confirmation. Operational
read failures block instead of silently consenting. Consent and completion
remain independent: consent never authorizes destructive uninstall.
Explicit confirmation does not overwrite corrupt consent. A rejected grant
enters recovery, which stays retryable: the window keeps Retry and the
Installed apps shortcut so a user who removes the previous app is noticed by
the next pass, and it offers to discard migration records that can no longer
carry the handoff. Records are judged one file at a time, so a corrupt receipt
beside a good intent costs only the receipt. Two things are discardable: a
record that does not decode, and a leftover consent or intent once the previous
app is gone and no readable receipt remains, because there is then nothing left
to migrate from. A receipt that still decodes is never deleted, and a retry
never treats an earlier confirmation as still standing. A discard that cannot
run, usually because the previous app is running and holds the migration lease,
is reported in the window rather than passing silently.

The preview:

- Reads only the exact production Inno uninstall registration, not display-name
  matches. Only the default current-user installation at
  `%LOCALAPPDATA%\OpenClawTray` is supported. Custom, machine-wide, ambiguous,
  inconsistent, or unreadable installations block admission.
- Requires coherent source version and executable architecture evidence.
  Prerelease/informational versions do not satisfy the stable version gate.
- Reads existing intent/completion records through the shared DPAPI codec.
  A completion receipt requires finalization before normal startup, and that holds
  even when the receipt cannot be decoded or read: its presence on disk proves data
  already moved. An orphan intent, or an unreadable record with no receipt beside it,
  is reported and then gets out of the way, because refusing to launch cannot repair
  it. Completion without Inno requires finalization, not fresh startup.
- Stops before production instance forwarding, settings, gateway/node/MCP
  services, updates, or startup-task reconciliation when migration is needed.
  All normal launch, protocol, and startup-task activations use this gate.
- Shows localized **Migrate / Not now** buttons unless valid explicit handoff
  consent already exists. An inventory intent never replaces consent.
  Keyboard focus defaults to Not now. Not now closes the Store window without
  changing source state, and leaves the Store app inactive while the source app
  is installed, per issue #1374. The user must migrate or remove the source app
  before the Store app starts normally.
  Unlike PR 1's native Yes/No preview, this window labels its buttons directly;
  it does not append a separate action legend or reinterpret Yes/No as Migrate.
  Before acceptance, every locale discloses protected migration records, the
  normal Inno startup block after successful validation, and the required manual
  uninstall followed by Store finalization. It explains preservation, no automatic
  uninstall, and the option to leave without starting migration. Retry guidance
  permits uninstall only after the window reports recorded completion.
- After consent, requests graceful exit through the existing current-user
  activation pipe. This is a private fixed IPC message, not an `openclaw:`
  route. Only the gated, exact Inno process accepts it after rechecking protected
  consent, then delegates to the canonical app shutdown coordinator. The sender
  bounds IPC to two seconds and checks mutex release for up to ten seconds.
  Unsupported, unavailable or slow receivers leave **Close the previous app /
  Retry** guidance. Sending the message alone is not proof of exit; source
  ownership is checked again before preparation and completion. No force-kill.
- When Inno is closed, the preview acquires exclusive migration ownership,
  rechecks exact source evidence, and writes a protected, DPAPI-bound inventory
  intent. It then captures the inventory again, requires its fingerprint to
  match the intent, and requires canonical operator credential resolution for
  the active saved gateway before atomically writing a protected completion
  receipt. Data is adopted in place. It does not start gateway, node, or MCP
  services; provision or repair gateways; delete source state; invoke uninstall;
  automatically. The receipt blocks normal Store startup and exposes **Open
  Installed apps**. The user uninstalls Inno manually, then selects **Retry** or
  reopens Store. Only an exact
  `NotInstalled` result plus absent canonical source payload/process/mutex
  evidence permits finalization. The finalizer does not acquire the Inno-visible
  mutex, serializes on `prepare.lock`, rereads and matches the durable completion
  receipt, recaptures the inventory fingerprint, applies the saved auto-start
  preference through the tray adapter, and clears records only after that call
  succeeds. Successful finalization resumes the original launch, including any
  pending protocol activation. Errors remain visible with Retry; state transitions
  are announced to accessibility clients and no percentage is invented.
- Preserves normal fresh-install behavior when there is no exact Inno
  registration and no pending migration record.

This admission result is not an authorization to uninstall. The consent workflow
reacquires exclusive ownership, revalidates source state, and writes completion
only after its active-gateway credential check succeeds. Official signed-package
acceptance on x64 and ARM64, local and remote gateway continuity, verification
of the selected Inno release's safeguards, actual Store handoff, and production
enablement remain release gates. The source floor and Store listing ID are
assigned above; UI tests with fake operations are not package-boundary or real
migration proof.

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
