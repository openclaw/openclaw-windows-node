# Gateway Wizard Auth: Windows/WSL Gap Analysis

## Summary

The OpenClaw gateway wizard exposes provider auth flows (GitHub Copilot, OpenAI, Anthropic, etc.) 
over RPC so that GUI clients (macOS app, Windows tray, Control UI) can render setup steps without 
re-implementing onboarding logic. However, **provider auth flows that require browser interaction 
fail when the gateway runs in WSL on Windows** because the auth implementations use terminal-only 
UI libraries and TTY checks that don't work in RPC mode.

This affects all Windows users who run the gateway in WSL and use the Windows tray app's onboarding 
wizard to configure AI providers.

## The Architecture

```
┌─────────────────┐     RPC (WebSocket)     ┌──────────────────────┐
│  Windows Tray   │ ◄──────────────────────► │  Gateway (WSL)       │
│  Onboarding     │    wizard.start/next     │                      │
│  Wizard         │                          │  ┌────────────────┐  │
│                 │    ◄── step payload ──── │  │ Wizard Session  │  │
│  Renders steps  │                          │  │                 │  │
│  Collects input │    ── answer ──────────► │  │ Provider Auth   │  │
│                 │                          │  │ (login.ts)      │  │
│                 │                          │  │                 │  │
│                 │                          │  │ @clack/prompts ◄┤──── TTY only!
│                 │                          │  │ open(url) ◄─────┤──── needs browser
│                 │                          │  └────────────────┘  │
└─────────────────┘                          └──────────────────────┘
```

## Two Execution Modes, One Implementation

The gateway wizard has two prompter implementations:

| | CLI Mode (`openclaw onboard`) | RPC Mode (GUI clients) |
|---|---|---|
| **Prompter** | `ClackPrompter` → `@clack/prompts` | `WizardSessionPrompter` → RPC steps |
| **UI** | Terminal ANSI art | Client-rendered GUI |
| **Browser** | `open()` npm package (works) | Must be surfaced to client |
| **TTY** | `process.stdin.isTTY = true` | `process.stdin.isTTY = false` (or irrelevant) |
| **Status** | ✅ Works | ❌ Auth flows fail |

## The Problem

Provider auth implementations have a **dual code path** problem:

### Layer 1: Provider Registration (index.ts) — Uses `ctx.prompter` ✅

```typescript
// extensions/github-copilot/index.ts
async function runGitHubCopilotAuth(ctx: ProviderAuthContext) {
    await ctx.prompter.note("This will open a GitHub device login...");  // ✅ RPC-safe
    // ...
    await githubCopilotLoginCommand({ yes: true }, ctx.runtime);  // ❌ calls CLI code
}
```

### Layer 2: Login Command (login.ts) — Uses `@clack/prompts` directly ❌

```typescript
// extensions/github-copilot/login.ts
export async function githubCopilotLoginCommand(opts, runtime) {
    if (!process.stdin.isTTY) {
        throw new Error("requires an interactive TTY");  // ❌ Fails in RPC mode
    }
    intro(stylePromptTitle("GitHub Copilot login"));     // ❌ Terminal-only
    // ...
    note(`Visit: ${device.verification_uri}\nCode: ${device.user_code}`);  // ❌ Lost in RPC
    // ...
    const accessToken = await pollForAccessToken(...);   // ❌ Blocks for minutes
}
```

When the wizard calls `githubCopilotLoginCommand()`:
1. **TTY check fails** → error thrown → wizard shows "An error occurred"
2. Even if TTY check passes, **device code is printed to gateway's stdout** via `@clack/prompts`, 
   never surfaced to the Windows client
3. Even if the device code were surfaced, **no browser opens** because the gateway runs headless in WSL
4. Even if the browser could open, the `wizard.next` RPC call has a **30-second timeout** but 
   auth flows take minutes

## Affected Providers

| Provider | Auth Type | Uses `@clack/prompts` directly | Uses `ctx.prompter` | Status |
|----------|-----------|-------------------------------|---------------------|--------|
| **GitHub Copilot** | `device_code` | ✅ YES (`login.ts`) | Partially (`index.ts`) | ❌ Broken |
| **OpenAI Codex** | `device_code` | No (callback-based) | Via callbacks | ⚠️ Needs testing |
| **MiniMax Portal** | `device_code` (OAuth) | No | ✅ YES | ✅ Safe |
| **Anthropic** | `api_key` | No | ✅ YES | ✅ Safe |
| **OpenAI** | `api_key` | No | ✅ YES | ✅ Safe |
| **Google Gemini** | `oauth` | Needs verification | Needs verification | ⚠️ Unknown |
| **All other API key providers** | `api_key` | No | ✅ YES | ✅ Safe |

