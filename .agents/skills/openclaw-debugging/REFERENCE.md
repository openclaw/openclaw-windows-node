# OpenClaw debugging reference

Read this only when the [debugging loop](SKILL.md) needs a file location, a
verbose trace, or a native capability probe.

## File locations

For the normal unpackaged Release identity:

| Evidence | Default location |
|---|---|
| Tray log | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` |
| Tray archive | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log.old` |
| Crash log | `%LOCALAPPDATA%\OpenClawTray\crash.log` |
| Structured events | `%LOCALAPPDATA%\OpenClawTray\Logs\diagnostics.jsonl` |
| Setup logs | `%APPDATA%\OpenClawTray\Logs\Setup\setup-engine-*.jsonl` |
| Setup journal | Beside its setup log as `*.journal.jsonl` |
| Native setup log | `%APPDATA%\OpenClawTray\Logs\Setup\native-setup-*.jsonl` |

Dev uses `OpenClawTray-Dev`. An isolated run puts these artifacts under the
selected direct profile. For packaged storage, resolve the active profile
through the app instead of hard-coding the package publisher suffix.

## Verbose protocol trace

Set `OPENCLAW_TRAY_TRACE=1` only for a bounded reproduction that needs verbose
Gateway protocol traces. Restart the selected app with the same profile, then
unset the variable. A trace does not replace `app.connection.status` or a real
invocation.

## Native capability probes

Run only probes whose side effects and permission prompts are authorized. Bind
the client as in [windows-app-mcp](../windows-app-mcp/SKILL.md#bind-the-client-to-one-app)
and keep the binding lines in the same block. Exact schemas are in
[winnode](../winnode/SKILL.md).

Start with discovery and status commands:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --list-tools
winnode --mcp-url $mcpUrl --command camera.list --params '{}'
winnode --mcp-url $mcpUrl --command stt.status --params '{}'
winnode --mcp-url $mcpUrl --command tts.status --params '{}'
winnode --mcp-url $mcpUrl --command system.which --params '{"bins":["git","powershell"]}'
```

Sensitive capture needs an authorized non-isolated Release or Dev identity.
Redirect each full payload to a private artifact so it never prints into the
transcript:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --command screen.snapshot --params '{"format":"png","maxWidth":1280}' > '<private-screen-result.json>'
winnode --mcp-url $mcpUrl --command camera.snap --params '{"format":"jpeg","maxWidth":1280}' > '<private-camera-result.json>'
winnode --mcp-url $mcpUrl --command stt.transcribe --params '{"maxDurationMs":5000,"language":"auto"}' > '<private-stt-result.json>'
```

Check `$LASTEXITCODE` after each call. Report only the non-sensitive metadata
the debug claim needs.

Non-capture presentation probes, when authorized:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --command tts.speak --params '{"text":"OpenClaw diagnostic probe"}'
winnode --mcp-url $mcpUrl --command system.notify --params '{"body":"OpenClaw diagnostic probe","sound":false}'
```

## Read probe results by boundary

- Absence from `tools/list` usually means a disabled capability or
  registration gate, not a Windows API failure.
- Screen, camera, and location need remembered app consent and a visible
  per-use indication before sensor access.
- A blocked camera needs Windows camera access for desktop apps. Packaged and
  unpackaged identities can have different permission state.
- Microphone capture and camera clips with audio need Windows microphone
  permission and an input device.
- TTS status separates configuration and readiness from playback.
- Notification proof needs a visible toast from the tested identity. Isolated
  and fixture identities suppress toasts by design.
- A base64 payload proves the capability returned, not image or audio quality.
