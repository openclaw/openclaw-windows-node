using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Temporarily sets whether chat motion runs. The test assembly turns chat motion off at load
/// (see TestHostDpiInitializer) so layout proofs are deterministic; a motion proof opts back in.
/// </summary>
/// <example>
/// <code>
/// using var motion = new ChatMotionScope(enabled: true);
/// </code>
/// </example>
internal sealed class ChatMotionScope : IDisposable
{
    private readonly bool? _previous = ChatMotion.AnimationsOverride;

    public ChatMotionScope(bool enabled) => ChatMotion.AnimationsOverride = enabled;

    public void Dispose() => ChatMotion.AnimationsOverride = _previous;
}
