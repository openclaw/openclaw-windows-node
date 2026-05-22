# Releasing OpenClaw Windows Hub

This repo uses **GitVersion + CI** for release versioning.  
The canonical release flow is **tag-driven**, not manual file patching.

## TL;DR

1. Merge approved changes into `master`.
2. Create and push a semantic tag:
   ```powershell
   git checkout master
   git pull --ff-only origin master
   git tag -a vX.Y.Z -m "Release vX.Y.Z"
   git push origin master
   git push origin vX.Y.Z
   ```
3. CI (`.github/workflows/ci.yml`) builds/signs/publishes artifacts and creates the GitHub release from that tag.

## Alpha MSIX releases

Alpha tags use the same signed CI release pipeline, but GitHub marks them as pre-releases and not latest releases so normal updater checks do not offer them to users.

```powershell
git tag -a vX.Y.Z-alpha.1 -m "Alpha vX.Y.Z-alpha.1"
git push origin vX.Y.Z-alpha.1
```

The stable MSIX package identity is `OpenClaw.Companion`. Alpha-tagged MSIX packages are patched during CI to use `OpenClaw.Companion.Alpha`, which lets testers install the signed alpha package without upgrading a stable MSIX install. Command Palette packaging remains separate from the MSIX package.

## Why this is the correct flow

- `GitVersion.yml` is configured for `ContinuousDelivery` with `tag-prefix: 'v'`.
- CI computes version from git history/tags and passes it to builds (`-p:Version=...`).
- CI patches MSIX manifest version during build, so releases are consistent across EXE/MSIX assets.

## Important rules

- **Do not manually bump** version files for routine releases:
  - `src/OpenClaw.Tray/OpenClaw.Tray.csproj`
  - `src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj`
  - `src/OpenClaw.Tray.WinUI/Package.appxmanifest`
- Treat csproj `<Version>` as a **local fallback** for dev builds.
- Release versions should come from the **tag** (`vX.Y.Z`).

## Verify release pipeline

After pushing a tag, confirm in GitHub Actions:
- workflow: **Build and Test**
- trigger ref: `refs/tags/vX.Y.Z`
- jobs complete successfully (build, build-msix, release)
- release assets are attached to the tag release

## Non-Store auto-update via `.appinstaller`

OpenClaw Companion ships outside the Microsoft Store but still wants
quiet updates. The supported pattern is a signed MSIX with embedded
`.appinstaller` metadata plus a hosted `.appinstaller` XML file that Windows
AppInstaller polls. The CI release job renders one file per architecture from
`installer/openclaw-companion.appinstaller.template` via
`scripts/render-appinstaller.ps1` and attaches tag-pinned AppInstaller files plus
stable-name copies to the GitHub release:

- `OpenClawCompanion-X.Y.Z-win-x64.appinstaller`
- `OpenClawCompanion-X.Y.Z-win-arm64.appinstaller`
- stable-name copy `openclaw-x64.appinstaller`
- stable-name copy `openclaw-arm64.appinstaller`

The `.appinstaller` policy intentionally uses only:

```xml
<UpdateSettings>
  <AutomaticBackgroundTask />
</UpdateSettings>
```

Do not add `OnLaunch` or `ForceUpdateFromAnyVersion` to production output.
Updates should happen quietly in the background and bad releases should be
fixed by shipping a higher roll-forward version.

### How an install gets to a new version

1. **Embedded App Installer metadata** — on Windows builds that support
   `uap13:AutoUpdate`, double-clicking the signed MSIX seeds the stable
   architecture-specific `.appinstaller` URL.
2. **Hosted `.appinstaller` install** — users or enterprise tools can install
   from `openclaw-x64.appinstaller` or `openclaw-arm64.appinstaller`, which
   records the same stable source URL.
3. **Windows background task** — `AutomaticBackgroundTask` lets Windows poll
   that source URL without cold-start App Installer UI.
4. **In-app, on demand** — the tray's "Check for updates" command asks Windows
   to fetch the architecture-specific `.appinstaller` and avoids force-closing
   the tray by default. If an update is accepted while the app is in use, the UI
   should tell the user to restart OpenClaw when convenient.

### Important caveats for the release operator

- The `Version` attribute in the rendered `.appinstaller`, the `Version`
  attribute inside `<MainPackage>`, and the `<Identity Version=…>` of the
  matching MSIX must all match exactly.
- The rendered `<MainPackage ProcessorArchitecture=…>` must match the MSIX URL:
  x64 files point at x64 MSIX assets and arm64 files point at ARM64 assets.
- The embedded stable feed URLs currently point at raw GitHub files in this repo:
  `installer\appinstaller\openclaw-x64.appinstaller` and
  `installer\appinstaller\openclaw-arm64.appinstaller`.
- Raw GitHub mirrors the Mac companion app's Sparkle appcast pattern, but it
  serves repo files with GitHub-controlled headers. Windows App Installer must
  still be proven with red/blue E2E validation before this endpoint is treated as
  durable.
- After the release is created, CI dispatches
  `.github\workflows\appinstaller-feed-pr.yml`. That workflow renders the stable
  feed files from the signed release assets, validates them, and opens a PR.
  Merging that PR advances the stable update source.
- Pre-release/alpha feed updates are intentionally blocked until maintainers
  choose a separate channel strategy.

## If you need to retag

If a tag points to the wrong commit:

```powershell
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```


