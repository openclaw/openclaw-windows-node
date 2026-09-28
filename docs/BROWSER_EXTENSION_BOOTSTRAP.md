# Windows Chrome extension bootstrap

The Windows native host is `ai.openclaw.browser_bootstrap`. It admits only the
Foundation extension `kcdjddhmeafeomebliikmbpblkmkfoig`. Chrome still owns installation
and permission approval. This implementation does not force enterprise policy,
change Chrome profile preferences, or replace an extension's saved pairing.

## Current scope

Automatic pairing is supported for a running release Companion connected to its
explicitly setup-managed local WSL gateway. Browser control must be enabled and
the connection manager must still permit automatic connection intent. An explicit
Disconnect/Stop, gateway switch, disabled Browser control, unverified listener,
or stopped Companion prevents credential delivery. Dev and isolated instances
do not claim the production Chrome host.

Remote/SSH gateways, legacy records identified only by a friendly-name convention,
and custom WSL users are not automatically adopted. The native host returns
`manual_required` for unsupported topology and `pairing_unavailable` for unavailable
local runtime. No relay process is started by the Windows helper. The existing
`ensure_relay` operation is recognized but returns `manual_required` in this lane.

The per-user installer registers the native executable before starting the tray.
After successful native registration, the selected installer task creates an owned
HKCU external-extension request with the official Chrome Store update URL. Chrome
still owns download and permission approval. A saved Browser control opt-out blocks
a new request. The installer also remembers a declined task across upgrades; it does
not rewrite Chrome removal or disconnect state.
The tray startup path registers only the helper, so it cannot undo the installer
opt-out by re-requesting the extension. A registry entry or manifest belonging to
another installation is preserved. Moving the executable, including a versioned
MSIX path change, currently requires explicit removal of its old owned native
registration before registering the new installation. This is not silently
resolved by overwriting a foreign path. Packaged MSIX registry visibility and
upgrade behavior require real Windows proof before claiming MSIX parity.

## Ownership and call graph

1. Chrome launches an immutable generation copy of the packaged
   `tools/browser-bootstrap/OpenClaw.BrowserBootstrap.exe`. `NativeTransport` and
   `BrowserNativeProtocol` validates the exact origin, strict v1 request schema,
   canonical 16-32-byte nonce, UTF-8, and four-byte little-endian framing.
   Requests are capped at 4 KiB. Raw standard streams are binary, not text writers.
2. `RegistrationService` delegates generation admission and registry mutation to
   `WindowsRegistrationPlatform` and `GenerationStore`. The four immutable
   generation files, current SID, ACLs, hashes, exact runtime and origin must
   validate. Both registry views and machine/user locations are inspected;
   foreign or conflicting registrations are preserved. Installer, Companion and
   canonical CLI use this same bounded management owner.
3. For `companion-managed-wsl`, the executable sends one bounded request through
   the Windows current-user-only named pipe `OpenClawTray.BrowserBootstrap.v1`.
   Both endpoints use `PipeOptions.CurrentUserOnly`. Explicit
   `native-windows-cli` bindings instead invoke their admitted Windows Node/CLI;
   neither backend falls back to the other. The helper does not read saved
   gateway tokens, settings credentials, or a copied bearer secret. Same-user
   arbitrary code is outside this boundary, as with Chrome native messaging.
4. `BrowserBootstrapHost` composes `GatewayRegistry`,
   `IGatewayConnectionManager`, `SettingsManager` and
   `ManagedLocalGatewayPortProvenanceService`. `BrowserBootstrapService` pins the
   active record, verifies ownership before and after CLI execution, and discards
   stale results after state/registry/settings generations change.
