# Concept: Node Sandbox

The local preference for containing Windows node `system.run` commands.
It does not describe Gateway tools or prove that a particular command was contained.

## Composer presentation

- Icon: `FluentIconCatalog.Sandbox`, a Shield (U+EA18). It shares the protection
  metaphor with Permissions.
- On: `ChatSandboxOnBrush`, fixed blue (`#005FB8` in Light, `#60CDFF` in Dark),
  independent of the user's Windows accent color.
- Off: gray secondary text brush (`ChatSecondaryTextBrush`).
- High contrast: system theme colors take precedence.
- Accessible names: `Node Sandbox setting: On` and
  `Node Sandbox setting: Off`.
- Placement: right-hand action group, immediately before the microphone.
- Flyout heading: `Node Sandbox`, with a compact `On` or `Off` state.
- Action: `Sandbox settings`, a full-width native subtle button with a chevron.
- Localized titles follow the existing Sandbox page terminology; the action
  follows that locale's Settings navigation label rather than an English exemption.

The icon opens a native flyout, not a toggle. Its accessible name identifies the
saved setting. The 280-pixel flyout uses a padded header, one short caption,
and a separated settings action. On says "Commands on this Windows node run in a
sandbox." Off says "Commands on this Windows node run without a sandbox."
The enabled runtime always blocks if containment is unavailable; it never
retries on the host or silently clears the enabled preference. Opening the flyout
does not probe availability, save settings, or change execution policy.

## Owners

`ISettingsStore` supplies persisted snapshots. `ChatComposerSession` owns the
subscription; `ChatComposerViewModel` projects the sandbox preference.
`ReactorChatComposer` renders both Workspace and compact native chat.
Each host supplies the existing `sandbox` Companion Settings route.

The `Chat_Composer_Sandbox_*` resource keys own localized copy. The standard
Gateway WebView chat is outside this native composer surface.
