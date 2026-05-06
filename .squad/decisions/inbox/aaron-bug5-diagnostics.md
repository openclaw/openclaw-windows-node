# Aaron — Bug #5 Diagnostics Landed (commit 20af4f7)

**Scope:** Diagnostics-ONLY per RubberDucky-6's conditional-approval closure condition #1. NO structural changes. The Complete→advance→Wizard chain now logs every edge so the next manual run is dispositive.

## Files Modified (5 files, +47 / -7)

| File | LOC delta |
| --- | --- |
| `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs` | +20 / -5 |
| `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingApp.cs` | +12 / -1 |
| `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs` | +7 / -1 |
| `src/OpenClaw.Tray.WinUI/Onboarding/Pages/WizardPage.cs` | +7 / 0 |
| `src/OpenClaw.Shared/OpenClawGatewayClient.cs` | +1 / 0 |

## Per-Edge Log Lines Added

### 1. LocalSetupProgressPage.cs (Complete branch, lines 157-183)
| Edge | Log line | File:Line |
| --- | --- | --- |
| Schedule | `[LocalSetupProgress] Status=Complete observed; scheduling RequestAdvance after 1000ms` | `LocalSetupProgressPage.cs:162` |
| Delay elapsed | `[LocalSetupProgress] Delay elapsed; dispatching RequestAdvance` | `LocalSetupProgressPage.cs:165` |
| Dispatched lambda entry (BEFORE guard) | `[LocalSetupProgress] Dispatched lambda entered; checking guard` | `LocalSetupProgressPage.cs:168` |
| Guard pass | `[LocalSetupProgress] Guard passed` | `LocalSetupProgressPage.cs:171` |
| Guard skip | `[LocalSetupProgress] Guard skipped: CurrentRoute={route}` | `LocalSetupProgressPage.cs:177` |
| Pre-RequestAdvance | `[LocalSetupProgress] Calling state.RequestAdvance()` | `LocalSetupProgressPage.cs:172` |
| TryEnqueue result | `[LocalSetupProgress] TryEnqueue returned {true|false}` (captured into `enqueued` and logged after the call — observation only, not acted upon) | `LocalSetupProgressPage.cs:181` |

### 2. OnboardingState.cs (RequestAdvance, lines 48-54)
| Edge | Log line | File:Line |
| --- | --- | --- |
| Top of RequestAdvance | `[OnboardingState] RequestAdvance invoked; subscriber count = N` (uses `AdvanceRequested?.GetInvocationList().Length ?? 0`) | `OnboardingState.cs:51` |
| After invocation | `[OnboardingState] AdvanceRequested invoked; returned` | `OnboardingState.cs:53` |

### 3. OnboardingApp.cs (handler + GoNext, lines 36-72)
| Edge | Log line | File:Line |
| --- | --- | --- |
| Handler entry | `[OnboardingApp] AdvanceRequested handler entered; current Props.CurrentRoute={X}, computed pageIndex={N}, total pages={M}` | `OnboardingApp.cs:69` |
| Advance | `[OnboardingApp] Advancing pageIndex {N}→{N+1}, next route={Y}` | `OnboardingApp.cs:42` |
| At-last-page no-op | `[OnboardingApp] AdvanceRequested no-op: at last page (pageIndex={N}, total={M})` | `OnboardingApp.cs:51` |

### 4. WizardPage.cs (mount + polling + pre-send, lines 173-217)
| Edge | Log line | File:Line |
| --- | --- | --- |
| Mount / constructed | `[Wizard] WizardPage constructed; gatewayClient={null|present}` | `WizardPage.cs:176` |
| Mount effect started | `[Wizard] Mount effect started; about to send wizard.start` | `WizardPage.cs:177` |
| Polling loop entry | `[Wizard] Polling for gateway client; attempt N` (logged each iteration) | `WizardPage.cs:202` |
| Pre-wizard.start send | `[Wizard] Sending wizard.start frame` | `WizardPage.cs:215` |

### 5. OpenClawGatewayClient.cs (SendWizardRequestAsync, line 266)
| Edge | Log line | File:Line |
| --- | --- | --- |
| Frame send | `[GatewayClient] Sending frame: {method}` | `OpenClawGatewayClient.cs:266` |

## Behavior-Preservation Notes (no structural changes)
- `Task.Delay(...).ContinueWith(... dispatcher.TryEnqueue(...))` pattern is **unchanged**. Only added: capturing `TryEnqueue`'s bool into a local, logging it, and not acting on it.
- Guard `advanceRef.CurrentRoute == OnboardingRoute.LocalSetupProgress` semantics **unchanged**; only the else branch logs (and returns implicitly as before).
- `RequestAdvance` still does `AdvanceRequested?.Invoke(this, EventArgs.Empty);` — wrapped between two log lines.
- `UseEffect` subscription wiring **unchanged**; the handler body now logs first, then calls `GoNext()`.
- WizardPage poll loop, retry count (30), retry delay (1s), and connection check **unchanged**; just adds a log per iteration and one log before `wizard.start`.
- `SendWizardRequestAsync`: only adds an `_logger.Info` line at the top (after the `IsConnected` guard) — no logic change.

