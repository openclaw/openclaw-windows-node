# Control UI theme recordings

Each zip records what one real packaged OpenClaw Gateway served and how its own dashboard painted every built-in theme.
Tests use them to resolve built-in palettes offline exactly as the Companion does against that Gateway
(`ServedThemeStylesheetReader` + `ThemeProbe.js`) and compare the result with the dashboard's own rendering.

| Recording | Gateway | Why |
|---|---|---|
| `gateway-2026.11.4.zip` | npm `openclaw@2026.11.4` | Current stable release |
| `gateway-2026.9.7.zip` | npm `openclaw@2026.9.7` | Older release: no built-in branding on the wire |
| `gateway-2026.11.5-beta.4.zip` | npm `openclaw@2026.11.5-beta.4` | Newer build whose surfaces differ from the bundled table, and whose core stylesheet was renamed |

Each zip holds:

- `index.html`, `assets/*.css` and `themes/*.css`: the Control UI files the Gateway served, unmodified. They are the
  OpenClaw project's MIT-licensed build output.
- `dashboard.json`: the Gateway's build id, the recording time, the hash of the probe that recorded it, and for each
  built-in theme and mode the tokens `ThemeProbe.js` read inside the real dashboard page after the dashboard applied that
  theme, plus the background color the page actually painted.

## Recording again

Run a packaged Gateway with the Control UI enabled (on a Crabbox guest, never a personal install), then from the
repository root:

```powershell
.\scripts\Record-ControlUiThemeFixture.ps1 -GatewayUrl http://<host>:<port>/ -Name gateway-<version>
```

The script needs Microsoft Edge. It loads the dashboard in a headless Edge, selects each built-in theme through the
dashboard's own saved settings, and writes the zip here. Changing `ThemeProbe.js` changes its hash; record the fixtures
again so the dashboard values come from the probe under test.
