# Versioning in OpenClaw Windows Hub

## Source of truth

OpenClaw uses GitVersion and git tags for application versioning. Product
project files must not hardcode release versions with `<Version>` elements.

Canonical release tags use:

- Stable: `vX.Y.Z`
- Stable correction: `vX.Y.Z-N`, where `N` is a positive integer
- Alpha: `vX.Y.Z-alpha.N`

Numeric correction suffixes are an intentional stable-channel exception to
SemVer's usual prerelease interpretation. Corrections sort after their
unsuffixed base release and then by numeric correction: `vX.Y.Z` <
`vX.Y.Z-1` < `vX.Y.Z-2`. A correction must stay on the current Windows latest
release line and strictly advance that line's correction number. A correction
tag must not be published if that exact tag already exists or a newer
stable/correction release has already been published.

Windows Hub stable and correction releases remain an independent version
domain. Managed Gateway setup resolves the npm `latest` package independently
and verifies that the protocol-v4 handshake reports the installed package
version. A Windows Hub correction release does not select a Gateway package
version.

The daily Windows alpha train is the narrow exception. Before creating a tag,
the workflow reads the canonical latest stable release from
`openclaw/openclaw`. If that Gateway release line is numerically newer than the
GitVersion-derived Windows line, the workflow starts the matching Windows train
at `vX.Y.Z-alpha.1`. Equal or older Gateway lines leave GitVersion's result
unchanged, so the lookup can advance but never downgrade Windows.

`GitVersion.yml` controls how tag history becomes SemVer. The product build
imports GitVersion through `src\Directory.Build.props`, so normal `dotnet build`,
`.\build.ps1`, `.\run-app-local.ps1`, and CI builds all derive assembly metadata
from the same tag history.

The repository-local tool manifest (`.config\dotnet-tools.json`) and MSBuild
package reference (`src\Directory.Build.props`) are the authoritative local
tool/package pins and currently target GitVersion 6.8.2. The CI workflow's
`gittools/actions/gitversion/setup` step tracks the matching `6.8.x` line for
workflow output computation. Keep the workflow action on the same major/minor
line as the repository pins so the value injected into product metadata is
computed by the same GitVersion release.

## Tagged and untagged builds

Tagged releases must resolve to the exact tag SemVer:

- `vX.Y.Z` -> `X.Y.Z`
- `vX.Y.Z-N` -> `X.Y.Z-N`
- `vX.Y.Z-alpha.N` -> `X.Y.Z-alpha.N`

Untagged `master` checkouts are still prerelease builds. After an alpha tag,
GitVersion advances to the next alpha prerelease until another tag pins the
version. For example, after `v0.6.0-alpha.5`, an untagged commit on `master`
may resolve to `0.6.0-alpha.6`.

`GitVersion.yml` intentionally gives `master`/`main` and `release/*` branches
the `alpha` label so alpha tags are treated as exact version sources. Release
branches also preserve the version when the current commit is tagged. This
overrides GitVersion's default beta release-branch policy without weakening the
CI guard that requires the computed version to match the release tag.

### Selective alpha backports

Use a release branch from the intended published alpha when only selected fixes
should ship. GitVersion reads the candidate's configuration, not current main.
If the base predates the release-branch versioning repair, first cherry-pick the
reviewed commit that adds the `release` branch override in `GitVersion.yml`.
Omit that cherry-pick only when the base already contains the repair.

In an isolated checkout, prepare the candidate without creating its release tag:

```powershell
$baseTag = '<published-alpha-tag>'
$releaseTag = '<new-unused-alpha-tag>'
$versioningRepairCommit = '<reviewed-release-versioning-repair-commit>'
$fixCommit = '<reviewed-fix-commit>'
git switch -c "release/$releaseTag" $baseTag
# Required when the base predates the versioning repair.
git cherry-pick -x $versioningRepairCommit
git cherry-pick -x $fixCommit
```

