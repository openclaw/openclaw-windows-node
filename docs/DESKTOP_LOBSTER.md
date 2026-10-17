# Desktop lobster

Enable **Settings > General > Desktop lobster** to keep the OpenClaw mascot on
the desktop while Companion runs. It is off by default. Drag it to move it,
right-click for options, or focus it and use Enter/F10 for its native menu.
**Hide desktop lobster** persists the off setting. Closing Workspace does not
close the lobster; exiting Companion does.

Hold the left mouse button on the lobster, move it, and release to reposition
the overlay. Its speech bubble follows it. Releasing a drag does not open the
menu; a normal click or right-click still does. Placement stays within the
monitor work area and is not saved across app restarts.

New app notifications, including Gateway notifications and `system.notify`,
appear as plain text in a speech bubble. Informational/success notifications
produce a happy hop, warnings an attentive expression, and errors a sad sigh.
The latest notification replaces the previous bubble rather than building a
second queue. The bubble clears after 15 seconds, pauses while hovered, and can
be dismissed immediately. Long messages scroll. **Open notifications** opens
the existing notification surface. Dismissing a bubble does not dismiss the
app notification. Existing Windows toasts and notification history are unchanged.

This opt-in exposes notification text outside the app, including during screen
sharing. Disable the lobster when that is inappropriate. **Show notifications**
off also clears and suppresses bubbles; enabling either toggle does not replay
old notifications. Existing upstream category filters continue to apply.
The Windows animation preference and high-contrast palette are inherited from
the shared mascot. Reduced motion keeps static expressions. No new network
requests, telemetry, artwork, or desktop/window enumeration are introduced.

## eSheep investigation

