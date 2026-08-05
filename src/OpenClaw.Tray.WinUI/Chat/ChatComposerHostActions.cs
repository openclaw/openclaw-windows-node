using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Host/view capabilities the composer controller forwards to, bound once when the
/// session is created by <see cref="ReactorChatHostExtensions.CreateComposerSession"/>.
/// These are delegate references to existing host behavior (dialog confirmation,
/// file picker, voice capture, settings navigation, speaker persistence) — not
/// named-control setters and not a second mutable state source. Internal: not part
/// of the pre-existing public host API, so it stays internal rather than growing
/// the public surface merely because it is a primary-constructor record.
/// </summary>
/// <remarks>
/// <see cref="SelectedSessionHandoff"/> is intentionally not part of this immutable
/// record: it depends on the root's per-mount selection state, which does not exist
/// until <see cref="OpenClawReactorChatRoot"/> renders for the first time. The root
/// binds it once via <see cref="ChatComposerController.BindSelectionHandoff"/>.
/// </remarks>
internal sealed record ChatComposerHostActions(
    Func<string, string?, Task<bool>>? ConfirmResetAsync,
    Action? AttachmentPickerRequest,
    Func<CancellationToken, Action?, Task<string?>>? VoiceCaptureRequest,
    Action? SettingsNavigation,
    Action<bool>? SpeakerMuteChanged);
