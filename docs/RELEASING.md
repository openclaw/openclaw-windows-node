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
silent-ish updates. The supported pattern is a hosted `.appinstaller` XML file
that Windows AppInstaller polls; the CI release job renders one per tag from
`installer/openclaw-companion.appinstaller.template` via
`scripts/render-appinstaller.ps1` and attaches it to the GitHub release as
both `OpenClawCompanion-X.Y.Z.appinstaller` (per-tag) and `latest.appinstaller`
(stable filename for the published gh-pages URL).

### Four ways an `.appinstaller` install gets to a new version

When a user installs by clicking the `.appinstaller` (not the raw `.msix`),
Windows AppInstaller persists the source URL and the embedded
`<UpdateSettings>` block. After that the following triggers can update the
install:

1. **OnLaunch (passive)** — `HoursBetweenUpdateChecks="24" ShowPrompt="true"
   UpdateBlocksActivation="false"`. Windows polls the URL no more than once per
   24 hours at app launch, prompts the user, and applies the update on the
   *next* launch. This is the default path most users will see.
2. **OnLaunch (blocking)** — same poll, but with `UpdateBlocksActivation="true"`
   the app waits while the update applies. We leave this OFF because it adds
   user-visible cold-start latency.
3. **In-app, on demand** — the tray's "Check for updates" menu (when running
   packaged) calls `PackageManager.AddPackageByAppInstallerFileAsync` against
   `https://openclaw.github.io/openclaw-windows-node/latest.appinstaller`. This
   bypasses the 24 h poll window and applies any newer published version
   immediately (and restarts the app).
4. **Windows background scan** — Windows historically re-polls on user sign-in
   and on Start-menu launches. This is best-effort and not contractually
   guaranteed; never depend on it as the only update path for a particular
   user cohort.

### Important caveats for the release operator

- The `Version` attribute in the rendered `.appinstaller` AND the `Version`
  attribute inside `<MainBundle>` AND the `<Identity Version=…>` of the
  attached MSIX must all match exactly. The CI rendering step asserts this;
  if you hand-edit the rendered file before publishing, re-validate manually.
- The release notes "Quick Start" link should point at the **`.appinstaller`**
  URL, not the raw `.msix`. A user who installs from a raw `.msix` does not
  get the AppInstaller poll wired up and is stuck on that version until they
  re-install via `.appinstaller`.
- The `latest.appinstaller` URL on GitHub Pages must keep pointing at the
  currently shipping stable; pre-release alpha builds use their tag-specific
  filename and never overwrite `latest.appinstaller`.
- Publishing `latest.appinstaller` to GitHub Pages is **a separate step** from
  attaching it to the release. Until that's automated, the release operator
  copies the file from the GitHub release into the `gh-pages` branch by hand
  after the release artifacts are validated.

## If you need to retag

If a tag points to the wrong commit:

```powershell
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```


