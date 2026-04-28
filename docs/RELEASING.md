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

## If you need to retag

If a tag points to the wrong commit:

```powershell
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```

