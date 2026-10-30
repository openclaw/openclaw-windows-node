# Reactor chat parity audit

Reference: [openclaw/openclaw at `1fe2aa03fe4fb4f53ac11290562f9b60acc45e91`](https://github.com/openclaw/openclaw/tree/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat),
audited October 8, 2026. This is a source comparison, not a claim that every
feature of that unreleased web UI exists in every installed Gateway.

The Companion's native chat is Reactor over WinUI, not an embedded copy of the
web application. Existing WinUI resources remain the design authority for this
change. Questions use a pinned native dock instead of a modal popup.
The October visual follow-up makes an expanded dock replace the writing surface
while retaining the mounted composer and its unsent draft.

## October 2026 visual follow-up

The supplied macOS screenshot is compared against
[Control UI source `db2d951`](https://github.com/openclaw/openclaw/tree/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src).
This is a second, explicitly pinned visual reference, not a replacement for the
protocol audit below.

| Check | Reference | Native change or remaining difference |
| --- | --- | --- |
| [x] | Filled 18-pixel claw and double snip | Literal upstream SVG paths, pivot, 2.4-second keyframes and cubic-bezier interpolation |
| [x] | Surprise motions and reduced motion | All twelve variants, 5% seeded selection, 4.8-second delay and reduced-motion stance |
| [x] | Compact working row | 200-millisecond entrance, elapsed time and known output tokens; phase retained for accessibility; completed turns dismiss the row |
| [x] | Bottom question card | Upstream spacing with native WinUI backplates and Windows accents, numbered options, descriptions, inline Other, collapse and Skip/Submit |
| [x] | Multi-question flow | Counter, single-choice advance, Back/Next, multiple selections, request switching and retained drafts |
| [x] | Keyboard flow | Digits outside editors, Ctrl+Enter, newline in text, native arrow selection and focus |
| [x] | Composer preservation | Expanded question hides, rather than remounts, the same writing surface |
| [ ] | Complete screenshot pixel identity | Windows font metrics, shell, sender avatar/identity tint and background artwork remain different |

The screenshot's teal user bubble is sender-identity styling, not the universal
upstream user color. Companion does not invent that identity or copy a screenshot's
personal avatar. Existing transcript sender colors and avatar behavior are not
claimed as part of this completed visual port.

Pure `ChatWorkingClawMotionTests` check independent source keyframes, interpolation,
surprise selection and reduced/settled poses. Mounted `ReactorQuestionProofTests`
exercise actual native controls, keyboard input, per-question drafts and animation
unload. The production fixture round-trip additionally checks that question
expansion/collapse/acknowledgement preserves the same composer and draft. These
layers are not real-provider proof or an exhaustive web workspace port.

## Why the app appeared frozen

1. The root hid its working indicator as soon as a turn contained assistant
   prose. A preamble followed by long tool work consequently had no persistent
   run feedback.
2. `ChatEventMapper` omitted partial tool results and several progress streams.
   Tool cards could remain unchanged until completion even while output arrived.
3. The Gateway client did not route `question.requested` or `question.resolved`.
   A run waiting for structured user input had no native answering surface.
   Sending an ordinary chat message is not a substitute for `question.resolve`.
4. Newer Gateway `chat` frames can contain append-only `deltaText` and `replace`
   without `message`. A snapshot-only consumer silently loses those updates.

## Checklist and native diff

`[x]` means a corresponding native capability exists, including the additions
below. It does not mean pixel identity or live-provider verification. `[ ]`
marks partial or missing parity. "Existing" means preserved, not newly
implemented or revalidated exhaustively by this patch.

### Streaming, progress and interruption

| Check | Web behavior | Native before | Native after / owner |
| --- | --- | --- | --- |
| [x] | Assistant text snapshots and deltas | Existing agent deltas and chat snapshots | Preserved in `ChatEventMapper` and `ChatTimelineReducer` |
| [x] | Append-only `chat.deltaText`, replacement, snapshot-plus-delta deduplication | Missing reconstruction | `GatewayChatStreamProjection` reconstructs once before all consumers, with connection/session/agent/run isolation |
| [x] | Recover a missing append baseline | No recognition | Visible notice and generation-bound socket abort through the existing bounded reconnect/history path; no fabricated partial response |
| [x] | Reasoning content and reasoning completion | Existing | Preserved reasoning rows and lifecycle |
| [x] | Persistent working claw after assistant preamble | Generic indicator disappeared | `ReactorRunProgress` stays present for the active turn, with native animated claw geometry |
| [x] | Elapsed run time and terminal timing metadata | No run clock | Locally observed start/end timestamps remain in state; the row dismisses at completion unless input is pending; no invented historical duration |
| [x] | Respect reduced motion; avoid per-second screen-reader announcements | No elapsed animation | Native OS animation preference checked at mount; timer live region is off; unmount stops timer/storyboard |
| [x] | Distinguish working, thinking, responding, running tool, waiting for input/approval | Mostly generic thinking | `ChatRunProgressProjection` prioritizes pending questions and approvals |
| [x] | Tool start, result, error, correlated parallel tools | Existing | Preserved `NativeToolProjector`, reducer and native tool cards |
| [x] | Tool `phase:update` / `partialResult` | Dropped | In-progress preview updates, sequence and run fences, terminal outcome cannot be overwritten by a late update |
| [x] | Command output updates | Completion only | `command_output` update snapshots and delta appends; bounded previews |
| [x] | Compaction, compaction retry, model-request retry | Omitted active status | Nonterminal progress labels without ending the run |
| [x] | Lifecycle waiting approval / approval resolved | No progress label | `waiting-approval` and `approval-resolved` map to progress; existing approval authorization remains unchanged |
| [x] | Fallback model and operational notice | Omitted | Safe status rows for fallback/restoration and notices |
| [x] | Latest-run output tokens and terminal token recap | Session/entry usage only | `usage.outputTokens` maps to the observed run, with sequence fences, downward corrections, late final counts and zero; new turns clear the counter. Session totals are never substituted |
| [ ] | Authoritative run timing and throughput | Session/entry usage exists | Elapsed time remains locally observed; no provider-time or tokens-per-second claim |
| [ ] | Tool `input_delta`, provider `review`, Guardian-specific structured activity | Partial tool/presentation support | Rich argument/diff preview and provider-specific review surfaces remain gaps |
| [ ] | Waiting for subagents, swarm/child activity headlines | Some generic tool activity | No equivalent child attention/progress panel |

Sources: [working indicator](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/components/chat-working-indicator.ts),
[progress](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/chat-progress.ts),
[run usage contracts](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/tool-stream.usage.node.test.ts),
[stream status](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/tool-stream-status.ts),
[stream projection](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/packages/gateway-client/src/chat-stream-projection.ts).

### Interactive questions and approvals

| Check | Web behavior | Native before | Native after / owner |
| --- | --- | --- | --- |
| [x] | Discover questions without a transcript message | Missing | Gateway question events and `question.list` recovery through the captured operator bridge |
| [x] | Single choice / multiple choices / free text / lines | Missing | Native radio buttons, checkboxes and text input in `ReactorQuestionPanel`; wire values, not labels |
| [x] | Up to three questions, form option limits, defaults, optional empty answers | Missing | Validated protocol records and `ChatQuestionDraft` |
| [x] | Secret text entry | Missing | PasswordBox; secret defaults discarded, no transcript/cache persistence, clear after submission |
| [x] | Question URL | Missing | Explicit HTTP/HTTPS link action, no automatic launch or credential-bearing URL |
| [x] | Submit/cancel with server confirmation | Missing | Exact `question.resolve` RPC, disabled duplicate submission, acknowledged terminal state |
| [x] | Resolution in another client, expiry, conversation routing | Missing | ID tombstones, per-record revisions, local expiry, session/agent-scoped display and submission |
| [x] | Reconnect/list/get recovery and failed submit feedback | Missing | Generation fencing, merge recovery, manual refresh, unanswered prompts retained; uncertain answers cannot be resent before refresh |
| [x] | Older Gateway compatibility | No question support | Unknown `question.list` is treated as unsupported, not as a failed chat connection |
| [x] | Exec approvals | Existing native permission cards | Existing `ChatApprovalState` / `PermissionRequestCard`; not merged with question answers |
| [ ] | Resource previews, thumbnail/resource selection, secret-store authorization | Missing | Explicit web-chat-required explanation; native does not guess authorization or send a secret as ordinary chat |
| [x] | Independent recovery and not-found retirement | Missing | Coalesced refresh publishes each recovered question independently. Structured `error.details.reason=QUESTION_NOT_FOUND` retires the prompt as unavailable, not as an invented answer/expiry |
| [ ] | Web's full recovery retry schedule | Missing | Native retries read-only recovery after 1, 2 and 4 seconds, then leaves explicit refresh and web-chat guidance. Unlike web's repeated 4-second polling, retries are bounded per reconnect/manual refresh. Answers are never automatically resubmitted |

Sources: [question wire schema](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/packages/gateway-protocol/src/schema/questions.ts),
[question owner](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/app/question-prompt.ts),
[answer controls](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/components/chat-question-answer-controls.ts),
[resource questions](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/components/chat-question-resource.ts).

### Existing conversation essentials

| Check | Web behavior | Companion status / native owner |
| --- | --- | --- |
| [x] | Markdown, code blocks, copy, selectable prose | Existing `ChatMarkdownPresentation`, sanitizer, bubble and copy renderers |
| [x] | Expandable reasoning and tool activity | Existing `ChatToolActivityPresentation` and `ToolCallCardRenderer` |
| [x] | Attach, paste/drop files and images; attachment removal | Existing composer controller, attachment presentation and media renderer |
| [x] | Model search/selection and effort controls | Existing `ChatModelPicker`, `ChatReasoningPicker`, `ChatEffortGauge` |
| [x] | Slash commands, draft editing, queue, cancel, stop/retry | Existing composer controller, send queue, lifecycle dispatcher |
| [x] | Session selection, saved transcript/history, reconnect fences | Existing provider facade, conversation/history owners and Workspace selection |
| [x] | Read aloud and voice input | Existing composer host actions and voice services |
| [x] | Compaction boundary, usage and checkpoint entry points | Existing compaction/usage presentation and checkpoint navigation |
| [x] | Native themes, high-contrast resources, automation names | Existing semantic resources retained; new question and progress copy in all six locales |
| [ ] | Cursor pagination/full transcript search and large-history equivalence | Native has history loading and virtualized timeline, but root does not expose the web's full pagination/search workflow |
| [ ] | Queue reorder/edit/steer, durable outbox custody | Native queue exists; not equivalent to the web outbox and steer lifecycle |
| [ ] | Rich audio/video/PDF/HTML previews and media galleries | Native typed media/attachments exist, but not every web preview/player/lightbox |
| [ ] | Mention/skill/library selection, selected context, goals, incognito | Slash/model/effort controls do not constitute equivalent composer capability menus |
| [ ] | Live collaboration: presence, typing, reactions, reply attribution, sharing | No equivalent complete native collaboration workflow |

Sources: [web component inventory](https://github.com/openclaw/openclaw/tree/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/components),
[composer](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/components/chat-composer.ts),
[outbox owner](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/chat-outbox-owner.ts),
[history pagination](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/chat-history-pagination.ts).

### Web workspace surfaces, not equivalent to the Companion chat

- [ ] Embedded browser and annotation lifecycle.
- [ ] Terminal/process panels and continue-in-terminal workflow.
- [ ] File editor, unsaved draft conflict resolution, file and session diff panels.
- [ ] Subagent/session roster, swarm progress and child attention controls.
- [ ] GitHub pull request/CI panels and publication workflows.
- [ ] Session discussion/board views, split panes and persisted panel layouts.
- [ ] MCP app embeds, resource widgets and exports.

These are visible in the pinned [pane composition](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/ui/src/pages/chat/chat-pane.ts)
and component inventory. Native canvas, diagnostics, sessions and checkpoint
windows are related Companion features, not proof that these web workflows
have been ported. They remain explicit follow-up scope, not silent checkmarks.

## Ownership and invariants

- `OpenClawGatewayClient.Questions` owns protocol IO. `GatewayQuestions` rejects
  malformed records and retains form presentation. Form and secret answers keep
  their whitespace and repeated values; lines answers accept CR, LF and CRLF.
  Errors do not log question answers. Limited bootstrap handoff requests
  `operator.questions` without adding admin authority or widening stored device
  scopes, matching the pinned
  [upstream bootstrap profile](https://github.com/openclaw/openclaw/blob/1fe2aa03fe4fb4f53ac11290562f9b60acc45e91/src/shared/device-bootstrap-profile.ts).
- `ChatQuestionCoordinator` owns transient prompts, recovery and submission for
  one bridge. Questions may outlive a run. Unscoped operator questions can appear
  in any selected conversation; scoped questions require exact session/agent
  identity. No question drafts or answers are written into chat history.
  Retry timers stop on disconnect/dispose. Old-generation results cannot clear
  a replacement refresh. A failed or slow get does not withhold another
  question's confirmed state. Structured invalid-answer replies allow correction
  without an uncertain-outcome warning. Already-terminal replies trigger bounded
  status reads, never answer resubmission: a pending read can still represent an
  in-flight or unknown commit and cannot release the submission fence. A known
  terminal event wins over late submission replies and recovery.
- `GatewayChatStreamProjection` owns append baselines only. Connection changes
  and terminal frames retire them; retained baselines are bounded to 16 runs
  and 2 MiB characters per reconstructed message. Legacy text-only frames still
  use the existing handler without creating an append baseline; modern appends
  without a baseline still trigger stream recovery.
- `ChatConversationState` continues to own transcript transactions. The root
  only subscribes, selects and composes. `ReactorRunProgress` owns its UI timer
  and animation, never a second gateway connection.
- Only the latest observed run's usage is retained in each timeline, not an
  unbounded run history. Sequence fences survive terminal events, tool replay
  reset and history refresh. A sessionless usage event must match a currently
  active run through `ChatLifecycleState`; other sessionless streams still
  follow the existing missing-identity diagnostic.

## Verification scope

Focused coverage is in `GatewayQuestionsTests`, `GatewayChatStreamProjectionTests`,
`OpenClawGatewayClientTests`, `ChatQuestionCoordinatorTests`,
`ChatStreamingParityTests`, `ReactorQuestionProofTests`, and
`ReactorChatLayoutProofTests.ActiveRun_AfterAssistantPreamble_KeepsClawAndElapsedTimeVisible`.
The mounted tests exercise actual Reactor/WinUI controls with synthetic events.
They are not a claim of real provider or live Gateway question support.

Required closeout:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

For mounted proof on native ARM64:

```powershell
$env:OPENCLAW_VISUAL_TEST = '1'
$env:OPENCLAW_VISUAL_TEST_DIR = '<isolated-proof-directory>'
$env:OPENCLAW_RUN_KEYBOARD_PROOF = '1' # Only on an authorized, isolated interactive desktop.
.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'FullyQualifiedName~ReactorQuestionProofTests|FullyQualifiedName~ActiveRun_AfterAssistantPreamble' -ResultName 'reactor-chat-parity' -RuntimeIdentifier win-arm64
```

Live Gateway proof still requires a Gateway/provider that emits the pinned
question and append-stream contracts. Exercise a long tool run after a preamble,
an interactive choice, resolution from another client, and a disconnect during
submission. Record that separately from fixture/component results.

### Collected results (October 8, 2026, Windows ARM64)

| Command / proof | Result |
| --- | --- |
| `.\build.ps1` | Passed all five build targets and documentation checks |
| `dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore` | 4,284 passed, 35 suite-declared skips, zero failures |
| `dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore` | 4,023 passed, zero skips/failures |
| `dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj --no-restore -r win-arm64 --filter 'FullyQualifiedName~ReactorQuestionProofTests\|FullyQualifiedName~ActiveRun_AfterAssistantPreamble'` | 3 passed, zero skips/failures |
| Loopback question RPC | Answer/cancel payload nesting and matched acknowledgement verified by `QuestionResolve_WireRoundTripUsesNestedAnswersAndRequiresMatchingAcknowledgement` |
| Visible native state | Captured and inspected 320-DIP light and 800-DIP dark question cards, plus the production chat root showing elapsed time after assistant prose |
| Rubber-duck review | Corrected omitted `isOther` semantics, 64-option protocol limit, mixed-clock timing, empty preview updates, unsafe-link isolation and repeated stream serialization |
| Live Gateway/provider | Not exercised; synthetic Gateway events/RPC and mounted native controls are the collected evidence |

The UI pass caught a native animation failure: WinUI's default transform can
be a non-null matrix, so the jaw must explicitly own a `RotateTransform` before
animating `Angle`. It also caught narrow action-button clipping; the actions
now stack and option labels wrap.

One concurrent full-suite run failed existing MCP shutdown and HTTP redirect
tests. Both passed on focused rerun without changes. The final full build,
Shared suite and Tray suite were rerun serially and passed with the counts
above. No tests were disabled or assertions relaxed.

### Recovery and run-usage follow-up

The follow-up adds coalesced, bounded automatic recovery after reconnect,
unconfirmed submission or malformed question events; independent per-question
recovery; structured not-found retirement; and stable oldest-first eviction of
terminal-question tombstones. It adds sequenced output-token counts for the
latest observed run, including corrected counts and a frozen terminal recap.
The count never falls back to session totals and clears at the next turn.

The screenshot pass exposed a dark-theme problem that the first control-only
assertions missed: Reactor-resolved brushes could retain light-theme values.
Progress/result text and question cards now use native theme-bound styles.
Light, Dark and simulated HighContrast proof checks composite text contrast
against the rendered surface at a minimum of 4.5:1. OS High Contrast mode itself
was not changed or verified.

| Command / proof | Final follow-up result |
| --- | --- |
| `.\build.ps1` | Passed all five build targets and documentation checks after the theme correction |
| `dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore --verbosity quiet` | 4,287 passed, 35 suite-declared skips, zero failures |
| `dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore --verbosity quiet` | 4,043 passed, zero skips/failures |
| `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'FullyQualifiedName~ReactorQuestionProofTests\|FullyQualifiedName~ActiveRun_AfterAssistantPreamble' -ResultName 'reactor-chat-followup-final' -RuntimeIdentifier win-arm64` | 6 passed, zero skips/failures |
| Visible native proof | Inspected current-source narrow/wide run counters and terminal recap, dark and simulated HC question cards, and the unavailable terminal card |
| Rubber-duck review | No blocking findings. Added malformed-event automatic recovery and stable tombstone eviction; kept discovery errors connection-global because the failed list may concern an as-yet undiscovered question |
| Live Gateway/provider | Still not verified. Loopback transport and synthetic mounted UI do not establish provider compatibility |

The required build and Shared/Tray suites were rerun serially after the final
production code change. Proof results are in
`TestResults\ProofPools\reactor-chat-followup-final\reactor-chat-followup-final.trx`;
screenshots are retained in this session's `files\reactor-proof-final` directory.
The unchecked checklist items above remain open.