The [Store listing](https://apps.microsoft.com/detail/9mx2v0tqt6rm) points to the
modern eSheep/desktopPet project. The findings below describe its public source,
not a reverse-engineered or verified installed Store binary. Source was inspected
at [desktopPet e293330](https://github.com/Adrianotiger/desktopPet/tree/e2933305157a34818f47d238c74607593d81816d).

| Area | Verified source |
| --- | --- |
| Desktop renderer | C# WinForms, .NET Framework 4.8, `System.Drawing`, borderless `FormPet`; not a UWP-rendered pet |
| Transparency | Magenta `TransparencyKey`/background; no taskbar button; topmost desktop form |
| Animation engine | WinForms timer, sprite frames, XML start/end timing and motion interpolation, weighted next-state transitions, border/gravity conditions |
| Sprites | The default eSheep XML embeds a 16 by 11 tile sheet with magenta transparency |
| Store packaging | `UWPSheep.wapproj` names `DesktopPet.csproj` as entry point, packages the Win32 executable and references a separate UWP `OptionsWindow` project |
| Additional dependency | NAudio for sound support; unnecessary for this feature |

Sources:
[desktop project](https://github.com/Adrianotiger/desktopPet/blob/e2933305157a34818f47d238c74607593d81816d/src/DesktopPet.csproj),
[window implementation](https://github.com/Adrianotiger/desktopPet/blob/e2933305157a34818f47d238c74607593d81816d/src/dotNet/FormPet.cs),
[window configuration](https://github.com/Adrianotiger/desktopPet/blob/e2933305157a34818f47d238c74607593d81816d/src/dotNet/FormPet.Designer.cs),
[packaging](https://github.com/Adrianotiger/desktopPet/blob/e2933305157a34818f47d238c74607593d81816d/src/UWPSheep/UWPSheep.wapproj).

### Available eSheep animations

The [default pet definition](https://github.com/Adrianotiger/desktopPet/blob/e2933305157a34818f47d238c74607593d81816d/Pets/esheep64/animations.xml)
contains **54 states**, including transitions rather than 54 independent full
animations. These are the actual XML names, grouped for readability:

| Family | States |
| --- | --- |
| Walking and turning | `walk`, `rotate1a`, `rotate1b`, `walk_win2`, `walk_task2` |
| Running and bouncing | `run`, `run_begin`, `run_end`, `boing` |
| Dragging and falling | `drag`, `fall`, `fall fast`, `fall soft`, `fall hard`, `fall_wina`, `fall_winb`, `fall_winc`, `fall_wind` |
| Window-edge movement | `vertical_walk_up`, `vertical_walk_down`, `vertical_walk_over`, `top_walk`, `top_walk2`, `top_walk3`, `look_down` |
| Jumping | `jump`, `jump_down`, `jump_down2`, `jump_down3` |
| Sleeping | `sleep1a`, `sleep1b`, `sleep2a`, `sleep2b`, `sleep3a`, `sleep3b` |
| Bathing | `batha`, `bathb`, `bathc`, `bathd`, `bathw`, `bathz` |
| Eating and novelty behaviors | `eat`, `flower`, `pissa`, `pissb`, `kill`, `sync` |
| Black-sheep sequence | `blacksheepa`, `blacksheepb`, `blacksheepc`, `blacksheepv`, `blacksheepw`, `blacksheepy`, `blacksheepz` |

## WinUI Reactor implementation

OpenClaw already ships the Mac mascot as retained WinUI vector shapes in
`OnboardingMascot`, with the attribution in `Assets/Setup/Mascot-NOTICE.txt`.
It supplies nine moods (idle, curious, thinking, working, happy, celebrating,
sad, sleepy, attentive), sixteen gesture clips, blinking, pointer gaze,
tap reactions, idle sleep, particles and accessories. Its 12 fps animation
budget and reduced-motion/hidden-control behavior are reused without a second
animator or a raster conversion.

`DesktopCompanionSurface` declares the mascot, speech bubble, literal notification
text and actions in C# using the app's existing Microsoft.UI.Reactor preview.12.
One `ReactorHostControl` reconciles updates from its presentation state.
A Reactor `XamlHostElement` retains the exact setup control and animator across
updates. Only notification arrivals restart its
entrance; theme/layout renders do not. Closing the window disposes the Reactor
host, unsubscribes presentation updates, and disables the retained animator.
No companion XAML or separate animation framework is required.

`DesktopCompanionWindow` remains the native window/input adapter using the
existing WinUIEx `TransparentTintBackdrop`. An undecorated topmost `AppWindow` is shown without
activation and excluded from taskbar/Alt+Tab. A native region excludes the
unused bubble area from hit testing, so a transparent 360 by 420 DIP canvas
does not block the desktop underneath. Placement uses physical monitor work
areas, DPI-aware sizing, drag-and-drop, and display-change clamping.
The companion explicitly disables DWM non-client rendering, rounded window
corners and the outer border. Hiding the presenter title bar alone is insufficient:
Windows can otherwise draw a rectangular outline and shadow around the transparent
canvas. A window-scoped `WM_NCCALCSIZE` handler makes the entire HWND client area,
preventing the classic border that the presenter's retained dialog-frame styles
would otherwise paint. The handler is disposed with the window. The speech
bubble's own rounded corners and the mascot's glow remain.

The input adapter observes handled pointer events because the native WinUI
`Button` consumes them before ordinary event subscriptions run. It retains the
button's pointer capture and accessibility behavior, clears dragging when capture
is lost or canceled, and suppresses only the drag gesture's click.

`DesktopCompanionController` owns the opt-in lifetime and observes the canonical
`AppNotificationService` plus `ISettingsStore`. `DesktopCompanionState` recognizes
new/coalesced arrivals without replaying selection, dismissal, startup backlog,
or stale dispatched snapshots. App remains only the composition/lifecycle owner.
Notification content is never parsed as markup or executable actions.

This is a Clippy-like stationary companion, not an eSheep physics engine.
It does not walk across other windows, collide with window borders, throw
itself around the desktop, persist its position across app restarts, or start
independently of Companion. Those features are separate future scope.

## Validation

Run the required `build.ps1`, Shared.Tests and Tray.Tests closeout plus focused
`DesktopCompanionWindowTests` in Tray.UITests on the host's native architecture,
including Reactor reconciliation, retained mascot identity and close/unmount.
Use `run-app-local.ps1 -Isolated -AllowNonMain` for the real app. Enable the
desktop lobster, select **Preview notification** from its menu, and send a
real `system.notify` through an isolated local MCP endpoint. Confirm happy
arrival, readable text, dismiss/timeout, drag, hide/re-enable, and no foreground
activation on arrival. No Gateway is necessary for the local notification path;
Gateway notification delivery needs a connected Gateway for end-to-end proof.

On an interactive desktop, set `OPENCLAW_DESKTOP_COMPANION_POINTER_PROOF=1` for
the focused UI tests to also exercise real mouse input: repeated dragging with
and without a bubble, release without a menu, capture-loss cancellation, and
subsequent accessible menu invocation. These opt-in tests move the mouse only
over the test-owned overlay and restore its original position afterward.
