# OpenClaw Setup — V2 (redesigned onboarding flow)

Status: **mounted in the app + service-wired**. Light / dark / system theme
support landed. Remaining items (Wizard provider/model fold; legacy page
deletion; real translations) are itemised below as the follow-up backlog.

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

## Cutover (this PR)

The V2 flow is mounted in the live app via `OnboardingWindow`. Setting
`OPENCLAW_USE_V2_SETUP=0` (or starting at `OnboardingRoute.Connection`
through the existing env-var override) falls back to the legacy flow as
a kill-switch for one release cycle.

Service wiring is centralised in
[`OnboardingV2Bridge`](../src/OpenClaw.Tray.WinUI/Onboarding/V2/OnboardingV2Bridge.cs):

| Real service | V2 state field | Notes |
| --- | --- | --- |
| `LocalGatewaySetupEngine.StateChanged` | `LocalSetupRows`, `LocalSetupErrorMessage` | Re-uses `LocalSetupProgressStageMap` so V2 picks up every legacy bug-fix. |
| `PermissionChecker.CheckAllAsync` + `SubscribeToAccessChanges` | `Permissions` | Snapshot list of `PermissionRowSnapshot`. Marshals back to UI thread. |
| `SettingsManager.GetEffectiveGatewayUrl` | `GatewayUrl` | Flips `ws://` → `http://` for the browser-launch link. |
| `SettingsManager.AutoStart` ↔ `LaunchAtStartup` | `LaunchAtStartup` | Two-way: initial value from settings; toggle change calls `_settings.Save()`. |
| `Settings.EnableNodeMode` | `NodeModeActive` | Seeded once at construction (the legacy app doesn't mutate it post-onboarding). |

Threading: every cross-thread mutation marshals through
`DispatcherQueue.TryEnqueue`. The V2 state's `StateChanged` event fires
on the UI thread, bumping a render tick in
[`OnboardingV2App.UseEffect`](../src/OpenClawTray.OnboardingV2/OnboardingV2App.cs).

Completion: `OnboardingWindow.TryCompleteOnboarding` now treats
`V2Route.AllSet` as a "finished from Ready" terminus (in addition to the
legacy `OnboardingRoute.Ready`). The bridge's `Finished` event closes
the window, which routes through the same completion logic legacy used —
persisting `Settings.AutoStart` via `AutoStartManager`, firing
`OnboardingCompleted`, and launching `HubWindow` on the chat tab when
setup is complete.

Advanced setup: Welcome's "Advanced setup" link raises
`OnboardingV2State.AdvancedSetupRequested`; `OnboardingWindow` catches
it via the bridge and **re-mounts the same `FunctionalHostControl`** onto
the legacy `OnboardingApp` at `OnboardingRoute.Connection`. No second
window is opened. The legacy `OnboardingState` object was constructed
up-front so it's available for this swap.

## Follow-up backlog

The cutover deliberately scopes down to the items below to keep this PR
reviewable. None are blocking — V2 works end-to-end without them.

1. **Fold the legacy `WizardPage` (provider/model picker) into V2
   `GatewayWelcomePage`.** Today V2 Gateway shows the welcome card +
   "Open localhost:18789 in browser" link, which routes the user to the
   real gateway web UI for provider setup. The legacy `WizardPage`
   in-process picker (612 lines of RPC-driven UI) is currently
   unreachable from V2; a follow-up should either port it as a V2
   component or surface it via a "Configure providers" CTA that hosts
   the legacy page inside the V2 Gateway card.
2. **Real translations for V2_* keys.** `tools/seed_v2_resw.py` seeds
   every V2_* key into all five `.resw` locales with the English value
   and the `Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales`
   test is taught (via a `key.StartsWith("V2_")` predicate) that they
   are intentionally English-only at first ship. Translations land in
   a follow-up by replacing each non-en-us value.
3. **Delete unused legacy pages.** With V2 mounted by default, these
   pages are only reachable via the kill-switch:
   `Welcome.cs`, `SetupWarning.cs`, `WizardPage.cs`,
   `LocalSetupProgressPage.cs`, `PermissionsPage.cs`, `Ready.cs`,
   `ChatPage.cs`. `ConnectionPage` stays (Advanced flow target).
   Deleting them requires test refactoring (e.g.,
   `OnboardingWizardPage_*` test files reference the legacy
   component). Scope as its own PR.
4. **Replace the kill-switch with a real removal.** Once #3 lands and
   we've shipped one release cycle on V2, drop the
   `OPENCLAW_USE_V2_SETUP=0` branch and the legacy
   `OnboardingApp` mount path.
5. **Welcome page's `RequestAdvancedSetup` should round-trip back to
   V2 when the legacy Connection page completes.** Today the user is
   left in legacy after Advanced; we should bridge `OnboardingState`
   completion back into `V2State.Finished` so the AllSet page can
   still display.

## Cutover plan (historical — superseded by "Cutover (this PR)" above)

The rubber-duck critique on the V2 design surfaced five blocking items
that need to be solved together at cutover time. Tackling them in this
order minimises risk:

### 1. Mount V2App from `OnboardingWindow`

`src/OpenClaw.Tray.WinUI/OnboardingWindow.cs` currently mounts the
legacy `OnboardingApp`. Cutover swaps the mount expression and rewires
the host->state bridge:

- Construct an `OnboardingV2State` (no longer the legacy
  `OnboardingState`). Subscribe `Window.DispatcherQueue` to
  `state.StateChanged` so service mutations from background threads
  marshal to the UI thread before the V2 render tick fires.
- Translate legacy bootstrap routes (`Welcome`, `Wizard`, `Permissions`,
  `Connection`, `Ready`) into V2 routes
  (`Welcome`, `LocalSetupProgress`, `GatewayWelcome`, `Permissions`,
  `AllSet`).
- Remove the legacy `OnboardingApp` mount once the V2 path is the only
  path; do not feature-flag both UIs in production.

### 2. Decide the fate of the Wizard step

Legacy onboarding has a "Wizard" route that handles provider/model
choice. The V2 designs do not include this step. Choices:

a. **Drop it** if `GatewayWelcome` already conveys the intent
   (recommended — the design implies "we picked sensible defaults; here
   is your gateway").
b. **Re-introduce it** as a sixth V2 page styled to match. This
   requires a new design and shouldn't block the cutover.

Document the decision in the cutover PR and update
`OnboardingV2App.PageOrder` accordingly.

### 3. Wire `LocalSetupProgress` to the real stage map

Today the V2 `LocalSetupProgressPage` consumes a self-contained enum
defined inside the page. Cutover should:

- Re-use the existing `LocalSetupProgressStageMap` (whatever the legacy
  flow uses) so V2 picks up the same bug-for-bug semantics. The risk
  of forking is regressing fixes that already landed against the legacy
  enum.
- Source the failure card text from the existing failure-message catalog
  (do not duplicate strings).
- Mirror legacy "Try again" semantics: re-run the same stage that
  failed, not a hard-coded restart.

### 4. Wire "Advanced setup" link

The Welcome page's `Advanced setup` link is currently a no-op. Cutover
should route it to the legacy Connection page (or its V2 equivalent if
one is added).

### 5. Re-map completion

Legacy `OnboardingWindow.TryCompleteOnboarding()` keys off the legacy
`OnboardingRoute.Ready` value. Cutover changes that to
`V2Route.AllSet` AND wires the Finish button to also persist
`Settings.AutoStart` from the launch-at-startup toggle (currently the
toggle only mutates `OnboardingV2State.LaunchAtStartup` — the new field
added in the hardening commit).

### State plumbing for V2

Connect each legacy domain event to the corresponding V2 setter so the
`StateChanged` event we just added drives re-renders:

| Legacy signal | V2 setter to call |
| --- | --- |
| local-setup stage update | `OnboardingV2State.ProgressStage = ...` |
| local-setup failure | `OnboardingV2State.ProgressFailedStage = ...` |
| gateway probe result | `OnboardingV2State.GatewayUrl = ...; OnboardingV2State.GatewayHealthy = ...` |
| permissions snapshot | `OnboardingV2State.Permissions = snapshotList` (use `PermissionRowSnapshot`) |
| node-mode toggle | `OnboardingV2State.NodeModeActive = ...` |
| launch-at-startup pref | `OnboardingV2State.LaunchAtStartup = ...` (two-way: also write `Settings.AutoStart`) |

Every setter raises `StateChanged`, the V2 app's `UseEffect` bumps a
render tick, and FunctionalUI re-renders the active page.

### Localization

Adding V2 to the .resw set requires *all five* locales to add the keys
listed in `V2Strings.DefaultEnUs`. The localization parity tests in
`tests/OpenClaw.Tray.Tests/LocalizationValidationTests.cs` enforce this:

1. Add every V2 key with the English value to all five `.resw` files
   (the `Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales` test
   accepts "all locales seeded with English" via the
   `InvariantOrDeferredResourceKeys` allowlist).
2. Add each V2 key to that allowlist if it is intentionally
   English-only at first ship.
3. Switch `V2Strings.Resolver` to point at the .resw lookup so live
   strings come from resources rather than the bundled fallback.

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
