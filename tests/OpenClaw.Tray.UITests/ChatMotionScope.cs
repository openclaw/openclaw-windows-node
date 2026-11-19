using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Temporarily controls chat motion. The test assembly turns motion off at load so layout proofs
/// are deterministic; motion proofs can opt in and override the platform reduced-motion setting.
/// </summary>
/// <example>
/// <code>
/// using var motion = new ChatMotionScope(enabled: true);
/// </code>
/// </example>
internal sealed class ChatMotionScope : IDisposable
{
    private readonly bool? _previous = ChatMotion.AnimationsOverride;
    private readonly bool? _previousReducedMotion = ChatMotion.ReducedMotionOverride;

    public ChatMotionScope(bool enabled, bool? reducedMotion = null)
    {
        ChatMotion.AnimationsOverride = enabled;
        ChatMotion.ReducedMotionOverride = reducedMotion;
    }

    public void Dispose()
    {
        ChatMotion.AnimationsOverride = _previous;
        ChatMotion.ReducedMotionOverride = _previousReducedMotion;
    }
}