**Pattern:** Providers that accept API keys work fine (text input via `ctx.prompter`). Providers 
that need device code / OAuth / browser interaction fail because the browser launch and device 
code display happen server-side in code paths designed for CLI terminals.

## WSL Browser Launch: Known Limitations

Research across Microsoft/WSL, wslutilities/wslu, and community forums confirms:

- **No reliable standard mechanism** exists for background WSL processes to open Windows browsers
- `xdg-open` is not installed by default on Ubuntu 22.04+ (wslu was removed from default repos)
- `wslview` (from wslu) works from interactive terminals but has known failures in headless/non-TTY 
  contexts ([wslu #66](https://github.com/wslutilities/wslu/issues/66), 
  [wslu #242](https://github.com/wslutilities/wslu/issues/242))
- WSL Store version cannot be accessed from Session 0 contexts 
  ([WSL #9231](https://github.com/microsoft/WSL/issues/9231))
- The `open` npm package has WSL support but depends on finding Edge/Firefox/Chrome executables 
  at hardcoded Windows paths

**Bottom line:** Even if we fixed the code path to call `open(url)`, it wouldn't reliably work 
from a background WSL gateway process on all Windows setups.

## Proposed Solution

### Core Principle

**Device code auth flows must be surfaced through `ctx.prompter` (wizard RPC), not executed 
server-side with terminal UI.** The gateway should send the URL and device code as wizard step 
data; the client opens the browser and shows the code.

### Implementation

**Refactor provider auth to separate concerns:**

```typescript
// BEFORE: login.ts does everything server-side
async function githubCopilotLoginCommand(opts, runtime) {
    const device = await requestDeviceCode({ scope: "read:user" });
    note(`Visit: ${device.verification_uri}\nCode: ${device.user_code}`);  // terminal
    open(device.verification_uri);  // server browser
    const token = await pollForAccessToken(...);  // server polling
}

// AFTER: split into request + poll, let client handle UX
async function runGitHubCopilotAuth(ctx: ProviderAuthContext) {
    const device = await requestDeviceCode({ scope: "read:user" });
    
    // Surface URL + code through wizard prompter (works in both CLI and RPC)
    await ctx.prompter.note(
        `Visit: ${device.verification_uri}\nCode: ${device.user_code}`,
        "Authorize GitHub Copilot"
    );
    
    // Open browser through ctx (client can do this natively)
    await ctx.openUrl(device.verification_uri);
    
    // Poll with progress (wizard shows spinner, client stays responsive)
    const progress = ctx.prompter.progress("Waiting for authorization...");
    const token = await pollForAccessToken({...});
    progress.stop("Authorized");
}
```

### What Each Client Does

| Responsibility | CLI Mode | RPC/GUI Mode |
|---|---|---|
| **Show device code** | `@clack/prompts note()` | Wizard step → client renders prominently |
| **Open browser** | `open()` npm package | Client calls native `Launcher.LaunchUriAsync()` |
| **Show progress** | Terminal spinner | Client renders `ProgressRing` |
| **Wait for auth** | `pollForAccessToken()` blocks | Same, but `wizard.next` timeout = 5 min |

### Changes Required

1. **`extensions/github-copilot/index.ts`** — Inline the device code request + poll logic using 
   `ctx.prompter` instead of calling `githubCopilotLoginCommand()` which uses `@clack/prompts`

2. **`extensions/github-copilot/login.ts`** — Export `requestDeviceCode()` and 
   `pollForAccessToken()` as standalone functions (they're already well-structured for this)

3. **`src/plugins/types.ts`** — `ProviderAuthContext` already has `openUrl: (url: string) => Promise<void>` 
   — this is the correct mechanism for browser opening. Ensure all device code providers use it.

4. **`src/wizard/session.ts`** — Increase timeout for auth-related steps (currently no per-step 
   timeout, but RPC clients impose their own)

5. **Repeat for each device code provider** — OpenAI Codex already uses callbacks (easier), 
   MiniMax already uses `ctx.prompter` (safe)

### Scope

- **GitHub Copilot**: Needs refactor (most users, highest priority)
- **OpenAI Codex**: Has callback pattern, needs `ctx.openUrl` integration
- **MiniMax Portal**: Already safe (uses `ctx.prompter`)
- **Future providers**: Use `ctx.prompter` + `ctx.openUrl` pattern (no direct `@clack/prompts`)

## Interim Workaround (Windows Tray v1)

Until the upstream fix lands, the Windows wizard can:

1. Detect auth steps that will fail (step message contains "device login" / "authorization")
2. Show: "Please configure AI providers via `openclaw onboard` in your WSL terminal"
3. Skip the auth step and continue with remaining wizard steps
4. API key providers (Anthropic, OpenAI API key mode) work fine — no workaround needed

## Why the macOS Client Works

The macOS app doesn't have this problem because it operates in a fundamentally different 
environment:

### Gateway Runs Natively on macOS

On Mac, the gateway runs as a **native macOS process** (via LaunchAgent), not inside a 
virtualized Linux environment. This means:

1. **`process.stdin.isTTY` = true** — The gateway process has full terminal access
2. **`open()` npm package works natively** — It calls macOS `open` command, which launches 
   Safari/Chrome directly. No WSL↔Windows boundary to cross.
3. **`@clack/prompts` works** — Terminal UI renders correctly to the native console
4. **No Session 0 restrictions** — LaunchAgent runs in the user's login session with full 
   GUI access (unlike WSL which can't access Windows Session 0 — see Microsoft/WSL #9231)

### The Mac Wizard Still Has the Same Code Path

The Mac wizard uses the **exact same RPC mechanism** (`wizard.start` / `wizard.next`). When 
the GitHub Copilot auth step runs:

1. `githubCopilotLoginCommand()` is called server-side (same as WSL)
2. `process.stdin.isTTY` check passes (native process has TTY)
3. `@clack/prompts note()` prints the device code to the gateway's console log
4. The `open` npm package calls macOS `open` → browser launches immediately
5. `pollForAccessToken()` blocks while user authenticates in browser
6. Auth completes → wizard advances

The device code is technically only visible in the gateway's console log (not in the Mac 
wizard UI), but the browser opens automatically so the user doesn't need to see it. The 
Mac wizard just shows a progress spinner while waiting.

### Why WSL Breaks This

On Windows, the gateway runs inside WSL, which creates three barriers:

| Barrier | macOS | Windows (WSL) |
|---------|-------|---------------|
| **TTY access** | ✅ Native LaunchAgent has TTY | ❌ Background WSL process may lack TTY |
| **Browser launch** | ✅ `open` calls macOS `open` cmd | ❌ `open` tries `xdg-open` (not installed by default) or Edge path (may not exist) |
| **Console output** | ✅ `@clack/prompts` renders in Terminal.app | ❌ Output goes to WSL stdout (invisible when started via `Start-Process`) |
| **Session access** | ✅ LaunchAgent = user session | ❌ WSL Store can't access Session 0 (Microsoft/WSL #9231) |

### The Real Fix

The problem isn't WSL-specific — it's that **provider auth code assumes a native terminal 
environment**. The fix is to route auth UX through `ctx.prompter` (the wizard RPC channel) 
instead of `@clack/prompts` (direct terminal output). This would fix WSL AND make the Mac 
wizard properly display device codes in its own UI instead of only in the console log.



| Platform | CLI (`openclaw onboard`) | GUI Wizard (RPC) |
|----------|--------------------------|-------------------|
| **macOS** | ✅ Works | ✅ Works (native `open`) |
| **Linux** | ✅ Works | ✅ Works (`xdg-open`) |
| **Windows (WSL)** | ✅ Works (interactive TTY) | ❌ **Broken** (this gap) |

## References

- Microsoft/WSL #8892 — `xdg-open` not available by default
- Microsoft/WSL #9231 — WSL not accessible from Session 0
- wslutilities/wslu #66 — `tcgetpgrp` errors in non-interactive shells
- wslutilities/wslu #242 — `wslview` fails from Putty/SSH
- `ProviderAuthContext.openUrl` — Already exists in gateway type system
- `extensions/github-copilot/login.ts` — Standalone device code functions ready to extract