## Build + Validation

| Step | Result |
| --- | --- |
| `./build.ps1` (default Debug, win-x64) | ✅ Shared, Cli, WinNodeCli, WinUI all built |
| `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q` | ❌ **x64 link deferred** — MSB3027/MSB3021: `OpenClaw.Shared.dll` is locked by **OpenClaw.Tray.WinUI (PID 36492)**. Per playbook, defer until Mike kills PID 36492 + Bostick rebuilds. |
| Tray tests (`OpenClaw.Tray.Tests`, with `OPENCLAW_REPO_ROOT` set) | ✅ **Failed: 0, Passed: 559, Total: 559** — baseline preserved |
| Shared tests (`OpenClaw.Shared.Tests`, with `OPENCLAW_REPO_ROOT` set) | ✅ **Failed: 0, Passed: 1158, Skipped: 22, Total: 1180** — baseline preserved |

Tests-without-env-var: 6 Tray + 1 Shared failures, all pre-existing environmental (`Could not find repository root. Set OPENCLAW_REPO_ROOT...` in `LocalizationValidationTests` and `ReadmeValidationTests`). Disappear once `OPENCLAW_REPO_ROOT` is set. Not caused by these diagnostics.

## DLL Freshness (default-platform Debug, NOT x64)

```
Name                    LastWriteTime
----                    -------------
OpenClaw.Tray.WinUI.exe 5/5/2026 10:26:42 AM
OpenClaw.Shared.dll     5/5/2026 10:26:08 AM
```

**x64 build artifacts are stale** (PID 36492 still holds the 10:23 binary). The fresh diagnostics binary will only land in `bin\x64\Debug\...\win-x64\` after Mike kills PID 36492 and Bostick rebuilds at x64.

## Commit

- Branch: `feat/wsl-gateway-clean` (NOT pushed)
- SHA: **`20af4f72e2aa5cec1bdf7f6c694761e9d9844c3b`**
- Message: `chore(onboarding): add diagnostics around LocalSetupProgress→Wizard advance (Bug #5 instrumentation)`
- Co-authored-by trailer: ✅ included (`Copilot <223556219+Copilot@users.noreply.github.com>`)

## Bostick Handoff

1. **Mike kills PID 36492** (the live d4bc385 tray instance currently locking the x64 .exe / .dll).
2. **Bostick rebuilds** at x64: `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q`. Verify post-edit timestamp on `bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.exe` is later than 2026-05-05T10:26.
3. **Bostick relaunches** the new tray binary.
4. **Mike walks the autopair flow** end-to-end: SetupWarning → SetupPath=Local → LocalSetupProgress (auto-pair) → wait for engine `Status=Complete` → observe whether the wizard hang reproduces.
5. **Capture the log** (`%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`) and grep for the prefixes `[LocalSetupProgress]`, `[OnboardingState]`, `[OnboardingApp]`, `[Wizard]`, `[GatewayClient] Sending frame: wizard.start`. Whichever expected line is **missing** identifies the broken edge:
   - Missing `Delay elapsed` → ContinueWith never fired (TaskScheduler issue).
   - `TryEnqueue returned false` → dispatcher dead/disposed; Wizard never gets a chance.
   - Missing `Dispatched lambda entered` after `TryEnqueue returned true` → dispatcher accepted but never ran lambda (window/thread shut down).
   - `Guard skipped` → `CurrentRoute` mutated before delay elapsed (race we suspected).
   - `Calling state.RequestAdvance()` fires but `[OnboardingState] subscriber count = 0` → handler is unsubscribed at fire time (pageIndex-keyed UseEffect race).
   - `subscriber count > 0` and `[OnboardingApp] AdvanceRequested handler entered` fires but `Advancing pageIndex` is replaced by `no-op: at last page` or stale `pageIndex` → stale closure / clamped index.
   - `[OnboardingApp] Advancing pageIndex N→N+1, next route=Wizard` fires but no `[Wizard] WizardPage constructed` → nav.Navigate not landing on Wizard.
   - `[Wizard] WizardPage constructed` fires but `[Wizard] Polling for gateway client; attempt 1` never appears or only client=null → Wizard mounted but client seeding lost.
   - All Wizard logs through `[Wizard] Sending wizard.start frame` fire but no `[GatewayClient] Sending frame: wizard.start` → frame send threw before reaching SendWizardRequestAsync's IsConnected check (caught upstream).
6. **Aaron then writes the targeted structural fix** based on which exact edge was silent. No restructuring before this evidence per RubberDucky-6.

## Hard Guardrails Honored

- ✅ Diagnostics ONLY. No structural fix. No subscription rewiring. No guard semantics change. No RequestAdvance contract change. No Task.Delay/TryEnqueue restructure.
- ✅ NOT pushed.
- ✅ 559 Tray + 1180 Shared baselines preserved.
- ✅ PID 36492 NOT touched.
