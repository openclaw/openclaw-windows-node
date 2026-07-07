# Releasing OpenClaw Windows Hub

This repo uses **GitVersion + CI** for release versioning. The canonical release
flow is **tag-driven**: merge to `main`, tag `main`, and let GitHub Actions
build/sign/publish release artifacts.

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
     "Verify Release Executable Signing Policy", `
     "OpenClaw.Tray.WinUI.exe", `
     "build-msix:", `
     "Paused for alpha"
   ```

3. Create a new tag from `origin/main`. Prefer a new alpha tag over moving a
   previously failed tag.

   ```powershell
   $tag = "v0.6.0-alpha.4"
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

6. Confirm the GitHub release is a prerelease and not latest for alpha tags.

   ```powershell
   gh release view $tag --repo openclaw/openclaw-windows-node `
     --json tagName,isPrerelease,isLatest,url,assets
   ```

## Alpha release policy

Alpha tags use the same signed CI release pipeline, but GitHub marks them as
pre-releases and not latest releases so normal updater checks do not offer them
to stable users.

```powershell
git tag -a vX.Y.Z-alpha.N -m "OpenClaw Windows Hub vX.Y.Z-alpha.N"
git push origin vX.Y.Z-alpha.N
```

For the current alpha flow, ship only:

- Inno setup installers:
  - `OpenClawCompanion-Setup-x64.exe`
  - `OpenClawCompanion-Setup-arm64.exe`
- Portable ZIP payloads for Updatum:
  - `OpenClawTray-<version>-win-x64.zip`
  - `OpenClawTray-<version>-win-arm64.zip`

MSIX artifacts are intentionally paused for alpha while we focus on the Inno
installer path and signed portable update payloads. Re-enable MSIX only when we
explicitly want packaged camera/microphone consent validation again.

## Executable signing policy

Only OpenClaw-owned executables should be signed by the OpenClaw release signing
identity.

OpenClaw-owned executables:

- `OpenClaw.Tray.WinUI.exe`

Third-party/runtime executables that must not be OpenClaw-signed:

- `tools\mxc\<arch>\wxc-exec.exe`
- `createdump.exe`
- `RestartAgent.exe`
- `SetupEngine\RestartAgent.exe`

CI enforces this with `scripts\Test-ReleaseExecutableSignatures.ps1`. The
verifier fails closed on unknown `.exe` files so future payload changes are
reviewed deliberately.

CI also checks native runtime dependencies before release packaging. Both the
x64 and ARM64 portable payloads must ship `vcruntime140.dll` in the payload
root for the native speech stack. Both build legs source their loose VC runtime
DLLs from the Visual Studio install on the CI runner (resolved via `vswhere` in
`src\Directory.Build.targets`). This ensures the bundled CRT is new enough for
`onnxruntime` — the `VCRuntime.CefSharp.140` NuGet is only used as a dev-time
convenience for local `dotnet build` (not publish). The release validation
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

## How CI signs payload executables

The release workflow does not recursively sign every `.exe`. Instead it creates
temporary signing input directories with hardlinks to only the OpenClaw-owned
executables from the x64 and ARM64 payloads, then runs Azure Artifact Signing on
those allowlists. Because these are NTFS hardlinks, signing the staged file
signs the real payload file.

After signing, CI verifies the actual payload directory, not the staging folder.
If hardlink signing does not affect the payload, the verifier fails before
release artifacts are created.

## Expected release workflow jobs

For alpha tags, the **Build and Test** workflow should run:

- `repo-hygiene`
- `test`
- `e2etests` shards: `setup-connect`, `revocation-recovery`, and `network-recovery`
- `build` matrix entries shown by GitHub as `build (win-x64)` and `build (win-arm64)`
- `release`

The `setup-connect` E2E shard contains the MXC proof tests for the gateway -> Windows node -> `system.run` path and validates that the expected proof test names appear in the TRX output. GitHub-hosted runners may report those MXC proofs as skipped when the host is not MXC-capable; use `.\scripts\validate-mxc-e2e.ps1` for required local/self-hosted MXC merge validation. The `build-msix` job is disabled with `if: false` while MSIX is paused for alpha releases, so it should not appear in the required run list.

The release job should:

1. Download x64/ARM64 tray payload artifacts.
2. Authenticate to Azure with OIDC in the `release-signing` environment.
3. Sign only the OpenClaw-owned EXEs in both payloads.
4. Verify executable signing policy.
5. Create the portable x64 ZIP.
6. Build Inno installers.
7. Sign installers.
8. Create a GitHub prerelease with installer and x64 ZIP assets only.

## Post-release verification

After the release exists, download the x64 installer and ZIP and verify:

```powershell
$tag = "v0.6.0-alpha.4"
gh release view $tag --repo openclaw/openclaw-windows-node `
  --json tagName,isPrerelease,isLatest,url,assets
```

Expected:

- `isPrerelease` is `true`.
- `isLatest` is `false` for alpha tags.
- Installer EXEs are signed.
- In ZIP payload:
  - `OpenClaw.Tray.WinUI.exe` is OpenClaw-signed.
  - `wxc-exec.exe`, `createdump.exe`, and `RestartAgent.exe` are not
    OpenClaw-signed.

## If a tag build fails

Do not keep moving a tag repeatedly from chat unless you are certain GitHub and
local refs agree. Prefer a fresh alpha tag (`alpha.N+1`) after the fix is merged
to `main`.

Use these commands to inspect state:

```powershell
git status --short --branch
git rev-parse HEAD
git rev-parse origin/main
git ls-remote --tags origin "refs/tags/v0.6.0-alpha*"

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
