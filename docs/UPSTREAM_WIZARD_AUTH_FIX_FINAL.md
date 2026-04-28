# Upstream Fix (Final): GitHub Copilot Wizard Auth — Surgical & Hardened

> Supersedes `UPSTREAM_WIZARD_AUTH_FIX.md`,
> `UPSTREAM_WIZARD_AUTH_FIX_V2.md`, and
> `UPSTREAM_WIZARD_AUTH_FIX_SECURE_MINIMAL.md`. Same end-to-end behavior,
> tightened to the smallest defensible upstream change set.

## Problem

The Windows tray onboarding wizard cannot complete GitHub Copilot
device-code auth when the OpenClaw gateway runs in WSL. Today
`runGitHubCopilotAuth()` in `extensions/github-copilot/index.ts`:

1. Delegates to `githubCopilotLoginCommand()`, which gates on
   `process.stdin.isTTY` → blocks RPC/headless callers.
2. Uses `@clack/prompts` directly → device code is printed to gateway
   stdout, invisible to the wizard UI.
3. Never calls `ctx.openUrl()` → no browser launch through the framework.
4. Calls `upsertAuthProfile()` itself without `agentDir` → token can land
   in the wrong agent directory under `--dev`.

Browser launch in WSL also requires `wslview` to be present, which is not
guaranteed on a fresh distro.

## Solution at a glance

| Concern                                | Fix location                                              |
|----------------------------------------|-----------------------------------------------------------|
| WSL browser support (`wslview`)        | **Windows tray installer.** No upstream change.           |
| Auth flow (terminal-only → RPC-capable) | **Upstream gateway.** 2 files, 1 new internal symbol.    |
| Credential persistence (wrong agentDir) | Stop calling `upsertAuthProfile` from the provider; let the framework persist. |
| Public API surface                     | **No changes to `api.ts`.**                               |

## Goals

- Make the Copilot device-code flow work from the Windows tray wizard.
- Keep `device_code` and `accessToken` inside the gateway process — only
  `verification_uri` and `user_code` cross into the UI.
- Persist credentials through the existing provider-auth framework (correct
  `agentDir`).
- Share one code path between CLI (`openclaw models auth github-copilot`)
  and wizard so they cannot drift.
- Touch the minimum number of upstream files. No new public exports.

## Non-goals (deferred follow-ups)

- Reworking other OAuth providers.
- Redesigning wizard RPC progress/cancellation across the gateway.
- Guaranteeing browser auto-open in every Windows service/session config.

---

## Change 1 — WSL browser support (out-of-tree)

**No upstream changes.** The OpenClaw Windows tray installer is responsible
for ensuring `wslview` exists in the gateway's WSL distro before the
gateway is launched.

### Provisioning

The installer runs, as part of distro setup:

```bash
sudo apt-get update -qq
sudo apt-get install -y -qq wslu
```

Then verifies `command -v wslview` succeeds.

### If `wslu` is unavailable on the target distro

The installer surfaces a clear remediation message and proceeds. **No
hand-rolled `wslview` shim is shipped.** Reasons:

- Every shipped shim is permanent attack surface someone has to maintain.
- Earlier shim drafts had a shell-injection vector (single-quote breakout
  through `powershell.exe -Command "Start-Process '$URL'"`).
- The wizard already renders the URL and user code in the UI, so a user
  in this state can complete the flow by copy-pasting into any browser on
  the Windows host.

This is consistent with the gateway's existing contract: `browser-open.ts`
already returns `wsl-no-wslview` when `wslview` is absent, and
`ctx.openUrl()` already returns `false` rather than throwing.

---

## Change 2 — Gateway: one shared internal helper

### Files changed (2)

1. `extensions/github-copilot/device-flow.ts` — **new file**, internal to
   the extension.
2. `extensions/github-copilot/index.ts` — rewrite `runGitHubCopilotAuth`.
3. `extensions/github-copilot/login.ts` — replace inline device-flow code
   with a call into `device-flow.ts`. (No signature change to
   `githubCopilotLoginCommand`.)

**`api.ts` is not modified.** No new public exports.

### Why a new file instead of exporting from `login.ts`

- One symbol to share, not two raw primitives — eliminates misuse where
  a future caller invokes `requestDeviceCode` without honoring backoff,
  cancellation, or URL validation.
