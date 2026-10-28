# Native chat visual system

Workspace's ChatPage uses native Reactor/WinUI controls. This
presentation follows the reading rhythm of the published 2026.9.4 web chat
(source `3a9d69db306cd7f081e06254cb89c4bcc14a7107`) without
replacing Windows chrome, native input, or gateway contracts.
The question dock geometry and working claw motion additionally follow the
October 2026 Control UI at `db2d951fd047a9e6a03b73d41c8e5757506dedf9`.
Backplates and accents stay native: WinUI owns the surfaces and Windows supplies
the accent color, rather than the web client's orange/red palette.
This source-derived presentation does not imply pixel-identical macOS text
rasterization, a copied user avatar or the web client's personalized background.

## Geometry and typography

`ChatVisuals` owns the shared geometry. The reading surface is capped at 768
pixels. Transcript prose is inset 16 pixels from the aligned composer edge.
Outer gutters are 12 pixels below 640 pixels and 40 pixels above it. The native
annotated rail overlays only the unused right gutter, leaving transcript and
composer centered on the viewport, including while the rail is focused.
The avatar is omitted below 960 pixels; at wider sizes its balanced side slots
leave prose centered. Accessible sender and footer identity remain.

The writing surface uses a 20-pixel corner radius, 16-pixel editor inset, and a
112-pixel minimum height. Message and code surfaces use 12-pixel corners.
These are chat surface tokens, not replacements for standard control radii.
General layout spacing is on the 4-pixel grid. The question dock preserves the
upstream 7/9/11/14-pixel option gaps, insets and typography instead.

