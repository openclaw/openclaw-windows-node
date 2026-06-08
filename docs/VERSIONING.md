# Versioning in OpenClaw Windows Hub

## Source of truth

OpenClaw uses GitVersion and git tags for application versioning. Product
project files must not hardcode release versions with `<Version>` elements.

Canonical release tags use:

- Stable: `vX.Y.Z`
- Alpha: `vX.Y.Z-alpha.N`

`GitVersion.yml` controls how tag history becomes SemVer. The product build
imports GitVersion through `src\Directory.Build.props`, so normal `dotnet build`,
`.\build.ps1`, `.\run-app-local.ps1`, and CI builds all derive assembly metadata
from the same tag history.

## Tagged and untagged builds

Tagged releases must resolve to the exact tag SemVer:

- `vX.Y.Z` -> `X.Y.Z`
- `vX.Y.Z-alpha.N` -> `X.Y.Z-alpha.N`

Untagged `master` checkouts are still prerelease builds. After an alpha tag,
GitVersion advances to the next alpha prerelease until another tag pins the
version. For example, after `v0.6.0-alpha.5`, an untagged commit on `master`
may resolve to `0.6.0-alpha.6`.

`GitVersion.yml` intentionally gives the `master`/`main` branch the `alpha`
label so alpha tags are treated as exact version sources. Do not remove that
label unless the release train stops using alpha tags.

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
and artifact naming. Product builds themselves also use GitVersion-backed
MSBuild metadata; CI should not pass a competing hardcoded `-p:Version=...`
value that could hide drift.

Release build jobs must check out full git history (`fetch-depth: 0`) so
GitVersion can see tags.

Tagged CI runs verify that `github.ref_name` and GitVersion's `SemVer` output
match before build artifacts are published. If a release tag is
`v0.6.0-alpha.5`, CI must produce `0.6.0-alpha.5`; a derived value such as
`0.6.0-alpha.6` or `0.6.0-712` is a release-blocking error.

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

## References

- [Microsoft Docs: Assembly Versioning](https://learn.microsoft.com/en-us/dotnet/standard/assembly/versioning)
- [Updatum Library](https://github.com/sn4k3/Updatum)
- [GitVersion Documentation](https://gitversion.net/)
