# OpenClaw Setup — V2 (redesigned onboarding flow)

Status: **the only setup UI shell in the app**. Setup is now scoped to
installing a new app-owned local WSL gateway. Existing and remote gateway
management lives in the tray app's Connections tab.

## Where the V2 code lives

| Project / file | Role |
| --- | --- |
| `src/OpenClawTray.OnboardingV2/` | New class library: state, app shell, page components, animations, V2Strings. Builds against `OpenClawTray.FunctionalUI` (the "minimal Reactor"). |
| `src/OpenClawTray.OnboardingV2/Pages/` | One file per page: `WelcomePage.cs`, `LocalSetupProgressPage.cs`, `GatewayWelcomePage.cs`, `PermissionsPage.cs`, `AllSetPage.cs`. |
| `src/OpenClawTray.OnboardingV2/Animations.cs` | Composition-animation helpers (`WithEntranceFadeIn`, `WithEntrancePopIn`, `WithSlideInFromBelow`, `WithBreathe`). All gated on `ShouldAnimate` (false in capture mode and when `UISettings.AnimationsEnabled == false`). |
| `src/OpenClawTray.OnboardingV2/V2Strings.cs` | Resource-key dictionary + settable `Resolver` delegate. `Get(key)` falls back to the bundled English values when the resolver returns null/empty/echoes the key. |
| `src/OpenClawTray.OnboardingV2/OnboardingV2State.cs` | Mutable shared state with property setters that raise `StateChanged` (the V2 app subscribes and bumps a render tick). Includes cutover-staged shape: `GatewayUrl`, `GatewayHealthy`, `LaunchAtStartup`, `Permissions` (`IReadOnlyList<PermissionRowSnapshot>?`), `PermissionRowSnapshot`, `PermissionSeverity`. |
| `src/OpenClawTray.OnboardingV2/OnboardingV2App.cs` | Root `Component<OnboardingV2State>`. Owns the page area + nav bar (Back / Next-or-Finish + dot indicator). Welcome has no chrome by design. |
| `src/OpenClaw.SetupPreview/` | Standalone WinUI 3 unpackaged exe used for the inner-loop. Mounts the V2 tree against a fake `OnboardingV2State`. Reads env vars to switch page / scenario / locale and to enter capture mode (`OPENCLAW_PREVIEW_CAPTURE=1`). |
| `tools/v2_visual_diff.py` | Renders side-by-side `expected | actual` PNGs by spawning the SetupPreview exe in capture mode for each page scenario. The agent then *views* those PNGs and judges visual parity. |
| `tools/v2-design-refs/Dialog{,-1..-6}.png` | Designer source of truth (committed). |

## How the inner loop works

```
edit V2 page  →  python tools/v2_visual_diff.py --pages welcome
              →  view out/v2-visual/welcome/diff.png
              →  iterate
```

`v2_visual_diff.py` does an incremental `dotnet build` of
`OpenClaw.SetupPreview` once per invocation (cached so `--all` only
builds once). Each page render takes ~2-3 s on a warm tree.

Page scenarios baked into `PAGES` in `tools/v2_visual_diff.py`:

| Key | Page | Notes |
| --- | --- | --- |
| `welcome` | Welcome | `Dialog.png` — no chrome, lobster + CTA + Advanced setup. |
| `progress-running` | LocalSetupProgress (in-flight) | `Dialog-1.png` — running stage card. |
| `progress-failed` | LocalSetupProgress (failure) | `Dialog-6.png` — pink Try-again card slides in. |
| `gateway` | GatewayWelcome | `Dialog-2.png` — gateway URL + health checkmark. |
| `permissions` | Permissions | `Dialog-5.png` — 5 capability rows + Refresh status. |
| `allset` | AllSet (node-mode active) | `Dialog-4.png` — amber Node-Mode card + Launch toggle. |
| `allset-no-node` | AllSet (node-mode off) | Variant — no amber card. |

## Cutover

The V2 flow is mounted in the live app via `OnboardingWindow`. The old
standalone/fallback v1 setup shell and pages have been removed. The
provider/model setup experience is hosted by the explicit tray-side
`GatewayWizardPage` / `GatewayWizardState` pair and embedded inside V2's
`GatewayWelcomePage`.

Service wiring is centralised in
[`OnboardingV2Bridge`](../src/OpenClaw.Tray.WinUI/Onboarding/V2/OnboardingV2Bridge.cs):

| Real service | V2 state field | Notes |
| --- | --- | --- |
| `LocalGatewaySetupEngine.StateChanged` | `LocalSetupRows`, `LocalSetupErrorMessage` | Re-uses `LocalSetupProgressStageMap` so setup stage behavior stays consistent. |
| `PermissionChecker.CheckAllAsync` + `SubscribeToAccessChanges` | `Permissions` | Snapshot list of `PermissionRowSnapshot`. Marshals back to UI thread. |
| `SettingsManager.GetEffectiveGatewayUrl` | `GatewayUrl` | Flips `ws://` → `http://` for the browser-launch link. |
| `SettingsManager.AutoStart` ↔ `LaunchAtStartup` | `LaunchAtStartup` | Two-way: initial value from settings; toggle change calls `_settings.Save()`. |
| `Settings.EnableNodeMode` | `NodeModeActive` | Seeded once at construction. |
| `GatewayRegistry` + WSL distro probe | `ExistingGateway` | Drives Welcome CTA/warning behavior for none, app-owned local WSL, and external-only connections. |

