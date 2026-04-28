# AGENTS.md

## File Modification Permissions

The following files may be read, written, or reset WITHOUT asking for user confirmation. These are test/dev artifacts that are routinely modified during the development and testing cycle:

- WSL gateway pairing files (`pending.json`, `paired.json` under `~/.openclaw-dev/devices/` or `~/.openclaw/devices/`)
- `%APPDATA%\OpenClawTray\device-key-ed25519.json` — Windows device identity/token
- `visual-test-output\**` — Screenshot capture output

### CRITICAL: WSL File Access Method

**NEVER use `\\wsl$\` or `\\wsl.localhost\` paths.** They trigger Windows permission prompts.
Always use `wsl bash -c` subprocess commands:

```powershell
# ✅ CORRECT
wsl bash -c 'echo "{}" > ~/.openclaw-dev/devices/paired.json'
wsl bash -c 'cat ~/.openclaw-dev/devices/pending.json'

# ❌ WRONG — triggers permission prompts
Set-Content "\\wsl$\Ubuntu\home\user\.openclaw-dev\devices\paired.json" "{}"
```

This applies to ALL contexts: app code, test scripts, PowerShell commands. No exceptions.

## Required Validation After Every Change

All agents working in this repository must run validation after each code change before marking work complete.

Required steps:

1. Run full repo build:
   - `./build.ps1`
2. Run shared tests:
   - `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore`
3. Run tray tests:
   - `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore`

If a command fails:

1. Fix the issue.
2. Re-run the failed command.
3. Re-run all required validation commands before completion.

Notes:

- If a build/test is blocked by an environmental lock (for example running executable locking output assemblies), stop/close the locking process and rerun.
- Do not claim completion without reporting validation results.

## Required UX Change Workflow

**EVERY visible UI change MUST follow this process. No exceptions.**

### Step 1: Layout Contract (before writing ANY code)
Write out the exact layout structure:
- What rows exist and what's in each row
- What elements share a row vs get their own row
- Left-aligned, right-aligned, centered, or stretched for each element
- Which elements are always visible vs conditional
- Minimum heights/widths for variable-content areas

Present this to the user for approval BEFORE coding.

### Step 2: Choose the Right Layout Primitive
- **VStack** — Simple vertical stacking. Children go top-to-bottom. `HAlign` on children controls horizontal position within the stack width.
- **HStack** — Simple horizontal stacking. Children go left-to-right. Does NOT support "push one child to the right edge" — all children pack left.
- **Grid** — Use when you need "left content + right content on same row" (e.g., label left, button right). This is the ONLY way to right-align one element while left-aligning another on the same row.
- **FlexColumn/FlexRow** — Use when you need grow/shrink behavior with `.Flex(grow: 1)`.

**Common mistakes to avoid:**
- Do NOT use `HAlign(Right)` on a child in HStack expecting it to push right — HStack packs children left-to-right with spacing, `HAlign` has no effect on position.
- Do NOT use `TextBlock("")` as a "spacer" — it doesn't flex.
- Do NOT use an editable `TextField` for read-only display text — it shows a clear button (X). Use `TextBlock().Selectable()` instead.
- Do NOT put copyable command text in an HStack — it will truncate. Use VStack or full-width layout.

### Step 3: Screenshot Verification — MANDATORY GATE

**This step is a BLOCKING GATE. You MUST NOT present UI changes to the user without completing it. No exceptions.**

After every UI change, you must capture and view a screenshot to verify correctness:

1. Build: `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`
2. Kill existing process: `Get-Process -Name "OpenClaw*" -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }`
3. Launch with capture:
   ```powershell
   $env:OPENCLAW_VISUAL_TEST = "1"
   $env:OPENCLAW_VISUAL_TEST_DIR = "<repo>\visual-test-output\verify"
   dotnet run --project src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64
   ```
4. Wait 10+ seconds for render and auto-capture
5. View captured PNGs with the `view` tool (renders images inline)
6. Check: alignment, truncation, spacing, control visibility
7. If ANY issue is found, fix and re-capture
8. Present result to user WITH screenshot path

**If capture fails** (app exits early, no files, crash): debug and fix the capture mechanism before moving on. Do not skip this step.

Verify in each screenshot:
   - All left edges align to the same vertical line
   - Right-aligned elements are flush with container right edge
   - No text is truncated
   - Always-visible elements are visible in default state
   - No unexpected controls (clear buttons, scroll bars)
5. Only THEN present the result to the user

### Step 4: Regression Checklist
Before presenting, verify ALL prior requirements still hold:
- [ ] Left edges aligned
- [ ] Right-aligned elements flush right
- [ ] Status/feedback areas always visible
- [ ] No text truncation
- [ ] No unwanted controls (X buttons, scrollbars)
- [ ] Nav bar still at bottom
- [ ] Step dots still tracking
- [ ] Transitions still smooth

## Debug Environment Variables

The onboarding wizard supports the following environment variables for development and testing.
**These are process-scoped and do not affect other applications.**

| Variable | Purpose | Security Notes |
|----------|---------|---------------|
| `OPENCLAW_LANGUAGE` | Override UI locale (e.g., `zh-cn`, `fr-fr`) | Whitelisted to known locales only |
| `OPENCLAW_FORCE_ONBOARDING` | Show onboarding even with existing token | Development/testing only |
| `OPENCLAW_SKIP_UPDATE_CHECK` | Skip update dialog during testing | Development/testing only |
| `OPENCLAW_GATEWAY_PORT` | Override default gateway port (18789) | Validated: numeric, 1-65535 |
| `OPENCLAW_VISUAL_TEST` | Enable visual test screenshot capture | Set to `"1"` to enable |
| `OPENCLAW_VISUAL_TEST_DIR` | Output directory for visual test screenshots | Path-traversal validated |

**Release build consideration:** `OPENCLAW_FORCE_ONBOARDING` and `OPENCLAW_SKIP_UPDATE_CHECK` should
ideally be gated behind `#if DEBUG` in production builds to prevent misuse.
