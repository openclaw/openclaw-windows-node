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
MSIX bundle, standalone packages, and metadata. After a stable or correction
GitHub release is published, CI submits only the bundle to Partner Center.
Prereleases are never submitted to the Store. Dev-signed packages stay in
Actions.

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

These are **unsigned Store submission inputs, not installers**. The automated
Store job uploads only the bundle for one architecture-selecting submission.
Do not upload the bundle and standalone packages together: Partner Center
correctly rejects that as duplicate x64 and ARM64 packages. The standalone
packages remain available for inspection or fallback. Microsoft signs accepted
Store submissions. The release step checks both
architectures' clean source provenance, identity, version, and package hashes,
then proves that the bundle embeds those exact bytes. It fails rather than
publishing a partial or mismatched set.

Dev-signed tester MSIX packages, public certificates, and instructions remain
Actions artifacts only. No production signing step is applied to the unsigned
Store packages.

Stable and correction releases publish to Partner Center after the GitHub
release succeeds. The Store submission refuses to replace a pending draft and
requires an existing published submission. It creates the package update
without committing, verifies that the draft preserved the published product
metadata, and only then commits it. Store certification and rollout remain
Microsoft-managed asynchronous stages.

## Microsoft Store publication setup

The `submit-microsoft-store` job uses the official packaged-app submission API
with a short-lived GitHub OIDC assertion exchanged once for a Dev Center access
token. It stores no client secret. The job runs in the `microsoft-store` GitHub
environment after `release`, and only for non-prerelease `v*` tags.

Configure that environment before the next stable release:

1. Limit deployment tags to `v*`; do not permit branch deployments.
2. Set these environment variables (they are identifiers, not credentials):
   - `MSSTORE_TENANT_ID`
   - `MSSTORE_CLIENT_ID`
   - `MSSTORE_APPLICATION_ID`
3. Add an Entra federated credential for:
   `repo:openclaw/openclaw-windows-node:environment:microsoft-store`
   with audience `api://AzureADTokenExchange`.
   This must be the application's only credential: do not add client secrets,
   certificates, or additional federated subjects, and do not share the Entra
   application with another publisher.
4. Associate the Entra application with the Partner Center account and grant
   it access to the existing OpenClaw product.
5. Ensure that product has a published submission and no pending draft.
6. Record Store channel acceptance in the release PR before enabling the first
   production submission.

The Store identity is an exclusive writer. Only this serialized GitHub
environment may use it, and operators must not edit an API-created draft in
Partner Center. Microsoft documents that a Partner Center edit invalidates
further API update or commit operations; if that happens, let the workflow
delete its own failed draft and rerun after the product has no pending draft.

The git-controlled policy is [`store-submission.json`](../store-submission.json).
It pins the API origin and scope, OIDC audience, rollout percentage, timeout,
minimum access-token lifetime, environment, and draft ownership behavior.
`scripts\Submit-MicrosoftStore.ps1` rejects existing drafts, creates a new draft
without deleting anything, updates and commits that exact submission ID, and
deletes only its own draft if a pre-commit check fails. It refuses to commit if
another Partner Center writer replaces the draft or if published metadata
changes.
After the commit request starts, an unknown response is never cleaned up
automatically because Partner Center may already have accepted the commit.
Inspect the submission before retrying. Explicit terminal rejections such as
`CommitFailed` or `Canceled` remain eligible for owned-draft cleanup.
The workflow uploads a 90-day evidence artifact containing the submitted bundle
hash and Store submission identifiers. It never includes the OIDC assertion.

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
- `submit-microsoft-store` for stable and correction tags only

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
10. For stable and correction tags, submit only `OpenClaw.msixbundle` to
    Partner Center after the GitHub release succeeds.

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
