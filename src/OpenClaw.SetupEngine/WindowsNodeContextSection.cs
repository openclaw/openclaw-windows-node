namespace OpenClaw.SetupEngine;

internal static class WindowsNodeContextSection
{
    public const string BeginMarker = "<!-- BEGIN OPENCLAW WINDOWS NODE CONTEXT: managed by OpenClaw Windows setup -->";
    public const string EndMarker = "<!-- END OPENCLAW WINDOWS NODE CONTEXT -->";

    public const string Payload = """
This WSL gateway may be paired with the OpenClaw Windows tray node. For Windows desktop, Windows files, screenshots, camera, notifications, browser proxy, or Windows commands, use the `nodes` tool (`status` / `describe`) and target the Windows node instead of assuming the WSL shell can do it.

For Windows shell work, use `exec host=node` / `system.run`; normal gateway exec runs in WSL. If Windows node commands fail, ask the user to check the tray Permissions page: Node mode, System run (or the requested capability), and Exec policy. If settings changed or capabilities look stale, ask the user to reconnect/restart the Windows node or gateway.

If the user wants permissive WSL gateway exec defaults, suggest:

```bash
openclaw config unset tools.exec.mode || true
openclaw config set tools.exec.ask off
openclaw config set tools.exec.host auto
openclaw config set tools.exec.security full
openclaw config get tools.exec
```

Notes: unset `tools.exec.mode` avoids a schema conflict; `ask off` disables gateway exec prompts; `host auto` keeps default routing; `security full` makes gateway exec permissive; `get tools.exec` verifies the result.
""";

public static string ManagedBlock => $"{BeginMarker}\n{Payload.TrimEnd()}\n{EndMarker}";
}