Before tagging this checkout, verify the proposed tag in a disposable full-history
clone using the same release branch name. The following check must print the
exact tag version without its `v` prefix. A mismatch is a release blocker, not a
reason to override the computed version:

```powershell
$verificationRepo = Join-Path ([IO.Path]::GetTempPath()) "openclaw-release-check-$([guid]::NewGuid().ToString('N'))"
git clone --no-hardlinks --branch "release/$releaseTag" . $verificationRepo
if ($LASTEXITCODE -ne 0) { throw 'Could not clone the release candidate.' }
Push-Location $verificationRepo
try {
    git tag $releaseTag
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the disposable verification tag.' }
    $computedVersion = ./scripts/Get-OpenClawVersion.ps1 -Variable SemVer
    if ($computedVersion -cne $releaseTag.Substring(1)) {
        throw "Release version mismatch: $computedVersion versus $releaseTag."
    }
    Write-Host "Verified $releaseTag at $(git rev-parse HEAD)."
} finally {
    Pop-Location
    Remove-Item -LiteralPath $verificationRepo -Recurse -Force
}
```

Only after verification succeeds, create and push the actual release tag from
the original candidate checkout:

```powershell
git tag $releaseTag
git push --atomic origin "refs/heads/release/$releaseTag" "refs/tags/$releaseTag"
```

Choose an unused version that advances the intended release line. Do not move
an existing tag or reuse a reserved MSIX package identity after deleting a
release. A maintainer's tag push starts the same full validation, signing, and
publication gates as a main-based alpha. The daily alpha launcher is not the
backport entry point: it always selects current `main`. Tags pushed with
`GITHUB_TOKEN` still require the explicit CI dispatch described below.

CI runs `scripts/test-release-branch-versioning.ps1` with the pinned GitVersion
tool. Disposable repositories cover lightweight and annotated alpha/stable tags,
tag checkouts, and newer main changes that must remain excluded.

## Assembly metadata

GitVersion-derived builds set:

- `AssemblyVersion` and `FileVersion` to numeric versions Windows/.NET can
  compare.
- `AssemblyInformationalVersion` to the SemVer identity used by user-visible
  surfaces.

`OpenClaw.Shared.AppVersionInfo` reads `AssemblyInformationalVersionAttribute`
from the tray assembly and exposes:

- `AppVersionInfo.Version` -> bare SemVer, for example `1.2.3-alpha.4`
- `AppVersionInfo.DisplayVersion` -> `v`-prefixed SemVer, for example
  `v1.2.3-alpha.4`

Build metadata after `+` is stripped before display, but prerelease labels are
preserved. That makes alpha builds identify themselves precisely in About,
diagnostics, support context, `device.info`, MCP handshake metadata, and update
diagnostics.

## CI release flow

The release workflow computes GitVersion in the `test` job for workflow outputs
and artifact naming. It then passes that resolved value to product builds as
`Version` and `InformationalVersion`, keeping assembly metadata and artifacts on
one exact identity. CI must not pass a competing hardcoded version literal that
could hide drift.

The daily alpha workflow runs at 2:00 PM in `America/Los_Angeles`. GitHub
Actions schedules use UTC, so the workflow registers both possible UTC hours
and runs only the one matching the current Pacific UTC offset. It compares the
default-branch head with all published GitHub Releases and does nothing when
one already points at that commit. It defers while an unpublished non-alpha tag
points at the head. When changes exist, it uses the same GitVersion 6.8.x line
and compares that alpha with the canonical latest stable `openclaw/openclaw`
release. A newer Gateway line starts at `vX.Y.Z-alpha.1`; otherwise the
GitVersion alpha is kept. The workflow refuses to create a tag that is not
strictly newer than the newest reachable canonical alpha tag. It then explicitly
dispatches the Build and Test workflow at that tag. The
explicit dispatch is required because a tag pushed with the workflow's
`GITHUB_TOKEN` does not itself start another workflow. The normal test, E2E,
build, signing, and release jobs remain the publication gate; the alpha GitHub
Release is created only when all validation succeeds. After each successful
alpha publication, CI deletes canonical alpha release objects and their assets
once they are older than 30 days. It intentionally retains every Git tag so
GitVersion can continue deriving the next monotonic alpha version from complete
tag history. If the new release is not yet visible through the Releases API,
cleanup defers until the next alpha publication.

