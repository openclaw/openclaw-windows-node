# OpenClaw Gateway ↔ Windows Node Integration Guide

> Last updated: 2026-04-25
> Source of truth: [`openclaw/openclaw` — `src/gateway/node-command-policy.ts`](https://github.com/openclaw/openclaw/blob/main/src/gateway/node-command-policy.ts)

This document captures everything we've learned about how the OpenClaw gateway handles node commands, platform allowlists, and the QR bootstrap pairing flow. It exists because these details are not obvious from the docs alone and caused real debugging sessions.

---

## 1. The Gateway Command Allowlist System

Every command a node sends must pass **two** gates before it works:

1. **The node must declare it** — in the `commands` array of the `connect` handshake
2. **The gateway must allow it** — via a per-platform allowlist in `node-command-policy.ts`

If either gate fails, the command is silently dropped or rejected with:
```
node command not allowed: "X" is not in the allowlist for platform "Y"
```

### 1.1 Per-Platform Default Allowlists

The gateway has hardcoded defaults per platform (from `PLATFORM_DEFAULTS`):

| Platform | Default Commands |
|----------|-----------------|
| **macOS** | canvas.*, camera.list, location.get, device.info/status, contacts.search, calendar.events, reminders.list, photos.latest, motion.*, system.run/which/notify, screen.snapshot, browser.proxy |
| **iOS** | canvas.*, camera.list, location.get, device.info/status, contacts.*, calendar.*, reminders.*, photos.latest, motion.*, system.notify |
| **Android** | canvas.*, camera.list, location.get, notifications.*, device.*, contacts.*, calendar.*, callLog.search, reminders.*, photos.latest, motion.*, system.notify |
| **Windows** | **system.run, system.run.prepare, system.which, system.notify, browser.proxy** |
| **Linux** | system.run, system.run.prepare, system.which, system.notify, browser.proxy |
| **Unknown** | canvas.*, camera.list, location.get, system.notify |

**Windows and Linux get almost nothing by default** — only system commands. No canvas, no camera, no screen, no location. This is because Windows/Linux were originally designed as headless "node host" platforms (exec-only), not full companion apps like macOS/iOS.

### 1.2 "Dangerous" Commands (Always Need Explicit Opt-In)

These commands are **never** in any platform's defaults, regardless of platform:

```typescript
CAMERA_DANGEROUS_COMMANDS = ["camera.snap", "camera.clip"]
SCREEN_DANGEROUS_COMMANDS = ["screen.record"]
CONTACTS_DANGEROUS_COMMANDS = ["contacts.add"]
CALENDAR_DANGEROUS_COMMANDS = ["calendar.add"]
REMINDERS_DANGEROUS_COMMANDS = ["reminders.add"]
SMS_DANGEROUS_COMMANDS = ["sms.send", "sms.search"]
```

Even macOS doesn't get `camera.snap` or `camera.clip` by default! They must be added via `gateway.nodes.allowCommands`.

### 1.3 How to Enable Commands for Windows

Add ALL needed commands to `gateway.nodes.allowCommands` in `~/.openclaw/openclaw.json`:

```json5
{
  gateway: {
    nodes: {
      allowCommands: [
        // Canvas
        "canvas.present",
        "canvas.hide",
        "canvas.navigate",
        "canvas.eval",
        "canvas.snapshot",
        "canvas.a2ui.push",
        "canvas.a2ui.reset",
        // Camera (all are dangerous or not in Windows defaults)
        "camera.list",
        "camera.snap",
        "camera.clip",
        // Screen
        "screen.snapshot",
        "screen.record",
        // Location
        "location.get",
        // System (already in Windows defaults, but listed for completeness)
        // "system.run",
        // "system.run.prepare",
        // "system.which",
        // "system.notify",
        // Exec approvals
        "system.execApprovals.get",
        "system.execApprovals.set",
      ]
    }
  }
}
```

After changing config:
```bash
openclaw gateway restart
```

After changing the node's command list (code change), you must **re-pair**:
```bash
openclaw devices list          # find old device
openclaw devices reject <id>   # reject the old pairing
# Node will auto-reconnect and create a new pairing request
openclaw devices list          # find new request
openclaw devices approve <id>  # approve with updated commands
```

### 1.4 Why Re-Pairing is Needed

The gateway snapshots the node's declared `commands` array at **pairing approval time**. If you change the node's code to add new commands and restart it, the gateway still uses the old snapshot. You must reject the old pairing and approve a new one.

### 1.5 `denyCommands`

You can also explicitly deny commands:
```json5
{ gateway: { nodes: { denyCommands: ["system.run"] } } }
```
`denyCommands` wins over `allowCommands`.

---

## 2. Command Name Mismatches (Bugs We Found)

### 2.1 `screen.capture` → Should Be `screen.snapshot`

The Windows node previously registered `screen.capture` as a command name. The gateway calls it **`screen.snapshot`**:

```typescript
// Gateway source (node-command-policy.ts)
const SCREEN_COMMANDS = ["screen.snapshot"];
```

The macOS node uses `screen.snapshot`. `screen.capture` is not recognized by the gateway at all — it's silently filtered out of the declared commands.

**Fixed locally**: `ScreenCapability.cs` now advertises and handles `screen.snapshot`.

### 2.2 `screen.list` — Not a Gateway Command

Our node previously registered `screen.list`. This command does not exist in the gateway's command policy. It's never in any default allowlist.

**Fixed locally**: `screen.list` is no longer advertised.

### 2.3 Verified Correct Names

| Our Command | Gateway Canonical | Status |
|-------------|-------------------|--------|
| `camera.list` | `camera.list` | ✅ Match |
| `camera.snap` | `camera.snap` | ✅ Match (dangerous) |
| `camera.clip` | `camera.clip` | ✅ Match (dangerous) |
| `screen.snapshot` | `screen.snapshot` | ✅ Match |
| `location.get` | `location.get` | ✅ Match |
| `system.notify` | `system.notify` | ✅ Match |
| `system.run` | `system.run` | ✅ Match |
| `system.run.prepare` | `system.run.prepare` | ✅ Match |
| `system.which` | `system.which` | ✅ Match |
| `canvas.present` | `canvas.present` | ✅ Match |
| `canvas.hide` | `canvas.hide` | ✅ Match |
| `canvas.navigate` | `canvas.navigate` | ✅ Match |
| `canvas.eval` | `canvas.eval` | ✅ Match |
| `canvas.snapshot` | `canvas.snapshot` | ✅ Match |
| `canvas.a2ui.push` | `canvas.a2ui.push` | ✅ Match |
| `canvas.a2ui.reset` | `canvas.a2ui.reset` | ✅ Match |

### 2.4 Commands We're Missing vs macOS

| Command | macOS | Windows | Notes |
|---------|-------|---------|-------|
| `screen.record` | ✅ | ❌ | Video recording (PR #159 pending) |
| `canvas.a2ui.pushJSONL` | ✅ (in gateway allowlist) | ❌ | Not widely used |
| `device.info` | ✅ | ❌ | Hardware/OS info |
| `device.status` | ✅ | ❌ | Battery/charging status |
| `browser.proxy` | ✅ | ❌ | Chrome DevTools proxy |

---

## 3. Platform Detection

The gateway detects platform from two fields in the `connect` handshake:

```typescript
// Our connect payload
client: {
  platform: "windows",    // ← Primary signal
  mode: "node",
}
```

Detection logic (from `node-command-policy.ts`):
1. Normalize `platform` → lowercase
2. Match against prefix rules: `"win"` → windows, `"mac"/"darwin"` → macos, etc.
3. If no match, try `deviceFamily` field
4. If still no match → `"unknown"` (gets conservative defaults)

Our node sends `platform: "windows"` which correctly matches the `windows` prefix rule.

**The problem isn't detection — it's that the `windows` platform intentionally gets a minimal allowlist.** The gateway team designed Windows as a headless exec host, not a full companion app with camera/canvas/screen.

### 3.1 What "Unknown" Gets (and Why It's Actually Better)

Ironically, the `unknown` platform gets MORE than Windows:
```typescript
unknown: [
  ...CANVAS_COMMANDS,
  ...CAMERA_COMMANDS,     // camera.list
  ...LOCATION_COMMANDS,   // location.get
  NODE_SYSTEM_NOTIFY_COMMAND,
]
```

If we sent `platform: "windows-desktop"` (which wouldn't match any prefix rule), we'd fall through to `unknown` and actually get canvas/camera/location defaults. But that would be a hack — the right fix is `gateway.nodes.allowCommands`.

---

## 4. The QR / Bootstrap Token Flow

### 4.1 What `openclaw qr` Does

1. Calls `issueDeviceBootstrapToken()` on the gateway
2. Generates a **short-lived, single-use** `bootstrapToken`
3. Encodes `{ url, bootstrapToken, expiresAtMs }` as base64url
4. Renders as QR code or pasteable setup code

### 4.2 bootstrapToken vs gateway.auth.token

| | `bootstrapToken` | `gateway.auth.token` |
|---|---|---|
| **Purpose** | Initial device pairing | Shared-secret auth for operators |
| **Lifetime** | Short-lived, single-use | Permanent until changed |
| **Scope** | Node pairing + bounded operator bootstrap | Full operator access |
| **Generated by** | `openclaw qr` / `/pair` | User config in `openclaw.json` |
| **Auto-approval** | Yes — gateway auto-approves bootstrap-token handshakes | No — manual `devices approve` needed |

### 4.3 The Auth Cascade (How the Gateway Resolves Auth)

When a node connects with `auth: { token: "...", bootstrapToken: "..." }`, the gateway tries (from `auth-context.ts`):

1. **Shared-secret auth** — `auth.token` vs `gateway.auth.token/password`
2. **Bootstrap token** — `auth.bootstrapToken` vs issued bootstrap tokens
   - If valid: `authMethod = "bootstrap-token"`, auto-approved!
   - Preferred over shared-secret even if both succeed (QR flow relies on this)
3. **Device token** — `auth.token` as device-token fallback (for already-paired devices)

### 4.4 What Our Setup Wizard Does (and the Gap)

Currently, our Setup Wizard:
1. Decodes the setup code from `openclaw qr`
2. Extracts `url` and `bootstrapToken`
3. Stores `bootstrapToken` as the settings `Token` field
4. Sends it as `auth.token` in the connect handshake

**The problem**: We send it as `auth.token`, not `auth.bootstrapToken`. The gateway's auth resolution:
- Tries `auth.token` as shared-secret → **fails** (it's not the gateway token)
- Never sees `auth.bootstrapToken` → never tries bootstrap-token auth
- Falls back to device-token → **fails** (no prior pairing)

**The fix**: Send the bootstrap token as `auth.bootstrapToken` in the connect payload, separate from `auth.token`. This lets the gateway correctly classify it as a bootstrap-token handshake, which enables:
- Silent auto-approval (no manual `devices approve` needed)
- Bootstrap token revocation after pairing
- Bounded operator token handoff (if configured)

### 4.5 Post-Pairing: Device Tokens

After a successful bootstrap-token pairing:
1. Gateway issues a `deviceToken` in `hello-ok.auth.deviceToken`
2. Node should **save** this device token
3. Future connections use `auth.token = <deviceToken>` (device-token auth path)
4. The bootstrap token is revoked and no longer valid

**We're not doing step 2-3 yet.** Our node uses the same settings token forever. It works because the settings token matches the gateway's shared secret (if the user entered it manually), but it means QR-based pairing doesn't complete the handoff properly.

### 4.6 Ideal Bootstrap Flow (What We Should Implement)

```
1. User runs `openclaw qr` on gateway host
2. User pastes setup code into Windows Setup Wizard
3. Wizard decodes → { url, bootstrapToken, expiresAtMs }
4. Node connects with: auth: { bootstrapToken: "<token>" }
5. Gateway auto-approves pairing (bootstrap-token auth method)
6. Gateway returns hello-ok with: auth: { deviceToken: "<token>" }
7. Node saves deviceToken to identity store
8. Future connections use: auth: { token: "<deviceToken>" }
9. No manual `devices approve` needed!
```

This would make pairing truly seamless — scan QR, auto-paired, done.

---

## 5. Recommendations

### 5.0 Design Conclusion: Safe Windows/macOS Parity

The root issue is not that the gateway fails to recognize Windows. It recognizes Windows correctly. The problem is that `platform: "windows"` currently gets only the headless exec-host defaults, while the Windows tray app is now a full node that can declare canvas, camera, location, and screen capabilities.

The simplest upstream fix is to make Windows match macOS for **safe declared commands**, while keeping dangerous commands explicit opt-in.

This does **not** make every Windows node capable of camera/canvas/location/screen. A command still has to pass both gates:

1. The node must declare the command.
2. The gateway policy must allow the command.

So a headless Windows node host that only declares `system.run` / `system.which` remains exec-only. Expanding the Windows default allowlist just stops the gateway from filtering safe commands that a Windows node explicitly advertises.

Recommended gateway defaults:

| Command bucket | Windows default? | Reason |
|----------------|------------------|--------|
| Safe declared companion commands: `canvas.*`, `camera.list`, `location.get`, `screen.snapshot`, `device.info`, `device.status` | Yes | Matches macOS parity and only applies when declared by the node |
| Dangerous/privacy-heavy commands: `camera.snap`, `camera.clip`, `screen.record`, write commands like `contacts.add` | No | Existing gateway model already requires explicit `gateway.nodes.allowCommands` |
| Exec commands: `system.run`, `system.run.prepare`, `system.which`, `system.notify`, `browser.proxy` | Yes | Existing Windows headless-host behavior |

Until the gateway expands Windows safe defaults, the practical local solution is:

1. Keep declaring the correct command names from the Windows node.
2. Configure `gateway.nodes.allowCommands` for the Windows companion features.
3. Re-pair after command-list changes because the gateway snapshots commands at approval time.

### 5.1 Immediate Code Fixes (This Branch)

- [x] Rename `screen.capture` → `screen.snapshot` in `ScreenCapability.cs`
- [x] Remove `screen.list` from declared commands
- [ ] Remove debug logging from `WindowsNodeClient.cs` (done)

### 5.2 Setup Wizard Improvements (Next Sprint)

- [ ] Send `bootstrapToken` in correct field: `auth.bootstrapToken` not `auth.token`
- [ ] Handle `hello-ok.auth.deviceToken` — save it for future connections
- [ ] Show "auto-paired!" vs "waiting for approval" based on auth method
- [ ] Handle bootstrap token expiry gracefully (re-generate if expired)

### 5.3 Upstream Contributions / Issues to File

- [ ] **Request Windows/macOS parity for safe declared commands** — Windows should allow the same safe companion commands macOS does, while dangerous commands stay explicit opt-in.
- [ ] **Document `gateway.nodes.allowCommands`** — it's not in the config reference page
- [ ] **Consider `canvas.a2ui.pushJSONL`** — it's in the gateway allowlist but we don't implement it

#### Upstream issue draft

**Title:** Expand Windows node default allowlist for safe declared companion commands

**Body:**

Windows nodes are currently treated like Linux/headless exec hosts in `src/gateway/node-command-policy.ts`:

```ts
windows: [...SYSTEM_COMMANDS]
```

That means the gateway filters out safe companion-app commands that a Windows node explicitly declares, including `canvas.*`, `camera.list`, `location.get`, and `screen.snapshot`. The Windows tray app is now a full companion node, not just an exec host, so this causes confusing behavior: the node can implement and advertise a command, but the gateway drops/rejects it unless users manually configure `gateway.nodes.allowCommands`.

Proposal:

- Add safe declared companion commands to Windows defaults, similar to macOS:
  - `canvas.present`
  - `canvas.hide`
  - `canvas.navigate`
  - `canvas.eval`
  - `canvas.snapshot`
  - `canvas.a2ui.push`
  - `canvas.a2ui.pushJSONL`
  - `canvas.a2ui.reset`
  - `camera.list`
  - `location.get`
  - `screen.snapshot`
  - optionally `device.info` / `device.status`
- Keep dangerous/privacy-heavy commands explicit opt-in via `gateway.nodes.allowCommands`:
  - `camera.snap`
  - `camera.clip`
  - `screen.record`
  - write commands such as `contacts.add`, `calendar.add`, etc.

This does not grant capabilities to headless Windows hosts by itself. A command still has to pass both gates: the node must declare it in `commands`, and the gateway policy must allow it. Headless Windows node hosts that only declare `system.run` / `system.which` remain exec-only.

Related documentation gap: `gateway.nodes.allowCommands` and `gateway.nodes.denyCommands` should be documented in the gateway configuration reference, including the requirement to re-pair after command-list changes because approved pairing records snapshot declared commands.

### 5.4 User-Facing Documentation

When shipping the Windows node, README/wiki should tell users:

> **First-time setup**: After pairing your Windows node, add these commands to your gateway config:
> ```bash
> openclaw config set gateway.nodes.allowCommands '["canvas.present", "canvas.hide", "canvas.navigate", "canvas.eval", "canvas.snapshot", "canvas.a2ui.push", "canvas.a2ui.reset", "camera.list", "camera.snap", "camera.clip", "screen.snapshot", "location.get", "system.execApprovals.get", "system.execApprovals.set"]'
> openclaw gateway restart
> ```
> Then re-pair the node (`openclaw devices reject <old-id>` + re-approve).

---

## 6. Reference: Gateway Source Files

| File | What It Does |
|------|-------------|
| `src/gateway/node-command-policy.ts` | Platform allowlists, dangerous commands, command filtering |
| `src/gateway/device-metadata-normalization.ts` | Platform string normalization |
| `src/infra/node-commands.ts` | Constants: `system.run/which/notify`, `browser.proxy`, `execApprovals.*` |
| `src/gateway/server/ws-connection/auth-context.ts` | Auth cascade: shared-secret → bootstrap-token → device-token |
| `extensions/device-pair/index.ts` | QR generation, bootstrap token issuance, pairing flow |
| `src/cli/nodes-screen.ts` | CLI screen record helpers (confirms `screen.record` naming) |