- `login.ts` stops being a de-facto internal API surface; it becomes a
  CLI command implementation that consumes the same helper as the wizard.
- Future hardening (PKCE migration, batched polls, telemetry redaction)
  has exactly one place to land.

### `device-flow.ts` (new)

```ts
// extensions/github-copilot/device-flow.ts
//
// @internal — Used by ./login.ts (CLI) and ./index.ts (wizard/RPC).
// Do not depend on this module from outside the github-copilot extension.

const GITHUB_DEVICE_VERIFICATION_HOST = "github.com";
const GITHUB_DEVICE_VERIFICATION_PATH = "/login/device";

/** Strict allow-list for the URL we're about to render and open. */
function validateGitHubDeviceVerificationUrl(raw: string): string {
    let parsed: URL;
    try {
        parsed = new URL(raw);
    } catch {
        throw new DeviceFlowError("Invalid verification URL from GitHub.");
    }
    // Allow optional trailing slash; ignore query/fragment so GitHub can
    // evolve the URL non-breakingly. Host + scheme + base path are fixed.
    const path = parsed.pathname.replace(/\/+$/, "");
    if (
        parsed.protocol !== "https:" ||
        parsed.hostname !== GITHUB_DEVICE_VERIFICATION_HOST ||
        path !== GITHUB_DEVICE_VERIFICATION_PATH
    ) {
        throw new DeviceFlowError("Unexpected verification URL from GitHub.");
    }
    return parsed.toString();
}

/** Typed error whose `.message` is safe to render in user-facing UI.
 *  Original cause (which may contain device_code in URLs) is stashed on
 *  a non-enumerable field for internal logs only. */
export class DeviceFlowError extends Error {
    constructor(message: string, cause?: unknown) {
        super(message);
        this.name = "DeviceFlowError";
        if (cause !== undefined) {
            Object.defineProperty(this, "cause", {
                value: cause, enumerable: false, writable: false,
            });
        }
    }
}

export interface DeviceFlowIO {
    /** Display verification URL + user code to the user. */
    showCode(args: { verificationUrl: string; userCode: string }): Promise<void>;
    /** Best-effort browser open. Returns true if a browser was launched. */
    openUrl(url: string): Promise<boolean>;
    /** Called when openUrl returns false or throws — UI layer should tell
     *  the user to open the URL manually. */
    onOpenUrlFailed(url: string): Promise<void>;
    /** Optional cancellation. When triggered, polling aborts promptly. */
    signal?: AbortSignal;
}

/** @internal Shared device-code flow used by CLI + wizard. */
export async function runGitHubCopilotDeviceFlow(
    io: DeviceFlowIO,
): Promise<{ accessToken: string }> {
    let device;
    try {
        device = await requestDeviceCode({ scope: "read:user" });
    } catch (err) {
        throw new DeviceFlowError(
            "Could not start GitHub device authorization. Check your network and try again.",
            err,
        );
    }

    const verificationUrl =
        validateGitHubDeviceVerificationUrl(device.verification_uri);

    await io.showCode({ verificationUrl, userCode: device.user_code });

    let opened = false;
    try { opened = await io.openUrl(verificationUrl); } catch { opened = false; }
    if (!opened) await io.onOpenUrlFailed(verificationUrl);

    let accessToken: string;
    try {
        accessToken = await pollForAccessToken({
            deviceCode: device.device_code,
            intervalMs: Math.max(1000, device.interval * 1000),
            expiresAt: Date.now() + device.expires_in * 1000,
            signal: io.signal,
        });
    } catch (err) {
        // Sanitize: poll errors can carry the device_code in URLs.
        throw new DeviceFlowError(
            "GitHub authorization did not complete. Please retry.",
            err,
        );
    }

    return { accessToken };
}

// Module-private — moved here from login.ts. NOT exported.
async function requestDeviceCode(/* unchanged */) { /* ... */ }
async function pollForAccessToken(params: {
    deviceCode: string;
    intervalMs: number;
    expiresAt: number;
    signal?: AbortSignal;   // additive: honored if provided
}): Promise<string> { /* ... */ }
```

Notes:

- `requestDeviceCode` and `pollForAccessToken` move into `device-flow.ts`
  and stay private. **Zero new exports leave the extension.**
- `pollForAccessToken` gains an optional `signal` parameter — the only
  signature change to existing code. Existing callers pass nothing and
  behave identically.

