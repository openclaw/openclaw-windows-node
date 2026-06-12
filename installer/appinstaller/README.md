# Windows AppInstaller stable feed

This directory is the source-controlled stable update feed for the OpenClaw
Companion MSIX channel.

Installed MSIX packages poll these architecture-specific raw GitHub URLs:

- `https://raw.githubusercontent.com/openclaw/openclaw-windows-node/main/installer/appinstaller/openclaw-x64.appinstaller`
- `https://raw.githubusercontent.com/openclaw/openclaw-windows-node/main/installer/appinstaller/openclaw-arm64.appinstaller`

The checked-in feed files are bootstrap placeholders at version `0.0.0.0` so
the raw URLs exist before the first signed MSIX embeds them. End users never
install these placeholders directly — Windows AppInstaller checks them in the
background after the user installs from a real release.

## Release flow

Release builds do **not** push these files directly to `main`. After a
successful stable release tag:

1. `.github/workflows/appinstaller-feed-pr.yml` is triggered (manually via
   workflow_dispatch with the release tag).
2. The workflow renders the per-architecture feed files from the matching
   signed `.msix` release assets via `scripts/render-appinstaller.ps1`.
3. The rendered files are validated via `scripts/validate-appinstaller-hosting.ps1`
   against the hosted GitHub release assets.
4. A pull request is opened against `main` with the regenerated XML.
5. Merging the PR is the human gate that advances installed clients to the
   new version.

Git history is the audit trail for which release each feed file points at.

## Pre-release / alpha channel

Alpha/pre-release feed updates are blocked until maintainers choose a channel
strategy. Do not hand-edit the stable feed files to point at alpha packages —
auto-updating all stable users to a pre-release build is a one-way trip.

## Self-contained WindowsAppSDK

OpenClaw Companion is built with `WindowsAppSDKSelfContained=true`, so the
WindowsAppRuntime is packaged inside each `.msix`. The feed files therefore
emit no `<Dependencies>` block — Windows does not need to download a separate
framework package at install time.
