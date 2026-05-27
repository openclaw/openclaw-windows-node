# Windows AppInstaller stable feed

This directory is the source-controlled stable update feed for the OpenClaw
Companion MSIX channel.

Installed MSIX packages poll these architecture-specific raw GitHub URLs:

- `https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller`
- `https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-arm64.appinstaller`

The checked-in feed files are bootstrap placeholders at `0.0.0.0` so the raw
URLs exist before the first signed MSIX embeds them. Release builds do not push
these files directly to `master`. After a successful stable release,
`.github/workflows/appinstaller-feed-pr.yml` renders the feed files from the
signed GitHub Release MSIX assets, validates them, and opens a PR. Merging that
PR advances the stable auto-update source for installed clients.

Raw GitHub intentionally mirrors the Mac companion app's Sparkle appcast model,
but Windows App Installer has different hosting requirements. If two-version E2E
testing proves raw GitHub is not accepted by Windows App Installer, keep this
directory as the generated source of truth and publish the same files to a static
host or CDN that serves AppInstaller-compatible headers.

Alpha/pre-release feed updates are blocked until maintainers choose a channel
strategy. Do not hand-edit stable feed files to point at alpha packages.