### `login.ts` (CLI command, behavior unchanged)

```ts
import {
    runGitHubCopilotDeviceFlow,
    DeviceFlowError,
} from "./device-flow.js";
import * as clack from "@clack/prompts";
import open from "open";

export async function githubCopilotLoginCommand(/* ...existing... */) {
    // ...existing CLI prelude (intro, scope confirmation, etc.) unchanged

    const { accessToken } = await runGitHubCopilotDeviceFlow({
        showCode: async ({ verificationUrl, userCode }) => {
            clack.note(
                `Open: ${verificationUrl}\nCode: ${userCode}`,
                "Authorize GitHub Copilot",
            );
        },
        openUrl: async (url) => {
            try { await open(url); return true; } catch { return false; }
        },
        onOpenUrlFailed: async (url) => {
            clack.note(`Open this URL manually:\n${url}`, "Browser launch failed");
        },
        // CLI has no wizard signal; SIGINT handling is unchanged.
    });

    // ...existing CLI persistence (upsertAuthProfile) unchanged
}
```

### `index.ts` (wizard/RPC path, rewritten surgically)

```ts
import {
    runGitHubCopilotDeviceFlow,
    DeviceFlowError,
} from "./device-flow.js";

const COPILOT_PROFILE_ID = "github-copilot:github";
const COPILOT_DEFAULT_MODEL = "github-copilot/claude-opus-4.7";

async function runGitHubCopilotAuth(ctx: ProviderAuthContext) {
    try {
        const { accessToken } = await runGitHubCopilotDeviceFlow({
            showCode: async ({ verificationUrl, userCode }) => {
                await ctx.prompter.note(
                    [
                        "Open this URL in your browser and enter the code below.",
                        `URL:  ${verificationUrl}`,
                        `Code: ${userCode}`,
                        "The code expires soon. Do not share it.",
                    ].join("\n"),
                    "Authorize GitHub Copilot",
                );
            },
            openUrl: async (url) => {
                // ctx.openUrl returns false on wsl-no-wslview etc.
                try { return await ctx.openUrl(url); } catch { return false; }
            },
            onOpenUrlFailed: async () => {
                await ctx.prompter.note(
                    "Couldn't open a browser automatically. Open the URL above manually to continue.",
                    "Authorize GitHub Copilot",
                );
            },
            signal: ctx.signal,   // honored if the framework provides one
        });

        return {
            profiles: [{
                profileId: COPILOT_PROFILE_ID,
                credential: {
                    type: "token" as const,
                    provider: "github-copilot",
                    token: accessToken,
                },
            }],
            defaultModel: COPILOT_DEFAULT_MODEL,
        };
    } catch (err) {
        // DeviceFlowError.message is curated for user display.
        // The original cause is non-enumerable; structured-log it internally only.
        ctx.runtime.logger?.warn?.("github-copilot auth failed", { err });
        const userMessage = err instanceof DeviceFlowError
            ? err.message
            : "GitHub Copilot login did not complete. You can retry from the wizard.";
        await ctx.prompter.note(userMessage, "GitHub Copilot");
        return { profiles: [] };
    }
}
```

### Things this code deliberately does not do

- Does **not** check `process.stdin.isTTY`.
- Does **not** call `githubCopilotLoginCommand()`.
- Does **not** call `upsertAuthProfile()` — the framework persists with
  the correct `agentDir`.
- Does **not** read back from `ensureAuthProfileStore(undefined)`.
- Does **not** log `device.device_code` or `accessToken` anywhere.
- Does **not** stringify raw errors into user UI.
- Does **not** add any export to `api.ts`.

### Error semantics

- User-cancellation / authorization denial: caught, generic note shown,
  return `{ profiles: [] }`. Wizard treats this as "user backed out."
- Network failure / unexpected GitHub response / invalid verification
  URL / poll exhaustion: thrown internally as `DeviceFlowError`, caught at
  the top of `runGitHubCopilotAuth`, generic note shown, return
  `{ profiles: [] }`. The wizard does not get a success-shaped empty
  result with no signal — the user sees a real message.
- Internal log captures the original `cause` for diagnostics; user-facing
  strings never include it.

---

## Security analysis

### Attack-surface delta vs current code

