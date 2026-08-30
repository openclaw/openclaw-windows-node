using System.Collections.Frozen;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared;

/// <summary>
/// Command Center risk taxonomy for the optional Ollama node capability.
/// Ollama is not a platform parity requirement, so its read-only command stays
/// separate from the canonical companion command list.
/// </summary>
public static class OllamaNodeCommandPolicy
{
    public static readonly string[] ReadOnlyCommands =
    [
        OllamaCapability.ModelsCommand,
    ];

    public static readonly FrozenSet<string> ReadOnlyCommandSet =
        ReadOnlyCommands.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] SensitiveCommands =
    [
        OllamaCapability.ChatCommand,
    ];

    public static readonly FrozenSet<string> SensitiveCommandSet =
        SensitiveCommands.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
