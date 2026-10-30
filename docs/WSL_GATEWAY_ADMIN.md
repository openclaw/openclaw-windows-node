# Managing the locked-down WSL gateway

Local setup creates an app-owned WSL distro named `OpenClawGateway` by default. It is not a general-purpose Ubuntu profile: the gateway runs as the `openclaw` Linux user, Windows interop is disabled inside WSL, Windows drive automounts are disabled inside WSL, and there is no password-based `sudo` flow.

Use these rules:

- Run normal gateway and config commands as `openclaw`.
- Run sudo-style protected-file commands as `root` from Windows with `wsl.exe --user root`.
- Use double quotes around `bash -lc "..."`; single quotes do not work from Command Prompt.
- Do not edit through the Windows WSL share for this locked-down config; use a WSL shell as `openclaw` instead.
- Do not edit the VHD or `%LOCALAPPDATA%\OpenClawTray\wsl` storage directly.

If your setup used a custom distro name, replace `OpenClawGateway` in the examples below.

## Check the managed distro

```powershell
# Lists WSL distros and shows whether OpenClawGateway is running.
wsl.exe --list --verbose

# Opens a one-shot shell as the gateway user and prints the user plus current directory.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "whoami && pwd"

# Opens a one-shot shell as root and prints root identity details.
wsl.exe -d OpenClawGateway --user root -- bash -lc "whoami && id"
```

## Update `openclaw.json`

Most gateway configuration lives in:

```text
/home/openclaw/.openclaw/openclaw.json
```

Because the distro is locked down, edit this file from inside WSL as the `openclaw` user instead of editing it through the Windows filesystem share. The edit commands in this section intentionally change files; the inspection commands in the rest of this guide are read-only.

1. Open a shell as the gateway user:

   ```powershell
   # Opens an interactive shell as the gateway user.
   wsl.exe -d OpenClawGateway --user openclaw -- bash
   ```

2. In that shell, back up and edit the config:

   ```bash
   # Moves into the OpenClaw config directory.
   cd /home/openclaw/.openclaw

   # Creates a backup copy before editing.
   cp openclaw.json openclaw.json.bak

   # Opens openclaw.json in the nano editor.
   nano openclaw.json

   # Verifies that openclaw.json is still valid JSON.
   python3 -m json.tool openclaw.json > /dev/null
   ```

   If `nano` is not available, use `vi openclaw.json`.

3. If the change does not take effect, reconnect or restart the gateway from OpenClaw Companion.

Read-only checks for the config:

```powershell
# Shows ownership, permissions, size, and timestamp for openclaw.json.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "ls -l /home/openclaw/.openclaw/openclaw.json"

# Verifies that openclaw.json is valid JSON without printing it.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "python3 -m json.tool /home/openclaw/.openclaw/openclaw.json > /dev/null"

# Prints only the top-level config keys, not secret values.
wsl.exe -d OpenClawGateway --user openclaw -- python3 -c "import json; data=json.load(open('/home/openclaw/.openclaw/openclaw.json')); print('\n'.join(sorted(data.keys())))"
```

Do not paste or share the full `openclaw.json` file. It can contain gateway tokens, private endpoints, provider settings, or other secrets; redact those values before sharing diagnostics.

## Inspect gateway state

Run gateway service checks as `openclaw`:

```powershell
# Shows the user service status without paging; succeeds even if the service is inactive.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "systemctl --user status openclaw-gateway.service --no-pager || true"

# Shows the most recent gateway service journal entries without paging.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "journalctl --user-unit openclaw-gateway.service --no-pager -n 80 || true"

# Shows ownership and permissions for the app-owned gateway directories.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "ls -ld /home/openclaw/.openclaw /var/lib/openclaw /var/log/openclaw /opt/openclaw"
```

Do not run `systemctl --user` as `root`; that checks root's user service manager, not the gateway's service.

## Inspect an optional Tailscale Serve endpoint

When the setup review enabled **Tailnet access with Tailscale Serve**, the generated distro runs its own Tailscale daemon. The Windows Companion intentionally uses the generated `wss://<node>.<tailnet>.ts.net` endpoint; it does not silently fall back to localhost.

