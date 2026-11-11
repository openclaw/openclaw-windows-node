# Native chat visual system

Workspace's ChatPage uses native Reactor/WinUI controls. This
presentation follows the reading rhythm of the published 2026.9.4 web chat
(source `3a9d69db306cd7f081e06254cb89c4bcc14a7107`) without
replacing Windows chrome, native input, or gateway contracts.
The question dock geometry and working claw motion additionally follow the
October 2026 Control UI at `db2d951fd047a9e6a03b73d41c8e5757506dedf9`.
Running-label animation and message-footer disclosure were rechecked against
Control UI `be1e36290247b257945988fb873cd3ab00cc05fd` on November 8, 2026.
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
96-pixel minimum height. Its vertical padding is 8 pixels and the editor-to-toolbar
gap is 4 pixels. The editor retains its 44-pixel minimum height and grows with
multiline input; action targets and text sizes are unchanged. Message surfaces
use 12-pixel corners and code cards use the web's 10-pixel corners.
These are chat surface tokens, not replacements for standard control radii.
General layout spacing is on the 4-pixel grid. The question dock preserves the
upstream 7/9/11/14-pixel option gaps, insets and typography instead.

Prose uses native body type at 14 pixels with 21-pixel line height. Headings
use 24/20/16 pixels for H1/H2/H3 and below. Top-level blocks follow the web's
`--chat-block-gap`: one uniform 14-pixel gap (`ChatVisuals.MessageBlockGap`),
with zero extra top/bottom block margins, except that a heading after another
block adds 7 pixels for the web's 1.5x heading lead-in. Prose, tight list items
and table cells use the softer web text tone (`ChatProseRichTextStyle`); headings
and bold runs use the strong tone. List markers share the prose ramp, so each
marker sits on its first line's baseline in tight and loose lists, and occupy a
right-aligned 20-pixel column so item text starts 24 pixels in, as on the web.
Bullets use the muted tone and numbers the prose tone. Items are 6 pixels apart
(the web's 0.4em). Quote paragraphs follow the paragraph ramp. Table cells keep
the Markdown control's 14-pixel metrics with a bold header, where the web uses
13 pixels. Thematic breaks are a 1-pixel native divider. Inline code keeps its
surrounding tone on a subtle fill chip; WinUI text highlighters cannot draw the
web chip's border or padding. Bold, inline-code and syntax highlighter brushes
are re-resolved when the element's theme changes.
Text/media stacks use that same gap. The transcript has an 8-pixel lead-in.
Assistant turns use 4-pixel outer spacing without extra body padding. User
bubbles, thinking blocks and tool details use 12-pixel horizontal and 8-pixel
vertical insets. Related labels, details and action footers use one 4-pixel gap
rather than accumulating stack spacing and margins. Comparisons
normalize the released web header separately rather than adding fake app chrome.
Code remains literal and selectable, with native horizontal and vertical
scrolling rather than truncation. The card follows the web `.code-block-wrapper`:
a 1-pixel stroke over the existing native card fill, a header with the lowercase language
(or the localized `code` fallback) and the existing Copy action at 8/8/4/14-pixel
insets, and code 14 pixels from the side and bottom edges. The code body uses
native Consolas at 13 pixels with
18-pixel line height, matching the web's line rhythm while compensating for
Consolas's smaller glyphs relative to the web's 12-pixel monospace face.
The language label uses the same family at 12 pixels. Max-height line stacking
allows larger or fallback glyphs to grow instead of clipping at text scales.
Code text uses the prose tone, with system window text in HC.
`ChatCodeSyntax` classifies common fence languages (TypeScript/JavaScript, JSON,
C#, C-family, Python, PowerShell, shell, YAML, TOML, SQL, CSS and XML/HTML) into
the web's highlight.js roles. `ChatMarkdownPresentation.ApplyCodeSyntax` colors
them with theme-resolved text highlighters over Reactor's single literal run, so
reconcile never rebuilds unchanged code and selection survives re-renders. Colors
refresh on load and theme change and are cleared on unmount, before Reactor pools
the control. Comments use the muted color without italics. Unknown languages,
blocks over 32,000 characters or 4,000 colored spans, and High Contrast stay plain.
Compact thinking text keeps its tighter 8-pixel block and 2-pixel list rhythm.

Chat text sizes are pinned by `ReactorChatLayoutProofTests.FontContract.cs`,
`ReactorMarkdownReadingRampProofTests.cs` and the Worked header assertions in
`ReactorChatLayoutProofTests.WorkAlignment.cs`. The web column is the pinned
`ui/src/styles/chat` CSS at `b896457970bfa1b2cef23ea16f3731ba73436b0c`, which
agrees with the 2026.9.4 and October builds. Reading content follows the web;
chrome keeps the native WinUI ramp.

| Role | Native (px) | Web (px) | Note |
|---|---|---|---|
| Prose, user bubble, quote, list item | 14/21 | 14/21 | Markers share the first-line baseline |
| Headings H1, H2, H3 and below | 24/20/16, bold | 1.65, 1.35, 1.15em; H4 to H6 1em | One three-step ramp |
| Code block, language label | Consolas 13/18, 12 | mono 12 | Consolas glyphs are smaller |
| Table cell | 14, bold header | 13, 600 header | Markdown control metrics kept |
| Thinking text, lists, headings | 12/18, secondary | 12, muted | Headings differ by weight only |
| Tool and activity title, inline status, nested label | 13 | 13 | `ChatVisuals.ToolRowFontSize` |
| Tool input and result code | Consolas 12 | mono 11 to 12 | |
| Tool card outcome | 11 | 11 | Section names are accessible labels, not visible headings |
| Row glyph, hover chevron | 14, 12 | 14, 14 | Fluent glyph sizes |
| Footer, hover metadata, status, error | 12/18 | 12; error 13 | Error stays on the status ramp |
| Permission card | 14 title, 12/18 body and decisions, 11 caption | not compared | Native card chrome |

The pinned provider-review and child-activity panels keep stock WinUI body and
caption sizes as native chrome.

Additional shared glyph aliases live in `FluentIconCatalog`: `ChevronDown`
(E70D) and `Stop` (E71A).
Glyphs do not scale with text. Text containers keep native scaling enabled.
Chat uses dedicated `ChatSubmit` (Up, E74A) and `ChatAttach` (Add, E710)
aliases. Other Send glyph aliases are unchanged.

## Theme resources

`Themes/ChatResources.xaml` owns the following semantic aliases in Default
(Dark), Light, and HighContrast. Chat retains native WinUI surfaces and Windows
accent colors. Reading tones follow the web (softer prose, strong bold and
headings, muted bullets) through native text brushes, and code syntax uses the
web's highlight.js palette.

| Resource | Source / mapping |
|---|---|
| `ChatCanvasBrush` | Transparent over the original WinUI content layer; system window in HC |
| `ChatComposerBrush`, `ChatCardBrush` | `CardBackgroundFillColorDefault`; system window in HC |
| `ChatStrokeBrush`, `ChatQuestionCardStrokeBrush` | `ControlStrokeColorDefault`; system window text in HC |
| `ChatTextBrush`, `ChatStrongTextBrush` | `TextFillColorPrimary`; system window text in HC |
| `ChatProseTextBrush`, `ChatSecondaryTextBrush` | `TextFillColorSecondary`; system window text in HC |
| `ChatMutedTextBrush` | `TextFillColorTertiary`; system window text in HC |
| `ChatSyntax*Brush` | Web highlight.js keyword, string, number, title, type and attribute colors per theme; comments use the muted/secondary tone; system window text in HC |
| `ChatClawBrush` | Light/dark Windows accent variants; system window text in HC |
| `ChatQuestionOptionBrush`, `ChatQuestionOptionHoverBrush` | Native default/secondary control fills; system window in HC |
| `ChatQuestionSelectedBrush`, `ChatQuestionSelectedStrokeBrush` | Windows accent tint and stroke; system window/highlight in HC |
| `ChatQuestionSubmitStyle` | Native accent fill and text-on-accent brushes, including system HC mappings |
| `ChatUserBrush` | Windows accent at low opacity; full system highlight in HC |
| `ChatUserTextBrush` | Primary text; system highlight text in HC |
| `ChatRichTextStyle` | Native rich-text Foreground binding to `ChatTextBrush`, for headings |
| `ChatProseRichTextStyle` | Native rich-text Foreground binding to `ChatProseTextBrush`, for Markdown prose |
| `ChatSecondaryRichTextStyle` | Native rich-text Foreground binding to `ChatSecondaryTextBrush`, for thinking text |
| `ChatCodeTextStyle` | Native code Foreground binding to `ChatSecondaryTextBrush` |
| `ChatSecondaryTextStyle` | Native progress/result TextBlock binding to `ChatSecondaryTextBrush` |
| `ChatMutedTextStyle` | Native TextBlock binding to `ChatMutedTextBrush`, for list bullets |
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

Tool disclosures use compact native Expander headers with a single-line,
ellipsized command, path, query or supplied description. Non-success states
remain visible; accessible names retain the tool and status. Accessible help
retains the bounded preview; rows have no hover tooltip, as on the web.
Expanded details keep the successful completion
label visible and retain multiline commands,
redacted structured arguments and literal output independently of that preview.
Input envelopes retain the existing 64K character budget, individual strings
use the shared safe-display bound, and output uses the 256KiB chat-entry budget.
An oversized input is explicitly unavailable, not silently empty. This is not
unbounded raw-payload disclosure or full-output retrieval beyond those limits.
`filePath` and `notebook_path` retain their live/history aliases. Input and
output have separate stable identities so late input does not replace an
already-open output control.

Native disclosure headers and action targets keep their existing sizing; compact
spacing does not clip header content or shrink hit targets. Provider review and
child-agent details share a scrolling region capped at 160 pixels, leaving more
room for the conversation while keeping all supplied details accessible.

Gateway-truncated tool results keep their typed truncation flag and canonical
saved result-message/session/call identity, separately from invocation input.
An expanded disclosure offers **Show full output** only when a read can be
scoped. This explicit action uses the existing authenticated `chat.message.get`
contract, not another tool invocation or chat send. Opening or rendering a row
does not fetch. The loader rechecks connection, session, reset, history revision
and exact result identity before dispatch and after completion. Collapse,
cancel, disposal and scope replacement retire the view's pending result.
Input and output controls retain their existing keys during hydration.

Live structured tool results retain the loaded session identity too. When older
history splits an invocation from its result, exact run/call matches reunite the
input with the existing result row without changing its identity, outcome,
recovery locator or active-run state. A Gateway `windowReset` keeps cached
messages visible and exposes the existing history Retry action; an invalidated
cursor is not treated as the end of history.

The shared `ChatToolDetailsProfile` reader guard defaults to unknown/unavailable.
It reuses the authenticated, epoch-bound `users.self` read and existing identity
parser without loading avatars or exposing profile IDs. Every verification
first advances an opaque revision and disables reads; only the same canonical
profile can become available again at that new revision. A different profile
retires details until a new connection. Unknown identity and failed verification
deny reads. Recovery compares the captured profile snapshot before dispatch and
completion, and includes it in cache identity. Thus a same-profile name edit can
recover without accepting a previous revision's cached or pending output.
After reconnect, saved-result reads also wait for history committed on the
current connection; rebinding an old cached row to a new epoch is not admission.

Recovery stays within the 256KiB UTF-8 display budget and requests at most
262,144 characters from the Gateway. A server-retained cap or local UTF-8 cap
remains explicitly **limited**, never complete. Unsupported Gateway versions,
missing or invisible results, oversized responses, mismatched identities and
transport failures have separate visible outcomes. Retry is an explicit action.
The panel can reuse `LoadToolOutputAsync` and `ChatToolOutputOperation`; it must
not add another fetch or renderer. These rules follow the
[pinned message-read handler](https://github.com/openclaw/openclaw/blob/b896457970bfa1b2cef23ea16f3731ba73436b0c/src/gateway/server-methods/chat-message-get-handler.ts).

Consecutive tool groups stay within the exact run identity and legacy turn.
Every consecutive tool span, including a single call, renders as one collapsed
summary row that counts operations per category in web order, for example
`3 commands · 2 reads · 1 other operation`. Failed and interrupted steps stay
inside their group, where the step row shows an inline red `failed` instead of
a trailing status. Calls with provider reviews or input still being prepared
keep their standalone disclosure so the review and live diff stay visible.
Each row shows its category glyph; hovering or focusing the row header swaps
that glyph for an expand chevron, and the built-in Expander chevron is hidden.
Row headers stay borderless in every state (rest, hover, pressed, disabled),
like the web's inline disclosure.
Headers retain a 32-pixel minimum target with vertically centered icons, titles
and inline status; titles and status use the web summary's 13 pixels. The icon
keeps its natural line height inside a 16-pixel-wide
slot, so the category glyph and the hover or focus chevron share the title's
center line instead of sitting at the top of a fixed box. The stock template's
header padding, hidden-chevron margin and content-border insets are removed, so
nested group layers do not drift from the prose edge. Commentary inside
**Worked** uses that same reading column without reserving a second pair of
avatar slots on wide viewports.
A call the Gateway records as wrapping others (`parentToolCallId`, same run)
lists its nested calls first when expanded, then its own input behind a
collapsed **Tool input** disclosure, like the web `groupToolCalls` tree. Links
are never inferred; ambiguous identities and cycles stay top-level. Group
counts include nested calls but not their wrapper. Each layer keeps its own
expansion key, so opening a group never opens its first row.
The last transcript row keeps a 24-pixel bottom inset above the composer.
Existing chronological messages, reasoning,
partial-output updates, collapsed-by-default disclosures and explicit group
expansion are unchanged. This is a focused adaptation of the default web tool
row at `b896457970bfa1b2cef23ea16f3731ba73436b0c`, not full transcript parity.
Dashboard sessions (`agent:<agent>:dashboard:<id>`, exactly four segments)
collect completed tools, commentary, and reasoning in a collapsed **Worked**
disclosure above preserved answers. The header reads **Worked for X** (or
**Worked**); expanded, it appends the tool-call count (`Worked for 4s · 3 tool
calls`) above a divider. Its body reuses the transcript rows, so tools appear as
their own collapsed group summaries between commentary and reasoning.
The policy follows the
[pinned web turn presentation](https://github.com/openclaw/openclaw/blob/b896457970bfa1b2cef23ea16f3731ba73436b0c/ui/src/pages/chat/chat-turn-presentation.ts).
It is a projection of the native transcript, not a rewritten transcript or a
WebView. Other session types retain their chronological presentation.

The Shared parser retains supplied message and text-signature phases, execution
identity, turn and steer boundaries, forwarding provenance, and automation
identity. Mixed phases remain separate through history replay and live snapshot
reconciliation. Unknown phases remain unknown. For unphased providers only,
the last eligible visible reply is the presentation fallback; this never writes
a phase or execution identity and is never used for send admission.

Live and steered turns stay unfolded. Independent messages and structural rows
bound reordering. Explicit answers, earlier other-run replies, and media remain
visible; trailing failed work stays outside the disclosure. Tool-only turns
retain their result rows. The disclosure key uses the terminal answer, session,
and generation, so older-history prepends do not reset explicit expansion.
Existing native tool controls, literal details, accents, and backplates are reused.

Newly completed work settles with a 200-millisecond fade, and a changing live
tool headline enters over 160 milliseconds. Reactor owns these enter-only
animations; no transcript dimensions, scroll position or focus are animated.
Initial/loading history, history revisions, session changes and generation
resets do not replay completed-work entrances. A stable tool identity prevents
partial-output updates from restarting its headline animation.
Reactor's live reduced-motion hook disables both effects. Stock WinUI disclosure
motion and the existing working-claw animation remain their respective owners.

Confirmed `sessions_yield` and asynchronous `github_publish` receipts create
handoff boundaries using the existing tool/run correlation. They do not become
completed-work rollups after resuming. Yield calls show only the public
acknowledgment, never private continuation input; failed, pending, malformed or
mismatched receipts cannot establish a handoff. Typed receipt facts are read
before display truncation, not recovered from rendered JSON.

Native chat has no bubble-mode setting or in-conversation search control.
The non-bubble default maps to the web default, with the bubble gate retained
at the pure projection seam. Global search remains a separate surface and
does not disable completed-work rollups. **Worked for** uses the
Gateway's finite nonnegative `runtimeMs` only when the session and terminal
`lastRunId` match the reply that owns the disclosure. Missing or unmatched
facts and work without one owning reply stay untimed; message timestamps never
fabricate a duration. Unsigned stream replacements clear stale block signatures,
and matching cumulative text prefixes reconcile within their causal/run boundary.
Conflicting unidentified text remains visible rather than being guessed away. Pixel-level parity
requires current-build visible proof in addition to the offscreen control tests.

Supplied assistant `thinking` blocks render as inline native Markdown, not as
an extra closed disclosure. Their text uses the web thinking block's 12/18
secondary tone, and their lists and headings share that ramp. Session
`reasoningLevel` falls back to
`reasoningDefault`: `on` shows saved reasoning and live previews; `stream`
shows previews only; other or absent values hide both. The client default
corresponds to web's enabled Show thinking preference. Settings > **Show
thinking** (`ShowChatThinking`, default on) mirrors web's toggle: turning it
off hides saved reasoning and live previews, but not the run-progress
"Thinking" status. Encrypted/redacted blocks are not
reconstructed. Live `thinking` events require supplied item/run identities and
cumulative text. Exact non-imported publication acknowledges a persisted
receipt; stream mode retains that preview until the run ends. Only the latest
nonempty, receipt-free occurrence can transfer to its matching final reply,
and existing saved thinking wins. Empty tails, resets and terminal outcomes
cannot revive an earlier thought. These rules follow the
[pinned reasoning owner](https://github.com/openclaw/openclaw/blob/b896457970bfa1b2cef23ea16f3731ba73436b0c/ui/src/pages/chat/chat-reasoning.ts),
not assumptions about older installed web builds.

Explicit commentary does not receive a final-answer footer. Waiting,
compaction and retry labels remain visibly present beside elapsed time rather
than existing only in accessibility text. A `run_status` retry with a valid
`retryAt` counts down ("Retrying in Ns") on the existing one-second progress
timer, then falls back to the plain retry label; text, tool or turn events clear
it. While a current-run reasoning stream exists and no reply text has arrived,
progress reads "Thinking" instead of "Working". These changes do not alter send
admission, terminal-run classification or the panel's typed detail action.

While history loads, the transcript shows a skeleton
(`ChatTimelineLoadingSkeleton`) whose opacity pulse is disabled by reduced
motion and high contrast. The timeline stops that pulse when it leaves Loading,
even if ItemsView retains the old row without raising Unloaded; delayed row
mounts cannot restart a retired pulse. When history fails after its bounded retries on a
connected Gateway, an empty transcript shows `ChatHistoryUnavailable` and a
nonempty one shows a `ChatHistoryFailedStatus` banner, each with an explicit
`ChatHistoryRetry` action. A failed thread is not reloaded implicitly on each
render; Retry, reconnect or a committed reload clears it.

If `chat.history` reports an `inFlightRun` for a session whose `sessionInfo`
says the run is still active, opening that session adopts it like web's
`applyHistoryRun`: the working state starts at the supplied `startedAt`, the
retained matching run events replay through the live event path, and the
buffered text becomes the streaming tail. Stop aborts that run. A live run,
pending abort, reset or terminal outcome already known to this client wins, and
the run's later live lifecycle end settles it.

Initial transcript tail navigation retains its native layout gate. Entering a
transcript control through keyboard focus cancels pending automatic
navigation just as pointer and keyboard input do. Focus at the tail can retain
follow mode; moving away stops it. No renderer performs its own scroll loop.
The owner observes synchronous `GettingFocus`, including handled events, and
uses the event's requested focus state. A child control can consume `GotFocus`,
and its asynchronous delivery can occur after focus has moved again.
An explicit jump in an already laid-out view drains pending layout work before
dispatch instead of waiting for a future layout-change event.
Programmatic realization focus does not cancel navigation.
Item-target navigation uses the existing `ItemsRepeater.GetOrCreateElement`,
waits for stable rendered row geometry, then dispatches `UIElement.StartBringIntoView`
after rechecking the row index, source identity and navigation generation.
It never calls the synchronous `ItemsView.StartBringItemIntoView` path, whose
recycled-container ancestor loop can hang the released runtime
([upstream fix](https://github.com/microsoft/microsoft-ui-xaml/pull/12320)).
Realization is bounded and is cancelled by input, completion, unload or replacement.
A viewport resize can arm one additional discrete tail request if realizing variable-height
rows moves the extent away from a followed tail. This correction waits for
layout, a rendering boundary, and an idle scroll view. It adjusts the offset to
the current extent without synchronously realizing another item. Initial,
new-message and explicit jump navigation retain index-based brings.
Resizing does not itself start a bring, and this is not a continuous extent-follow loop. A correction does
not start while initial positioning is still pending, so it cannot impose its
follow requirement on that initial request. A reader who has scrolled up is not
pulled back down. Pointer, keyboard, touch, and scrollbar interaction cancel
pending navigation; follow state is then taken from the current position, so a
click or key press at the tail keeps following new replies. The correction
rechecks follow eligibility at dispatch so a newer UI Automation scroll-away also
takes precedence. Session replacement, unload, and disposal fence late callbacks.
Initial landing confirmation waits for the current tail item's native bring to
complete and its layout to settle before retrying; missing events remain bounded.
It schedules retries after the rendering callback returns and fences them against
cancellation or replacement. If rows or the native scroll view disappear during
initial positioning, it rearms when they return. Normal follow cannot interrupt
initial positioning.
If a completed bring needs an offset correction, the current tail row remains
the native anchor at an interior ratio until completion or cancellation.
The anchor callback rechecks the navigation generation, row index and source
identity without realizing another row. This avoids both disabled-anchor layout
cycles and edge anchoring undoing the correction as the virtualized extent changes.
Native bottom anchoring and the existing follow threshold are unchanged, except
for that bounded correction and while a reader toggles a disclosure header by pointer, Enter or Space: the
transcript then anchors to the top, so the header stays put and its details
grow downward, as on the web. Any other reader input or programmatic tail scroll
restores bottom anchoring.
Actual tail visibility is tracked separately from follow eligibility. The
**Jump to latest** button stays mounted as an overlay, so showing it does not
shrink the transcript viewport or move the composer.
It is a 32-pixel circular native button with a 16-pixel Fluent down arrow,
not a text banner. Its localized tooltip and accessible name retain
**Jump to latest** or **New messages. Jump to latest** as appropriate. The
decorative glyph is excluded from the accessibility tree and does not scale
with text size; native keyboard focus and activation remain available.
At the tail, the button retains its overlay geometry with zero opacity and
does not accept pointer input, keyboard focus or accessibility navigation.
Tail visibility changes do not collapse a Grid child during native scrolling.

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

Queued submissions leave the textbox and appear in a single compact panel directly
above the composer, separate from the virtualized transcript. `ReactorChatPendingMessages`
owns this presentation; the provider still owns ordering, dispatch, cancellation and promotion.
The panel uses `ChatCardBrush`, `ChatStrokeBrush`, the composer's 20px radius and
the shared reading width and gutters. Rows use native outline list/return and trash icons
at 14px, a 20px native `PersonPicture` (18px at widths <=560px),
selectable 13/18 primary text and an always-visible 32px trash target. The 13,6,4,6
row insets keep the trash center 20px from the right content edge while retaining
the larger native hit target. Column gaps are 8px, or 6px on narrow layouts.
The avatar uses the sidebar's current user image and profile name, with the same
native initials fallback and generic person when no profile is known. It is
decorative to accessibility, leaving the row's message and status unchanged.
These proportions follow the [web queue reference](https://github.com/openclaw/openclaw/blob/fe12d0af7389290693346e816bc345e756168115/ui/src/styles/chat/composer-queue-content.css),
not its queue editing or reordering behavior. Native text
selection and its Copy context action remain available without a separate hover footer.
Attachment chips remain visible, and each row retains its accessible **Pending** or
**Failed** status. Sending disables removal without shifting the action column;
failures keep their error and removal action visible. A native scrolling region caps
the queue at 160px or one third of the viewport height, whichever is smaller, so
larger queues do not displace the composer. Promotion removes the queued row and
shows the provider's normal sent message once. Empty queues remove the panel,
without remounting the composer. This applies to Workspace's ChatPage.
If acceptance arrives after that run has completed, the conversation owner places
the sent message before its matching live output without reopening the completed
turn or disturbing a newer response. Remaining queued messages can then drain.
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

Workspace retains the chosen assistant and existing conversation across a
reconnect to the same Gateway. Clearing a temporary connection cache is not an
agent removal and does not select the Gateway default. Assistant selection and
conversation creation wait for a fresh authoritative roster. A changed Gateway
cannot reuse the previous Gateway's cached roster or remembered selection.
When a current roster actually removes the chosen agent, its explicit-selection
or legacy-default policy applies.

Session summary metadata, such as a health payload's count, path and recent
entries, does not replace the conversation roster. Only a session collection
can remove absent rows; a genuine empty array or legacy empty map still clears
the roster.

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

`ChatReasoningPicker` retires its native slider handlers and pending value changes
on both element unmount and host disposal. Disposal uses component effect cleanup,
because Reactor host disposal does not guarantee element `OnUnmount` callbacks.

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
inert images, list measurement, timeline keys and scroll-follow remain with
their existing owners.

Assistant links follow the web chat's anchor policy: Markdown links, bare
`http(s)://` and `www.` URLs become native WinUI hyperlinks in the Windows
accent color without an underline, and open in the default browser. Inline
code keeps its monospace run and gets a subtle `ControlFillColorSecondaryBrush`
background through a text highlighter (high contrast keeps system styling).
A rounded, bordered chip like the web needs a Reactor inline-code hook. Only
absolute http/https destinations survive sanitization (Reactor additionally
admits only http/https/mailto); any other scheme, relative path or image stays
inert text. Expanded tool details put input above output or error text in one
thin-bordered, transparent card. A full-width one-pixel `ChatStrokeBrush` divider
separates input and output only when both exist; there are no visible section
headings. Localized Tool input, Tool output and Tool error names remain on the
scroll regions for screen readers. Each section retains selectable monospace
text, 12,8 padding and its 240-pixel scroll cap. Input uses primary text with
theme-bound syntax accents for explicit PowerShell tools, commands prefixed
with PowerShell/pwsh, and JSON arguments. Unrecognized languages remain literal
primary text rather than being guessed from the client OS. Output and errors
use native secondary ink, not reduced element opacity; the failed outcome
remains explicit. ColorCode.Core parses only the already-sanitized display
input, with a bounded regex match deadline. A timeout is logged and leaves the
complete literal text visible. Syntax runs retain native selection and theme
changes without reinterpreting markup or executing code.
A settled call shows its outcome (**Completed** or **failed**)
right-aligned under a divider. Tool rows have no hover tooltip. While work is
active, the running tool title, running group headline and streaming **Thinking**
label use the web's `text-shimmer`: a brighter band sweeps
across the text every 2.4 seconds. The gradient's endpoints stay fixed to the
label; its three interior stops move only between offsets 0 and 1 while the
two edge stops retain readable base ink. Do not animate the text brush's
`RelativeTransform`: on the tested native runtime that faded unrelated text
and backgrounds in a rectangular shared rendering region, despite their XAML
opacity remaining 1. `InteractiveWork_DoesNotFadeCompletedMessagePixels`
checks real-window pixels before, during and after a synthetic tool run;
offscreen control-property assertions alone do not catch that failure.
The gradient uses native primary ink as its Light base and a softer secondary
ink for the lighter flare. The primary-to-secondary alpha gap is increased
by 20 percent so the moving band is clearly distinguishable. Dark reverses
those roles: softer secondary base with a primary-ink flare.
This preserves a noticeable moving band rather than an
almost invisible ten-percent alpha change. The flare changes
emphasis without washing out the sentence. The color stops retain at least
4.5:1 contrast against the tested light and dark backdrops. Windows animation off or high
contrast keeps static text, matching the web's reduced-motion and forced-colors
gates. Ink is resolved through the native primary and secondary text styles on the actual
label, with its current tree and Light/Dark override. Loaded and theme-change
callbacks refresh the gradient from that native value. Copying a brush color
directly out of application resources is not a substitute for native theme
resolution.
`ChatVisuals.WithTextShimmer` stops the storyboard and restores the original
foreground on Reactor unmount, before a text control can be pooled for another
message or footer. `ToolCallCardRenderer` additionally cleans its Expander's
header slot, which Reactor preview.16 omits from native unmount traversal.
Native `Unloaded` remains a second cleanup boundary.
Completed tool labels, sender metadata, timestamps and copy/read-aloud glyphs
never inherit the running animation. The working claw retains its own motion;
its output-token count and ticking elapsed-time text stay static. The claw and
clock row remain fully opaque, including entrance and timer updates. Entrance
motion uses transforms only and does not restart on every clock tick.

On desktop, sent user and assistant message footers reveal metadata and actions
only while their own message is hovered or contains keyboard focus, including
individual messages inside expanded Worked groups. Focusing a group header or
a sibling message does not reveal other footers. They keep their
measured space while hidden, so revealing a timestamp or Copy does not move the
next message. Metadata and action glyphs share a vertical center, with Copy
retaining its 32-pixel hit target. A failed queued message keeps its recovery
actions visible; an active read-aloud operation keeps its Stop action visible. Commentary has
no final-answer footer. Code-block Copy remains a separate, static header
control, as in the web reference. Tool disclosure glyphs continue to switch
to a chevron on hover or focus, independently of running state.
These rules follow the pinned web
[running-label gradient](https://github.com/openclaw/openclaw/blob/be1e36290247b257945988fb873cd3ab00cc05fd/ui/src/styles/base.css#L1276-L1310)
and [message-footer disclosure](https://github.com/openclaw/openclaw/blob/be1e36290247b257945988fb873cd3ab00cc05fd/ui/src/styles/chat/grouped.css).

The web's GitHub mark
and issue/pull-request chips need a custom link renderer, which the consumed
Reactor `0.1.0-preview.16` declares but does not call (`LinkBuilder` is honored
from `0.1.0-preview.17`); they remain a follow-up for that upgrade.
