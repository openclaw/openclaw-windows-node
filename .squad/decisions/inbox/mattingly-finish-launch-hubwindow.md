# Mattingly: Finish opens HubWindow chat

## Summary
Onboarding completion from Ready now launches the full HubWindow directly on the Chat tab instead of the standalone quick-chat ChatWindow.

## Changes
- `src\OpenClaw.Tray.WinUI\App.xaml.cs`
  - Made `ShowHub(string? navigateTo = null, bool activate = true)` internal so onboarding can reuse the existing hub-opening path.
- `src\OpenClaw.Tray.WinUI\Onboarding\OnboardingWindow.cs`
  - Replaced `ShowChatWindow()` completion launch with `ShowHub("chat")`.
  - Added diagnostic log: `[OnboardingWindow] OnWizardComplete launching HubWindow on chat tab`.
- `src\OpenClaw.Tray.WinUI\Pages\ChatPage.xaml.cs`
  - Wired `BootstrapMessageInjector.InjectAsync` into the Hub chat WebView2 `NavigationCompleted` success path, matching the standalone `ChatWindow` gated injection behavior.

## Validation
- Ran `./build.ps1` successfully after the code change.
- Per active session directive, did not run tests after the fix.

## Architectural notes
- Hub already exposes tag-based navigation through `NavigateTo("chat")`; `ShowHub("chat")` selects the existing NavigationView item and navigates to `ChatPage`.
- Bootstrap injection remains wired in both standalone `ChatWindow` and Hub `ChatPage`; the existing global `Settings.HasInjectedFirstRunBootstrap` gate ensures only one path injects.
