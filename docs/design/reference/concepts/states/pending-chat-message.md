# Concept: Pending chat message

A locally accepted message waiting for the current assistant turn or send admission.

## Canonical name and copy

The accessible status is `Pending` (`Chat_Timeline_Pending`) until the provider
promotes the message into the transcript. It stays available without a visual footer.
Failed messages retain the existing `Failed` label and error detail.

## Icon and states

Pending messages share one rounded `ChatCardBrush` panel above the composer.
Each row has the web queue's native-rendered list/return outline, a circular
operator-avatar fallback, selectable `ChatTextBrush` text and an always-visible
outline trash removal action. Icons are 14px with rounded 1.75px strokes in a
24px viewbox; the trash target remains 32px. The avatar is 20px (18px on narrow
layouts), with 13/18 text and compact row insets. Row dividers use `ChatStrokeBrush`.
Sending disables the action in place; failures retain their error and enabled
removal. Native text selection supplies Copy without a separate action footer.
The panel is bounded to 160px or one third of the viewport height, whichever is smaller.

## Appears in

Workspace's ChatPage through its Reactor root, immediately above the composer.
See [Native chat visual system](../../../../CHAT_VISUAL_DESIGN.md).

## Code anchors and invariants

- `ReactorChatPendingMessages`: keyed panel rows outside transcript history.
- `OpenClawReactorChatRoot`: selected-thread queue and cancel callback.
- `ChatComposerController`: accepted submission clears the matching draft.
- `ChatQueueState`: unchanged send ordering, cancellation and promotion owner.

Do not keep accepted queued content in the composer or duplicate it in provider
history. Preserve text selection, attachment chips, copy, cancel before dispatch,
and failed-message removal. Sending entries cannot be canceled from the queue.