5. `BrowserBootstrapWslCommand` invokes the system `wsl.exe` in that exact distro,
   with a script over stdin to `/bin/bash --noprofile --norc -s`. It verifies the
   default WSL user is `openclaw` and selects only the installer's known CLI
   locations: `/home/openclaw/.openclaw/bin/openclaw`, `/opt/openclaw/bin/openclaw`,
   then `/usr/local/bin/openclaw`. It clears inherited CLI profile/state/config and
   Node overrides. It checks the running default systemd service's profile/state/config
   environment against the default installed user profile and refuses custom paths.
   stdout/stderr are bounded, secret-bearing stdout remains only
   in memory, and neither is passed through SetupEngine's command-output logger.
   A request-owned transient user-systemd unit uses READY/PERMIT/RESULT/SETTLED
   framing and stdin revocation. Positive guest settlement plus Windows process
   and drain joins are required before ownership release. The request deadline
   remains 15 seconds and soft cleanup budget 2 seconds. Lost acknowledgment is
   UNKNOWN/BUSY, not permission to retire; Windows exit alone is not settlement.
6. The canonical TypeScript CLI owns relay-token generation and pairing encoding.
   Windows validates local topology, gateway port, loopback route and gateway hint
   before returning the nonce-bound native response.

Existing runtime facts behind that boundary:

- `InstallCliStep.ExecuteAsync` installs and verifies the managed Linux CLI and
  its Node runtime; `EnsureCliOnDefaultPathAsync` supplies a compatibility symlink.
- `WslGatewayControlCommandBuilder` uses the same known CLI locations and stdin
  scripts for start/stop/restart. Bootstrap does not call those lifecycle actions.
- `BrowserProxyActivation` and `NodeService` register `BrowserProxyCapability`,
  which forwards HTTP to a browser-control endpoint. `BrowserProxyTunnelState`
  can forward the remote control endpoint over SSH. That is not a local extension
  relay or bundled Windows Node worker, so it cannot supply a Windows-local relay
  credential. This patch leaves that separate HTTP/shared-token contract unchanged.

## Cross-repository CLI contract

Required command in the managed WSL install:

```text
openclaw browser extension pair --json --local-gateway
```

Successful stdout must be exactly one JSON object, with no progress prose:

```json
{
  "remote": false,
  "relayPort": 18792,
  "pairingString": "ws://127.0.0.1:18789/browser/extension?gateway=ws%3A%2F%2F127.0.0.1%3A18789#<relay-token>"
}
```

The flag uses the canonical `buildBrowserExtensionPairing` with
`localTransport: "gateway"`. It must reject remote-mode/TLS configurations, preserve
browser opt-out, and never reinterpret a gateway authentication token as a relay token.
The gateway port must come from that same CLI profile. The Windows active record
port must match. Old CLI versions fail closed; Windows does not scrape human
output or fall back to reading another profile's secrets.

## Validation and proof gates

Baseline remains `./build.ps1`, full Shared tests and full Tray tests. Add:

```powershell
dotnet test tests/OpenClaw.Shared.Tests --filter "FullyQualifiedName~BrowserNativeProtocolTests|FullyQualifiedName~BrowserBootstrapPipeTests"
dotnet test tests/OpenClaw.Connection.Tests --filter FullyQualifiedName~BrowserBootstrapServiceTests
dotnet test tests/OpenClaw.Tray.Tests --filter FullyQualifiedName~BrowserBootstrapIntegrationContractTests
./scripts/Test-BrowserNativeHost.ps1 -ExecutablePath publish/tools/browser-bootstrap/OpenClaw.BrowserBootstrap.exe
```

The executable proof runs in the x64 and ARM64 publish jobs. It refuses existing
host registrations, uses synthetic pairing material, validates binary framing,
exact-origin rejection, oversize rejection, authenticated pipe handoff, owned
cleanup, native-first HKCU Store requests and preservation of foreign native and
external-extension registry entries. It is not a Chrome/WSL E2E
proof and must not be represented as one.

Required real behavior proof still needs a disposable Windows installation with
the matching TS CLI, official Chrome extension, and explicit Chrome permission
approval. Exercise first pairing, existing extension pairing, extension disconnect,
Browser-control disabled, gateway Disconnect/Stop, gateway switch during bootstrap,
foreign native registration, normal upgrade, uninstall and ARM64 launch. Never
publish pairing strings, raw native responses, gateway records or token files.
Relevant proof pools are `windows-clean-installer-upgrade`,
`windows-wsl-gateway-e2e`, `windows-11-arm64` and `windows-winui-interactive`.
