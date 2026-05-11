# Mattingly: actual fixes for PR #274 bugs 1, 4, 5

## Bug 1 — chat window auto-launch on Finish

Changed `OnboardingWindow.OnWizardComplete()` to ignore `WizardLifecycleState == "complete"`. The signal now is: the window is completing from `OnboardingRoute.Ready` and `StartupSetupState.RequiresSetup(settings, identityDataPath)` is false. That is the path the Finish button actually takes: `Ready` page Finish -> `OnboardingState.Complete()` -> `OnOnboardingFinished()` -> `OnWizardComplete()`.

Log to validate: `[OnboardingWindow] OnWizardComplete launching chat`.

## Bug 4 — BOOTSTRAP.md kickoff injection

Hardened `BootstrapMessageInjector`:

- Traverses shadow DOM for Lit UI controls.
- Probes and logs visible control count: `[OpenClaw] Bootstrap probe controls=N`.
- Supports `textarea`, text inputs, contenteditable, and role=textbox.
- Uses native value setters so controlled inputs see the value.
- Clicks Send/form-submit/Enter fallbacks.
- Does **not** burn `HasInjectedFirstRunBootstrap` when the script returns `no-input`; the gate is only persisted on `sent`.

Aaron still needs to move the call site to after successful chat navigation because current `App.ShowChatWindow()` can see `TryGetScriptExecutor()==null` when the WebView2 is still initializing.

Exact handoff line for Aaron in `ChatWindow.xaml.cs` NavigationCompleted success branch after `RequestChatInputFocus();`:

```csharp
OpenClawTray.Services.BootstrapMessageInjector.ScriptExecutor exec = script => WebView.CoreWebView2.ExecuteScriptAsync(script).AsTask();
_ = OpenClawTray.Services.BootstrapMessageInjector.InjectAsync(exec, ((App)Microsoft.UI.Xaml.Application.Current).Settings, initialDelayMs: 500);
```

If `App.Settings` is not exposed, add an internal property returning `_settings`, or route the existing `_settings` from `App.ShowChatWindow()` into a ChatWindow method. The important point is that the call must happen inside `NavigationCompleted` when `e.IsSuccess` is true.

## Bug 5 — autostart default/toggle

Changed `ReadyPage` to render the toggle ON as a safety default, then sync to `Settings.AutoStart` on mount and immediately call `AutoStartManager.SetAutoStart()` so a user who never toggles still gets the Run-key. The toggle handler still persists settings and updates the Run-key immediately.

Changed `AutoStartManager.SetAutoStart()` to use `Registry.CurrentUser.CreateSubKey(...)` instead of `OpenSubKey(...)`, so it can create the Run key/value when missing instead of silently returning.

Manual registry validation:

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name OpenClawTray -ErrorAction SilentlyContinue
```

## Validation

Ran `./build.ps1`: passed. Per fast-loop directive, skipped `dotnet test`.
