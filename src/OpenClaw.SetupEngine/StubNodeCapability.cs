using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Lightweight INodeCapability stub used only during setup to advertise capabilities
/// to the gateway. Does not handle actual command invocation — the tray app owns that.
/// </summary>
internal sealed class StubNodeCapability : INodeCapability
{
    public string Category { get; }
    public IReadOnlyList<string> Commands { get; }

    // Pair the same wire surface the tray will declare after setup completes.
    public IReadOnlyList<string> ProtocolCapabilities => SystemCapability.GetProtocolCapabilities(
        Category == "system" && Commands.Contains("system.run"));

    public StubNodeCapability(string category, string[] commands)
    {
        Category = category;
        Commands = commands;
    }

    public bool CanHandle(string command) => Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

    public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => Task.FromResult(new NodeInvokeResponse { Ok = false, Error = "Setup stub — not implemented" });
}
