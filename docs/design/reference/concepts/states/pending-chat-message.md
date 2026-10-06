# Concept: Pending chat message

A locally accepted message waiting for the current assistant turn or send admission.

## Canonical name and copy

The status label is `Pending` (`Chat_Timeline_Pending`). It appears beneath queued
and sending messages on hover or keyboard focus until the provider promotes them
into the transcript. The accessible item status is always available.
Failed messages retain the existing `Failed` label and error detail.

## Icon and states

No status icon is required. The existing Cancel action uses `FluentIconCatalog.Exit`.
Pending messages use the normal right-aligned user bubble geometry with
`ChatPendingUserBrush`, a `ChatStrokeBrush` outline, and `ChatSecondaryTextBrush`,
not reduced whole-control opacity. The pending fill is transparent in every
theme so the bubble has the same background as the conversation. Text uses the
slightly faded secondary foreground in Light/Dark and readable system text in HC.
The status and actions share a hover/focus footer that reserves its layout space.
Failures retain an always-visible footer and error. Keyboard focus reveals the
footer independently of pointer position.

## Appears in

Both ChatPage and ChatWindow through their shared Reactor timeline.
See [Native chat visual system](../../../../CHAT_VISUAL_DESIGN.md).

## Code anchors and invariants

- `ReactorChatTimeline`: presentation-only queue rows after the current turn.
- `OpenClawReactorChatRoot`: selected-thread queue and cancel callback.
- `ChatComposerController`: accepted submission clears the matching draft.
- `ChatQueueState`: unchanged send ordering, cancellation and promotion owner.

Do not keep accepted queued content in the composer or duplicate it in provider
history. Preserve text selection, attachment chips, copy, cancel before dispatch,
and failed-message removal. Sending entries cannot be canceled from the queue.
