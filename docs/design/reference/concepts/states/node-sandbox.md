# Concept: Node Sandbox

The Windows node's observed SDK sandbox availability, with a shortcut to its
permissions. It does not describe Gateway tools or certify an individual command.

## Composer presentation

- Icon: `FluentIconCatalog.Sandbox`, a Shield (U+EA18). It shares the protection
  metaphor with Permissions.
- Available: `ChatSandboxOnBrush`, fixed blue (`#005FB8` in Light, `#60CDFF` in Dark),
  independent of the user's Windows accent color.
- Other states: gray secondary text brush (`ChatSecondaryTextBrush`).
- High contrast: system theme colors take precedence.
- Accessible name: localized `Node Sandbox` plus the current state.
- Placement: right-hand action group, immediately before the microphone.
- Flyout heading: `Node Sandbox`, with `Available`, `Compatibility`, `Unavailable`,
  `Checking`, or `Tools off`.
- Action: `Sandbox settings`, a full-width native subtle button with a chevron.
- Localized titles follow the existing Sandbox page terminology; the action
  follows that locale's Settings navigation label rather than an English exemption.

The icon opens a native flyout, not a toggle. The 280-pixel flyout uses a padded
header, one short caption, and a separated settings action. Available means the
last SDK check supports containment, not that a particular command succeeded.
Compatibility explicitly warns that positively unsupported Windows runs approved
commands without containment. Unavailable means an uncertain or failed probe, not
permission to downgrade. The system-tools kill switch takes precedence.
Opening the flyout does not probe, save settings, or change execution policy.

## Owners

`SandboxStatusStore` publishes results from the existing app and Sandbox page
probes; it performs no IO or authorization. `ISettingsStore` supplies the
system-tools setting. `ChatComposerSession` owns both subscriptions;
`ChatComposerViewModel` projects version-fenced status snapshots.
`ReactorChatComposer` renders both Workspace and compact native chat.
Each host supplies the existing `sandbox` Companion Settings route.

The `Chat_Composer_Sandbox_*` resource keys own localized copy. The standard
Gateway WebView chat is outside this native composer surface.