Threading: every cross-thread mutation marshals through
`DispatcherQueue.TryEnqueue`. The V2 state's `StateChanged` event fires
on the UI thread, bumping a render tick in
[`OnboardingV2App.UseEffect`](../src/OpenClawTray.OnboardingV2/OnboardingV2App.cs).

Completion: `OnboardingWindow.TryCompleteOnboarding` treats
`V2Route.AllSet` as the terminal setup page. The bridge's `Finished` event
closes the window, which routes through the shared completion logic —
persisting `Settings.AutoStart` via `AutoStartManager`, firing
`OnboardingCompleted`, and launching `HubWindow` on the chat tab when
setup is complete.

Advanced setup: Welcome's "Advanced setup" link raises
`OnboardingV2State.AdvancedSetupRequested`; `OnboardingWindow` closes setup
without completing it and opens `HubWindow` on the Connections tab. Users
connect to existing, remote, or manual gateways there.

Existing connections: first-run setup no longer opens automatically when
there is any usable saved gateway connection. Users can intentionally start
local setup from the Connections tab via **Install new WSL Gateway**.

Welcome CTA/warnings:

1. No existing gateway: primary CTA stays **Set up locally**.
2. Existing app-owned WSL gateway: primary CTA becomes **Install new WSL Gateway**; confirmation warns that the current OpenClaw WSL gateway and `OpenClawGateway` distro will be deleted before a fresh install.
3. External-only gateway: primary CTA stays **Set up locally**; confirmation says a new local WSL gateway will be installed and connected, while the external gateway remains available in Connections.

## Follow-up backlog

The cutover deliberately scopes down to the items below to keep this PR
reviewable. None are blocking — V2 works end-to-end without them.

1. **Restyle the gateway wizard to native V2 UI.** The current
   `GatewayWizardPage` is no longer part of the v1 shell, but it still owns
   its own card/buttons while the gateway-driven provider/model flow remains
   embedded in V2.
2. **Real translations for V2_* keys.** `tools/seed_v2_resw.py` seeds
   every V2_* key into all five `.resw` locales with the English value
   and the `Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales`
   test is taught (via a `key.StartsWith("V2_")` predicate) that they
   are intentionally English-only at first ship. Translations land in
   a follow-up by replacing each non-en-us value.

## Animation discipline

- All animations live in `src/OpenClawTray.OnboardingV2/Animations.cs`.
  Pages opt in via extension methods (`element.WithEntranceFadeIn(...)`).
- Pages must call `ElementCompositionPreview.SetIsTranslationEnabled(fe, true)`
  before animating `Translation`. The helper does this for you.
- The `ShouldAnimate` predicate gates every helper. It returns `false`
  if `V2Animations.DisableForCapture` is set OR if
  `Windows.UI.ViewManagement.UISettings.AnimationsEnabled` is `false`
  (Windows reduce-motion). The SetupPreview sets `DisableForCapture` in
  capture mode so screenshots are deterministic.
- Don't add page-local `Composition` calls; extend `Animations.cs` so
  the gating stays centralised.

## Accessibility checklist

- [x] Re-enabled WinUI's system focus visuals (cyan ring) on every V2
      button by removing `UseSystemFocusVisuals = false`.
- [x] Stable `AutomationProperties.AutomationId` on Back / Next /
      Finish nav buttons and on every page CTA.
- [x] `AutomationProperties.Name` on the AllSet launch-at-startup
      `ToggleSwitch` (which uses empty `OnContent`/`OffContent` so the
      "On" label can render to the toggle's left, matching the design).
- [x] `AutomationProperties.Name` on the custom title bar, the lobster
      icon, and the title text.
- [ ] Keyboard nav verified end-to-end against the live UI (cutover
      gate — capture mode skips animation, we should manually confirm
      tab order in interactive mode).
- [ ] Screen reader smoke-test (Narrator + NVDA) at cutover.

## Visual validation

`python tools/v2_visual_diff.py --all` regenerates side-by-side
PNGs under `out/v2-visual/<page>/diff.png`. A human (or the agent
running this codebase) opens those PNGs and judges parity directly
against the designer references in `tools/v2-design-refs/`. We
intentionally do not pixel-diff — small, intentional rendering
differences (DPI scaling, font hinting, drop shadows) would dominate
the signal.

When running visual validation:

1. Render all pages: `python tools/v2_visual_diff.py --all`.
2. View each `diff.png` and note any discrepancies in:
   - Layout / spacing / alignment
   - Typography (size, weight)
   - Color (especially accent cyan, accent green, error pink, amber
     warning)
   - Iconography (asset / size / position)
   - Copy (the V2Strings dictionary holds the source of truth — the
     designer mocks contain a couple of typos we intentionally fixed:
     `localhost18789` → `http://localhost:18789`, `Stays` → `stays`,
     and `Acvtive` → `Active`).
3. If any discrepancy matters, edit the relevant page, re-render, and
   visually re-check until parity is restored.