Prose uses native body type at 14 pixels with 21-pixel line height. Headings
use 24/20/16 pixels for H1/H2/H3 and below. Top-level blocks use one uniform
16-pixel gap (the native 4-pixel-grid approximation of Web's 14 pixels), with
zero extra top/bottom block margins. List and quote interiors are unchanged.
Text/media stacks use that same gap. The transcript has a 16-pixel lead-in. Comparisons
normalize the released web header separately rather than adding fake app chrome.
Code remains literal and selectable, with native horizontal and vertical
scrolling rather than truncation. Its header exposes the supplied language and
the existing Copy action. The code body uses native Consolas at 13 pixels with
18-pixel line height, matching the web's line rhythm while compensating for
Consolas's smaller glyphs relative to the web's 12-pixel monospace face.
The language label uses the same family at 12 pixels. Max-height line stacking
allows larger or fallback glyphs to grow instead of clipping at text scales.
The frame keeps 12-pixel horizontal and 8-pixel vertical padding so the more
readable leading does not crowd compact conversations. Code text uses the
native secondary text brush for a calmer tone, with system window text in HC.

Additional shared glyph aliases live in `FluentIconCatalog`: `ChevronDown`
(E70D) and `Stop` (E71A).
Glyphs do not scale with text. Text containers keep native scaling enabled.
Chat uses dedicated `ChatSubmit` (Up, E74A) and `ChatAttach` (Add, E710)
aliases. Other Send glyph aliases are unchanged.

## Theme resources

`Themes/ChatResources.xaml` owns the following semantic aliases in Default
(Dark), Light, and HighContrast. Chat retains native WinUI surfaces, text and
Windows accent colors; the web reference supplies geometry and motion, not colors.

| Resource | Source / mapping |
|---|---|
| `ChatCanvasBrush` | Transparent over the original WinUI content layer; system window in HC |
| `ChatComposerBrush`, `ChatCardBrush` | `CardBackgroundFillColorDefault`; system window in HC |
| `ChatStrokeBrush`, `ChatQuestionCardStrokeBrush` | `ControlStrokeColorDefault`; system window text in HC |
| `ChatTextBrush`, `ChatStrongTextBrush` | `TextFillColorPrimary`; system window text in HC |
| `ChatSecondaryTextBrush` | `TextFillColorSecondary`; system window text in HC |
| `ChatClawBrush` | Light/dark Windows accent variants; system window text in HC |
| `ChatQuestionOptionBrush`, `ChatQuestionOptionHoverBrush` | Native default/secondary control fills; system window in HC |
| `ChatQuestionSelectedBrush`, `ChatQuestionSelectedStrokeBrush` | Windows accent tint and stroke; system window/highlight in HC |
| `ChatQuestionSubmitStyle` | Native accent fill and text-on-accent brushes, including system HC mappings |
| `ChatUserBrush` | Windows accent at low opacity; full system highlight in HC |
| `ChatPendingUserBrush` | Transparent in every theme, revealing the conversation background |
| `ChatUserTextBrush` | Primary text; system highlight text in HC |
| `ChatRichTextStyle` | Native rich-text Foreground binding to `ChatTextBrush` |
| `ChatCodeTextStyle` | Native code Foreground binding to `ChatSecondaryTextBrush` |
| `ChatSecondaryTextStyle` | Native progress/result TextBlock binding to `ChatSecondaryTextBrush` |
| `ChatQuestionCardStyle` | Native question Border bindings to card fill and question-specific stroke |
| `ChatCopySuccessBrush` | `SystemFillColorSuccess`; system window text in HC |
| `ChatPickerAccentBrush` | Light/dark system accent variants; system window text in HC |
| `ChatSandboxOnBrush` | Light/dark system accent variants; system window text in HC |

HC maps surfaces, strokes and text to system window/button/highlight colors.
An HC resource simulation is not evidence that Windows HC mode was enabled.
Progress text and question cards use native style setters rather than copying a
Reactor-resolved brush. This preserves element-local Light/Dark/HC theme lookup
and avoids light-theme colors on a dark question surface.

Window layering follows WinUI Gallery: the Workspace title bar and expanded navigation
pane reveal Mica, while `NavigationViewContentBackground` supplies one content
layer (`LayerFillColorDefaultBrush` in Light/Dark, system window in HC).
The embedded chat canvas is transparent so it neither hides nor doubles that
layer, including disconnected/loading states. The composer and code cards
use the card fill above that content layer.

## Controls and states

Pending agent questions replace the writing surface visually, not structurally.
The same mounted composer and unsent draft return when the question is collapsed
or resolved. Expanding the dock cancels voice capture and rejects late transcripts
without changing the unsent draft or attachments. Hidden composers cannot start
voice capture, including through host shortcuts. The dock uses a 20-pixel radius, 14-pixel padding, numbered full-width
native radio/checkbox options with descriptions, inline free text, a question
counter, collapse, request navigation, and right-aligned Skip/Submit actions.
One question is shown at a time. Single-choice selection advances, Back preserves
answers, digits 1-9 select outside text inputs, and Ctrl+Enter advances/submits.
Enter within free text inserts a line; radio arrow navigation selects without
advancing. Submission snapshots the current answers at invocation rather than
reusing an earlier render's values. The dock is bounded by `min(58% of viewport height, 576 pixels)` and
scrolls rather than clipping actions. Native automation peers and high-contrast
colors remain in use. The coordinator alone owns answer and cancellation RPCs.
A confirmed answer dismisses the dock without leaving a persistent success row.
Pending or unconfirmed questions and cancellation, expiry, and unavailable
feedback retain their existing presentation.

The 18-pixel working claw uses the upstream filled SVG paths, jaw pivot
`(8.6, 11)`, 2.4-second double-snip keyframes and CSS cubic-bezier timing.
All twelve seeded one-shot surprise motions retain their upstream order,
5% selection threshold and 4.8-second delay. `ChatWorkingClawMotion` is the pure
motion table; `NativeWorkingClaw` owns native rendering and its unload cleanup.
The row enters over 200 milliseconds and displays elapsed time plus known output
tokens. Phase remains in its accessible name without a per-second live-region
announcement. Elapsed units, fallback-model status and incomplete-stream recovery
copy use native localized resources. Shared recovery events carry a stable reason
code so the native mapper does not translate arbitrary Gateway messages.
Completed turns dismiss the working row; observed timing and usage
remain in timeline state, and late usage updates do not bring the row back.
Pending questions or approvals keep their waiting indicator until resolved.
OS reduced motion disables the moving presentation.

Queued submissions leave the textbox and remain after the current turn as normal
right-aligned user bubbles. A transparent `ChatPendingUserBrush`, subtle `ChatStrokeBrush`
outline, and slightly faded `ChatSecondaryTextBrush` distinguish them from sent messages without
reducing whole-bubble opacity. **Pending**, Copy, and Cancel appear underneath only
while the message is hovered or contains keyboard focus. The footer reserves its
space so revealing it never moves the message. Hidden actions do not accept
pointer taps, but remain keyboard-focusable. Accessible item status stays
available even when the visual footer is hidden.
Text remains selectable and attachment chips remain visible.
Cancel stays available until dispatch; failures keep their error and removal
action visible without hovering. Provider promotion replaces the pending row with the normal sent message,
without adding queue-only rows to history. This applies to Workspace's ChatPage.
See the [pending-message concept](design/reference/concepts/states/pending-chat-message.md).

Chat responses are not read aloud by default. Enabling the Text-to-speech
capability permits playback but does not enable automatic spoken replies.
Settings > Voice > "Read responses aloud" opts in to automatic playback;
manual "Read aloud" remains available independently when the capability is
enabled. Existing saved read-aloud preferences are preserved.

Welcome quick-start suggestions use borderless native buttons with a quiet gray
`ControlAltFillColorSecondaryBrush` backplate and the shared subtle toolbar
hover/pressed resources. Disabled and keyboard-focus states remain native.
Gateway dashboard management lives in a Connection settings card rather than
occupying a row above the chat surface. Settings' Chat rail action opens Workspace
without changing the selected Settings page or discarding the chat draft.

An authoritative empty conversation shows **Recent chats** instead of quick-start
suggestions when the active agent has eligible conversations. The list includes
active or token-bearing conversations, excludes the current session, background, archived and channel
sessions, and shows at most five chats by last update (session key breaks ties).
`ChatWelcomePresentation` owns this pure projection. Rows retain native button
focus/hover states and open the original session through the composer controller's
selection handoff, preserving the draft and notifying the host. No message is sent
by opening a recent chat. When none qualify, the upstream welcome suggestions appear.
Recent titles align left and truncate before a trailing last-used timestamp, matching
the web dashboard's row hierarchy. Relative ages use the existing localized time-ago resources;
the tooltip and accessible help expose the full local timestamp. Unknown times stay
empty rather than inventing activity.
A ready compose-only session can show this welcome state even when other sessions
exist; materialized sessions still wait for loaded, settled empty history.

The new-chat block is vertically centered within the content viewport: selected
agent name, new-session guidance, placement controls and composer, then recent
chats or starter prompts. It shares the 768-pixel reading width and responsive
gutters. A native ScrollViewer makes the entire block reachable at short heights
and large text scales. Existing transcripts keep the composer at the bottom.
The composer's parent and input control survive the transition to a transcript.
The editor and toolbar keep stable sibling keys when status, recording or
attachment rows appear above them, preserving native input, caret and picker identity.
The selected Gateway agent's `identity.name`, then `name`, then ID supplies the
heading through the placement catalog; a session title is never used as an agent name.
The agent avatar, name and guidance are horizontally centered. The avatar uses the
same `AgentIdentityBadge` image loader, emoji and initials fallback as the Workspace
agent picker, without its adjacent labels. It is 96 pixels, or 64 pixels at viewport
widths of 768 pixels and below. The existing `agents.list` response supplies the
selected agent's image and emoji, with no new request or copied identity.

Welcome guidance, the Recent chats label, and the four starter prompts are
bundled in the app's native `.resw` resources for all six supported languages.
They match the current web welcome copy, but Windows owns its presentation and
updates the strings with the app. No welcome RPC, catalog download, or Gateway
upgrade is required. Clicking a suggestion sends its displayed text through the
normal chat send path. Copy uses the app's startup UI language and the existing
Windows resource fallback, independently of the connected Gateway version.

An authoritative empty chat also shows **Environment** and **Workspace** pickers
above the writing surface. They use the same native toolbar chrome and searchable,
single-selection flyouts as the model picker. Labels shrink at narrow widths;
native selection, disabled reasons, keyboard focus, and system theme resources
remain intact, with computer and folder glyphs on the environment/workspace triggers and their
native menu rows. These decorative glyphs use `FluentIconCatalog` and do not replace
the controls' accessible names. The workspace picker offers the agent's default workspace,
registered projects, explicit Git URLs, and Gateway-side folder browsing. It never
interprets a Gateway path as a folder on the Companion machine.

An existing session's current location is not inferred from the agent's default
workspace. Until there is explicit local selection intent, its picker labels show
**Not verified**, with no selected flyout row. Sending without changing a picker
preserves the existing session location. Explicitly choosing even the displayed
default creates placement intent and runs preparation before sending. Genuinely
new compose-only targets may show the agent defaults.

`ChatPlacementController` owns each mounted host's local selection and cancellation
scope. Discovery and selection are read-only. On first send, `GatewayChatPlacementClient`
rechecks device availability, applies the workspace to the exact empty session key,
and, for remote hosts, creates a session-owned worktree and requires an active
dispatch acknowledgment before the existing chat send path runs. Preparation
errors remain visible and preserve draft/attachments. Navigation, disconnect,
or disposal fences late results; reconnect preserves explicit selection intent.
Unconfirmed remote placement is not retried or redirected locally. Review that
session in the Gateway before another placement attempt.
Losing empty-session eligibility cancels pending preparation before subsequent RPCs;
already admitted Gateway mutations are drained, not rolled back. Interrupted
preparation shows an explicit uncertainty notice. Each mounted host retains explicit
location intent and in-flight preparation by session across navigation, so returning
cannot silently display Local/default or skip placement preflight after an interruption.
Placement also requires history committed in the current connection generation;
cached transcripts remain visible after reconnect but cannot authorize preparation.
While history is unknown, explicit location intent and the draft are retained.
When an admitted preparation publishes its own new session, that same-key
materialization waits for fresh history rather than cancelling itself. The
existing operation rechecks authority before each subsequent mutation and before
sending. Nonempty history, changed session/connection, or disposal still stop it;
waiting never replays an admitted mutation. Obsolete history-wait notices clear
when current history arrives so location discovery can resume.
If history never becomes current, the wait expires after 120 seconds
(`OPENCLAW_CHAT_MATERIALIZATION_HISTORY_TIMEOUT_MS` overrides that bound),
releases the send operation, and retains the draft and uncertainty warning.
Timeouts and other non-cancellation failures after handing a mutation to the Gateway
preserve the same uncertainty notice as interruption. This is conservative when the
transport cannot distinguish a rejection from a lost response; it does not assert
rollback or duplicate allocation. Read-only discovery failures remain separate and
are no longer displayed once the empty-chat pickers disappear. Native resources
own recovery messages in every supported locale; raw exception details stay in
diagnostics rather than being rendered as untranslated guidance.
Folder selection uses the current successful directory result independently of
an earlier preparation warning. A new browse invalidates the previous directory,
so a failed request cannot leave a stale folder selectable.
A completed `/new` request cannot override a newer session or page navigation;
the successful session remains discoverable without pulling the user back into chat.

Workspace's sidebar owns session selection; the composer has no session picker.
The footer has a leading Attach action, model/reasoning selectors, and
quiet secondary actions beside the primary Send/Stop action. Every control stays
in one bottom row: model text shrinks rather than moving the selectors above
the actions. At viewport widths of 560 pixels or less, effort uses a gauge and
chevron with a 44-pixel target, retaining the full textual automation name and
tooltip. Attach, model, effort, Node Sandbox, voice and Send remain reachable in narrow
layouts through actual-width observation rather than a compact-window mode.
The [Node Sandbox icon](design/reference/concepts/states/node-sandbox.md) beside
the microphone in the right-hand action group uses the Windows accent when the latest SDK probe
supports containment and gray otherwise. It opens a compact native flyout with
Available, Compatibility, Unavailable, Checking, or Tools off status and a Sandbox
settings action, not a toggle. Compatibility warns about approved uncontained
execution on positively unsupported Windows. Failed/uncertain probes never imply
compatibility; the runner independently admits each command. This does not describe
Gateway tools or per-command results. Status and system-tools setting changes
refresh the mounted native composer without replacing drafts or attachments.
The compact effort trigger has a transparent idle background and the same
4-pixel interaction corners as the other toolbar controls. Its gauge and
chevron are vertically centered within the 44-pixel target. The shared toolbar
resource overrides survive resizing between text and icon layouts without
replacing native hover, pressed, disabled or keyboard-focus behavior. Picker anchor
capture preserves existing target mount callbacks.
There is no composer overflow menu. Voice preferences remain available in
Settings > Voice, where "Read responses aloud" controls the same shared
speaker mute state. Native focus, hover and disabled states remain.

The context-usage footer shows the latest assistant request measurement, not
the sum of overlapping requests. Newer session snapshots may lower that value,
including after compaction, but require an explicit positive total. A missing
total does not make input/output billing counters authoritative because they
may omit cached context. Cached seeding retains its existing fallback behavior.
Undated duplicates preserve the known measurement timestamp only when their
effective usage fields are unchanged, so matching older history cannot rewind
the footer. Changed undated usage does not inherit a measurement timestamp.

The compact effort gauge is an explicitly requested exception to the font-icon
convention: `ChatEffortGauge` renders the published web SVG geometry with native
WinUI paths and an ellipse, not a font approximation or WebView. It preserves the
24-unit viewbox, 20-pixel display size, 2-unit round strokes, dial opacity and
needle rotation from -120 to +120 degrees according to the advertised stop index.
Off and unanchored values use the minimum angle. An advertised inherited default
may inform the gauge without becoming a persisted override. The icon remains
decorative; the button keeps the textual accessible name and tooltip.
The source is pinned to 2026.9.4 and credited in `THIRD_PARTY_NOTICES.md`.

The model picker uses the standard native flyout presenter and an `AutoSuggestBox`
above one grouped, single-selection `ListView`. Native list-item templates own
hover, pressed, selected, selected-hover, disabled and keyboard-focus visuals,
including the selection indicator. Model labels use normal body typography,
metadata uses captions, and provider group headers use secondary captions.
`NativeChatModelPicker` owns that native content through Reactor's generated
control-wrapper API; `ChatModelPicker` owns its flyout lifetime. Highlighting a
row does not change the model until activation. Search submission commits only
a single unambiguous selectable result; otherwise it moves focus into the list.
Unrelated composer rerenders preserve list navigation, and reopening restores
the configured selection. The list has a bounded native scroll viewport.

All footer pickers keep top-end placement. Effort retains its custom-content
native flyout with 12-pixel corners and explicit compact padding, applying
presenter styles through native setters on mount and update.
Effort uses a native discrete slider over the
advertised stops, a textual heading/current value, and Faster/Smarter captions.
A single advertised stop uses a labeled button. The explicit Default reset
remains separate from the slider; neither opening the flyout nor its initial
value writes a setting. A Fast-mode switch is not displayed because that
workflow is not implemented by this composer. No decorative nonfunctional
web-only controls are added.
Native virtualization, scroll navigation and list lifetime remain with the
existing timeline and scroll-controller owners. The visual refresh does not
replace their navigation algorithm.
Pending attachments use a bounded horizontal rail with 56-pixel chips, 32-pixel
removal targets and room for focus visuals instead of growing the composer for
every file. Video-specific previews are not invented by the native file rail.

On history reload, message-wide structured user attachments appear on the first
nonempty text part only, not again on later text parts from that same message.
A textual media envelope retains precedence and prevents a later structured
fallback from repeating its chips. Matching cached captions preserve internal
line breaks and restore local filenames; already-flattened legacy captions
fall back to Gateway filenames. Reloaded descriptors do not restore image bytes.

The model flyout searches and groups only the existing catalog. Provider,
context and availability descriptions use `ChatModelLabels`. Provider headings
use the published web display names (for example, `github-copilot` becomes
`GitHub`), with a readable fallback for unknown IDs. Search accepts either the
display name or the original ID; grouping and wire identities keep the original ID.
The picker contains concrete catalog models only, with no Default/reset action.
An inherited model is shown by name when advertised, otherwise the trigger says
"Model". Opening or filtering the picker never changes the existing override.
The effort picker's separate Default reset and controller authorization paths
are unchanged.
No custom-ID, favorites, Fast, temperature or other gateway-backed setting is
invented.
Unknown nonempty thinking levels remain visible verbatim and do not check
Default. Reasoning choices come only from advertised session metadata, compatible
session defaults, or an exact provider/model catalog entry with no runtime conflict,
in that order. The first profile owns the choices, including explicit empty lists.
Structured IDs, labels and order are preserved; legacy `thinkingOptions` use
the gateway's public alias normalization. A `reasoning` boolean alone cannot
infer choices or veto an advertised mandatory profile.
Repeated profiles compare by their ordered options and default, not immutable-array
storage identity, so unchanged session/default/catalog metadata does not invalidate
the composer. Unknown and explicitly empty profiles remain distinct.

Without advertised choices the current value remains visible. A saved override
can still be cleared with Default; an already-default selector is read-only.
Default sends explicit JSON null, never an effective `thinkingDefault` value.
With no override, the slider and gauge show the advertised default's position,
while only the Default row is selected. Unknown explicit overrides are not
replaced by an inherited default.
Pointer previews commit only on release. Cancellation or capture loss while
pressed restores the current advertised selection without writing an override.
The native slider owns its value during the gesture, so unrelated snapshot
refreshes do not overwrite the active preview.
Deferred capture cleanup is gesture-fenced so it cannot cancel a newer input.
An authoritative session-list row that omits `thinkingLevel` clears the cached
override, while sparse activity events preserve it. Prepared profile metadata
may survive transient omissions only for the same identity and scope, and is
invalidated on a new connection. Model changes never replay the old override.
Model/reasoning changes require a connected, idle option-editing state;
the controller rechecks it for stale open-menu actions. Session browsing is not
disabled solely because the gateway is offline.
Context descriptions avoid repeating the group name.
The repository has a product brand mark but no
reusable provider-brand catalog, so providers retain accurate text rather than
invented logos.

The Web 9.4 retained-catalog warning/retry surface is deferred: the current chat
snapshot exposes model choices but no model-refresh failure state or explicit
refresh operation. Adding that surface requires a separate provider contract,
not view-owned network I/O.

Message and code Copy buttons use the same content/identity-fenced feedback
owner. A confirmed clipboard write shows a checkmark, announces Copied, and
resets after two seconds. Known clipboard access failures are logged and show
Copy failed instead. Repeated clicks restart the reset; content changes and
unmount cancel stale timers. Legacy `ClipboardHelper.CopyText` callers keep
their existing throwing behavior.
Code-copy automation IDs use `ChatCopy_code_<instance>` with a per-mount suffix,
so even identical snippets are independently addressable. The suffix survives
feedback and content rerenders, but is not a persistent conversation identifier.
Automation should discover the button in its code frame before retaining its ID.
Message-copy IDs remain based on the existing row identity.

Attachment, queue, recording, loading, thinking and error presentations use the
same resources and bounded layouts. Markdown sanitization, disabled HTML,
blocked link navigation, inert images, list measurement, timeline keys and
scroll-follow remain with their existing owners.