Windows must also have Tailscale installed and signed in to the same tailnet. These checks do not print credentials:

```powershell
# Windows Companion side: confirm this PC is connected to Tailscale.
& "$env:ProgramFiles\Tailscale\tailscale.exe" status --json

# WSL side: confirm the daemon is connected and has a MagicDNS name.
wsl.exe -d OpenClawGateway --user root -- tailscale status --json

# Confirm Serve routes tailnet HTTPS to the loopback OpenClaw gateway port.
# Tailscale lifecycle and Serve are intentionally owned by root, not openclaw.
wsl.exe -d OpenClawGateway --user root -- /usr/bin/tailscale serve status --json

# Funnel is unsupported. This must show no public route.
wsl.exe -d OpenClawGateway --user root -- /usr/bin/tailscale funnel status --json

# Check the OpenClaw gateway itself; it remains loopback-bound inside WSL.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "systemctl --user status openclaw-gateway.service --no-pager || true"
```

The generated WSL distro is Ubuntu 24.04 (noble). Tailscale Serve setup requires that exact generated distro and rejects another `BaseDistro` before replacing an existing gateway. Setup installs Tailscale from its signed stable APT repository rather than executing the mutable `install.sh` bootstrap script as root.

Tailscale Serve preserves Companion token and device authentication by default (`gateway.auth.allowTailscale=false`). Setup runs `tailscale up`, Serve, reset, logout, and daemon management as root; the `openclaw` account is never made a Tailscale operator. Tailscale's LocalAPI permits read-only operations, such as identity lookup, to local unprivileged clients while reserving mutation for root or an explicitly configured operator. In the setup review, **Trust Tailscale identities for gateway authentication** is an explicit opt-in that writes the auth setting and records consent. Consent alone does not establish working identity authentication: the setup-generated background Serve route is not Core-managed identity ingress. Token and device credentials remain available for Companion pairing and compatibility. Do not enable Funnel for this generated gateway: this workflow supports private tailnet Serve only.

### Upgrade dashboard authentication on an existing gateway

In Connection, select the managed Tailscale gateway, connect with
`operator.admin`, then choose **Use Tailscale identity for dashboard** from its
saved-row menu and confirm. This auth-only action requires
`gateway.tailscale.mode="serve"` and verified Core-owned foreground Serve ingress.
It reads configuration and checks ingress before changing either Core or local
trust. Missing or unavailable ingress returns a not-ready message.

The default setup topology has `gateway.tailscale.mode="off"` and root-owned
`tailscale serve --bg`. It is unsupported by this action, even if Serve forwards
HTTPS successfully. Ask the gateway administrator to review the installed Core
version and deployment. The action does not migrate the distro, switch Serve
ownership, restart services, or change token/device credentials. Do not toggle
the mode alone and assume that proves compatible ingress.

On supported ingress, the action sends only
`{"gateway":{"auth":{"allowTailscale":true}}}` with the fresh Core config hash.
A new local `TrustTailscaleAuth` marker is saved only after Core confirms patch
acceptance. The success message reports acceptance, not completed asynchronous
application. Dashboard links separately verify matching applied configuration
and live ingress before omitting the browser token.

A rejected, timed-out, lost, or canceled response does not create new trust.
Reconnect and repeat the same action; it remains available after registry reload,
and its minimal patch is idempotent even if Core accepted the earlier request.
Cancellation during dispatch waits for the bounded request to finish before
returning, without saving new trust. A local save failure restores the in-memory
marker and leaves the same retry available. Existing true consent is preserved
on ambiguous failures; only authoritative applied config revocation clears it.
When live identity cannot be verified, links use an independently approved shared
browser credential or fail closed if none is available.

## Use root instead of sudo

There is no interactive sudo password prompt. Open a root shell only when you intentionally need to inspect or change protected files:

```powershell
# Opens an interactive root shell for intentional protected-file work.
wsl.exe -d OpenClawGateway --user root -- bash
```

Keep gateway-owned files under `/home/openclaw/.openclaw`, `/var/lib/openclaw`, `/var/log/openclaw`, and `/opt/openclaw` owned by `openclaw:openclaw`.