Alpha release notes explicitly compare the new tag with the newest reachable
published canonical alpha tag. This avoids GitHub's automatic previous-tag
selection accumulating changes from an older alpha. Within each generated
GitHub release-note section, pull requests follow the Compare API's
oldest-to-newest chronological commit order. The first published alpha falls
back to GitHub's normal comparison-base selection.

GitVersion interprets `X.Y.Z-N` as a prerelease, so numeric stable corrections
use one narrow exception: the release-version step recognizes the correction
tag, validates its stable ordering with
`scripts\Test-OpenClawStableCorrectionRelease.ps1`, and replaces the GitVersion
workflow outputs with the exact tag-derived value before the build. The build's
explicit MSBuild version properties are therefore the validated correction tag,
not an independent version source. Ordinary stable, alpha, and untagged builds
continue to use the GitVersion result directly.

That validator resolves only this repository's latest release and then requires
the candidate to share its `X.Y.Z` base line and carry a strictly greater
correction. It also rejects a candidate that already has a published Windows
release. It performs no other repository's release lookup, and
`-CurrentWindowsTag` evaluates the ordering rule offline.
`scripts\test-stable-correction-release-validator.ps1` covers the accept and
reject matrix plus the workflow's tag classification deterministically in CI.
Every numeric-suffix tag is routed to the validator, so malformed corrections
such as `X.Y.Z-0` cannot be published as an ordinary prerelease instead. The
release job revalidates the same ordering after signing and before publication,
so a correction that stopped being the next release while the build ran cannot
be published as latest.

Release build jobs must check out full git history (`fetch-depth: 0`) so
GitVersion can see tags.

Tagged CI runs verify that `github.ref_name` and the resolved release version
match before build artifacts are published. For stable and alpha tags, the
resolved version must equal GitVersion's `SemVer`. For numeric corrections, the
resolved version must equal the validated tag even though GitVersion classifies
the suffix as a prerelease. If a release tag is `v0.6.0-alpha.5`, CI must produce
`0.6.0-alpha.5`; a derived value such as `0.6.0-alpha.6` or `0.6.0-712` is a
release-blocking error.

## Local scripts

`scripts\Get-OpenClawVersion.ps1` uses the repository-local
`.config\dotnet-tools.json` manifest and `GitVersion.Tool` to print the same
GitVersion value local scripts need outside MSBuild.

For example:

```powershell
.\scripts\Get-OpenClawVersion.ps1 -Variable SemVer
.\scripts\Get-OpenClawVersion.ps1 -Variable MajorMinorPatch
```

`scripts\build-inno-local.ps1` uses that helper for Inno's `AppVersion` when
`-Version` is not explicitly supplied.

## Guardrails

- Do not add `<Version>` release literals to product `.csproj` files.
- Do not hardcode user-visible version strings like `vX.Y.Z` in active code or
  tests; use `AppVersionInfo`.
- Keep release tags and `GitVersion.yml` as the versioning contract.
- Keep `GitVersion.yml` configured so exact alpha tags resolve to their tag
  SemVer, and keep CI's tag/version verification enabled.
- Keep numeric correction tags behind the stable-ordering validation in both
  release resolution and publication; never classify every hyphenated tag as a
  prerelease without recognizing this exception.
- Keep the correction validator free of other repositories' release APIs.
  Stable correction ordering and Gateway-aligned daily alpha selection are
  separate policies.

## References

- [Microsoft Docs: Assembly Versioning](https://learn.microsoft.com/en-us/dotnet/standard/assembly/versioning)
- [Updatum Library](https://github.com/sn4k3/Updatum)
- [GitVersion Documentation](https://gitversion.net/)
