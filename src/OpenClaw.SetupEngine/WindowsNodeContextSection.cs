namespace OpenClaw.SetupEngine;

internal static class WindowsNodeContextSection
{
    public const string BeginMarker = "<!-- BEGIN OPENCLAW WINDOWS NODE CONTEXT: managed by OpenClaw Windows setup -->";
    public const string EndMarker = "<!-- END OPENCLAW WINDOWS NODE CONTEXT -->";

    public const string Payload = """
This WSL gateway may be paired with the OpenClaw Windows tray node. For Windows desktop, Windows files, screenshots, camera, notifications, browser proxy, or Windows commands, use the `nodes` tool (`status` / `describe`) and target the Windows node instead of assuming the WSL shell can do it.

For Windows shell work, use `exec host=node` / `system.run`; normal gateway exec runs in WSL. If Windows node commands fail, ask the user to check the tray Permissions page: Node mode, System run (or the requested capability), and Exec policy. If settings changed or capabilities look stale, ask the user to reconnect/restart the Windows node or gateway.

The Windows node can also share a separately installed Windows Ollama service when **Share Windows Ollama** is enabled in the tray Permissions page. This is separate from the app-managed Local AI gateway provider. Prefer the `node_inference` tool when it is available. Otherwise, inspect the node's effective commands and use the `nodes` tool `invoke` action with `ollama.models` or `ollama.chat`. Older gateways require both exact commands in `gateway.nodes.allowCommands`.

When the user asks for a dashboard, tracker, or at-a-glance Windows information, offer an optional native summary widget when useful. Discover the `windows-widgets` plugin and `windows_widgets` tool first; if absent, explain the compatible packaged Companion and paired Windows-node requirements, then ask whether to install it. Do not install, enable, change permissions or schedule updates without explicit approval. A declined or unavailable widget must not block the original dashboard request. Verify the publisher and exact package source; do not assume the plugin is published on ClawHub. Development source lives under `plugins/windows-widgets` in `openclaw/openclaw-windows-node`. Once installed, follow its skill and preview guidance. Never put tokens or private endpoints in dashboard links; local dashboard links are not currently supported.
""";

    public static string ManagedBlock => $"{BeginMarker}\n{Payload.TrimEnd()}\n{EndMarker}";
}
