# OpenClaw Gateway MSIX

This subtree builds a Windows MSIX package containing:

- a .NET 10 NativeAOT bootstrapper exposed as the `openclaw` app execution
  alias;
- a pinned, verified build of
  [`openclaw/openclaw`](https://github.com/openclaw/openclaw);
- the matching official Node.js runtime for x64 or ARM64.

The package is independent from the OpenClaw Companion application and uses a
separate `OpenClaw.Gateway` package identity. Both packages use the OpenClaw
Foundation publisher metadata already established by this repository.

## Bootstrap behavior

Launching `openclaw` without arguments prepares the bundled gateway under
`%USERPROFILE%\.openclaw-msix\app`. It does not automatically start the
gateway. After preparation, use:

```powershell
openclaw setup --classic --mode local --no-install-daemon
openclaw gateway run
```

On later launches, the bootstrapper offers these choices:

- **C**: continue with fast verification (recommended);
- **R**: fully verify every prepared file and repair if needed;
- **G**: remove the prepared gateway files while preserving OpenClaw user
  configuration and state;
- **A**: remove both the prepared gateway and `%USERPROFILE%\.openclaw`.

The destructive full reset requires typing `RESET`. Cleanup first asks
OpenClaw to stop its gateway. If necessary, it terminates only the recorded
packaged gateway process, never every `node.exe` process.

All explicit arguments are forwarded unchanged to the bundled OpenClaw CLI
after payload preparation. The host does not interpret, reject, or rewrite
OpenClaw commands or options.

Every OpenClaw child process runs with
`OPENCLAW_SUPERVISOR_MODE=external` and `OPENCLAW_NO_AUTO_UPDATE=1`. This makes
the MSIX package the authoritative owner of Gateway code updates without
shadowing OpenClaw commands. OpenClaw itself is responsible for enforcing
those environment flags.

## Selecting the OpenClaw revision

`.github\workflows\gateway-msix.yml` resolves an explicit OpenClaw ref before
building. Pull-request and `main` push runs use the pinned commit configured in
both:

- `workflow_dispatch.inputs.openclaw_ref.default`;
- the non-manual fallback in `env.OPENCLAW_REF`.

Changing only the workflow-dispatch default does not change automatic builds.
For a one-time override, run **Build OpenClaw Gateway MSIX** manually and
provide a tag, branch, or preferably a full 40-character commit SHA in
`openclaw_ref`.

The workflow records the requested ref and resolved upstream commit in
`payload-metadata.json`. `msix-metadata.json` separately records both the
packaging repository commit and bundled OpenClaw commit.

`release-policy.json` records the immutable OpenClaw commit approved for
official signing. Updating that policy requires a reviewed repository change.
Official signing runs only from `main` and verifies the workflow input, both
architecture metadata files, both MSIX hashes, the embedded manifests, and
the embedded payload metadata and hashes before requesting Azure credentials.

## Build and test

```powershell
dotnet restore .\packaging\gateway-msix\OpenClaw.Gateway.MSIX.slnx
dotnet test .\packaging\gateway-msix\OpenClaw.Gateway.MSIX.slnx `
  --configuration Release `
  --no-restore
```

`scripts\Build-Payload.ps1` turns an OpenClaw npm package into an
architecture-specific payload. `scripts\Build-MSIX.ps1` verifies that payload,
downloads and verifies the official Node.js runtime, then creates an unsigned
NativeAOT MSIX. `scripts\Build-LocalMSIX.ps1` can reuse a successful workflow
payload or a local payload directory.

Normal pull-request and push workflows publish unsigned packages for
validation. Manual runs support three signing modes:

- `unsigned` accepts any OpenClaw branch, tag, or commit and publishes unsigned
  MSIX packages;
- `test` accepts any OpenClaw ref and publishes MSIX packages signed with a
  temporary self-signed certificate plus the public `.cer` needed for local
  installation;
- `official` requires the approved immutable commit from
  `release-policy.json` and may run only from `main`.

Official signing uses the protected `release-signing` environment, Azure OIDC,
and the existing OpenClaw Artifact Signing account and certificate profile.
Test-signing private keys are generated only on the temporary GitHub runner
and are deleted before artifacts are uploaded. No signing secret or private
key is stored in the repository.

## Installed data

| Data | Default path |
|---|---|
| Prepared gateway files | `%USERPROFILE%\.openclaw-msix\app` |
| OpenClaw configuration and user state | `%USERPROFILE%\.openclaw` |
| Bootstrap diagnostics | `%LOCALAPPDATA%\Packages\<package-family>\LocalState\OpenClawGatewayMSIX\Logs\openclaw.log` |

The prepared gateway and OpenClaw user state are outside the immutable MSIX
installation directory. Updating or removing the MSIX does not automatically
delete those directories.