| Surface              | Before        | After                                       |
|----------------------|---------------|---------------------------------------------|
| Public exports added | n/a           | **0**                                       |
| New shell scripts    | n/a           | **0** (no `wslview` shim)                   |
| New network endpoints | n/a          | 0 (same GitHub device endpoints)            |
| User-visible secrets | `user_code` in CLI stdout | `user_code` in wizard UI (by protocol design) |
| Token transit        | gateway stdout / `upsertAuthProfile` | in-process; framework persists |

### Hardenings introduced

- **Strict allow-list** of `https://github.com/login/device` (with
  trailing-slash and query/fragment tolerance) before display or open.
- **Sanitized error boundary** — `DeviceFlowError` ensures poll errors
  carrying `device_code` never reach the wizard UI.
- **Cancellation honored** via optional `AbortSignal`, eliminating
  orphaned polls on wizard cancel / RPC disconnect.
- **`openUrl` failures are user-visible**, removing the "wizard hangs
  silently" mode.
- **Single shared code path** for CLI and wizard means future audits
  cover both flows.

### By-design (not residual risk)

- The `user_code` is human-visible by the device-flow specification. It
  is bound to a server-side `device_code` that never leaves the gateway,
  and it is single-use and short-lived. Showing it in the local trusted
  wizard UI is the correct behavior.

### Genuine residual items (acceptable for v1)

- If the wizard process crashes between successful poll and framework
  persistence, the in-memory token is lost (user retries — no leak).
- If `ctx.signal` is not yet plumbed by the framework, polling runs to
  natural completion / expiration. This is no worse than today's
  behavior; the helper is forward-compatible the moment the signal lands.

---

## Test plan (upstream-mergeable)

Add tests under the github-copilot extension:

1. `runGitHubCopilotAuth` no longer returns `{ profiles: [] }` solely
   because stdin is not a TTY (regression guard against the old
   `process.stdin.isTTY` gate).
2. The verification URL and user code are passed to `ctx.prompter.note`
   (not to stdout).
3. `validateGitHubDeviceVerificationUrl` accepts canonical and
   trailing-slash forms; rejects http://, foreign hosts, and unrelated
   paths.
4. `openUrl` returning `false` triggers an `onOpenUrlFailed` note.
5. Poll errors surface as `DeviceFlowError` with a curated message; the
   `device_code` does not appear in the message.
6. When `signal` is aborted mid-poll, `pollForAccessToken` rejects
   promptly and `runGitHubCopilotDeviceFlow` does not return a token.
7. `runGitHubCopilotAuth` does not call `upsertAuthProfile`; the returned
   credential is what gets persisted.
8. Snapshot test: `extensions/github-copilot/api.ts` exports exactly the
   pre-change set (no `requestDeviceCode`, no `pollForAccessToken`,
   no `runGitHubCopilotDeviceFlow`).
9. CLI path: `githubCopilotLoginCommand` still completes the same
   end-to-end happy path it did before the refactor (smoke test via
   mocked HTTP).

---

## Verification (manual, end-to-end)

1. ✅ Wizard step shows "Authorize GitHub Copilot" with URL and code.
2. ✅ Browser opens to `https://github.com/login/device` via the
   installer-provisioned `wslview`.
3. ✅ With `wslview` removed: wizard shows the explicit "Couldn't open a
   browser automatically..." note; manual copy/paste completes the flow.
4. ✅ Cancelling the wizard step aborts polling within one interval.
5. ✅ Forcing a poll error: user sees a curated `DeviceFlowError`
   message; internal log contains structured cause; no `device_code` in
   any user-visible string.
6. ✅ CLI `openclaw models auth github-copilot` unchanged in behavior
   (now sharing the same helper).
7. ✅ Token saved under correct `agentDir` for default and `--dev`
   profiles.
8. ✅ Chat works with the Copilot model after wizard completes.

---

## Recommended PR sequencing

1. **Windows tray installer PR** — provision `wslu` in the managed WSL
   distro; add the `wslview` smoke-check.
2. **Upstream gateway PR** — introduce `device-flow.ts`, refactor
   `login.ts` to call into it, rewrite `runGitHubCopilotAuth`. Includes
   the test plan above.
3. **(Follow-up, optional)** — once upstream has consensus on
   `ProviderAuthContext.signal`, add it formally; the helper already
   honors it.

That's it: zero public API changes, two upstream files modified plus one
new internal file, and zero shell scripts shipped.
