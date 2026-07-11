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
wsl.exe -d OpenClawGateway --user openclaw -- tailscale serve status --json

# Check the OpenClaw gateway itself; it remains loopback-bound inside WSL.
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "systemctl --user status openclaw-gateway.service --no-pager || true"
```

The generated WSL distro is Ubuntu 24.04 (noble). Setup installs Tailscale from its signed stable APT repository rather than executing the mutable `install.sh` bootstrap script as root.

The setup trusts Tailscale identity headers for this tailnet-only gateway (`gateway.auth.allowTailscale=true`). Tailnet ACLs therefore control who may reach this authentication path; token and device credentials remain available for Companion pairing and compatibility. Do not enable Funnel for this generated gateway: this workflow supports private tailnet Serve only.

## Use root instead of sudo

There is no interactive sudo password prompt. Open a root shell only when you intentionally need to inspect or change protected files:

```powershell
# Opens an interactive root shell for intentional protected-file work.
wsl.exe -d OpenClawGateway --user root -- bash
```

Keep gateway-owned files under `/home/openclaw/.openclaw`, `/var/lib/openclaw`, `/var/log/openclaw`, and `/opt/openclaw` owned by `openclaw:openclaw`.
