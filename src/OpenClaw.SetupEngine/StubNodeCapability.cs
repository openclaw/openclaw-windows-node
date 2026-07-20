using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Lightweight INodeCapability stub used only during setup to advertise capabilities
/// to the gateway. Does not handle actual command invocation - the tray app owns that.
/// </summary>
internal sealed class StubNodeCapability : INodeCapability
{
    public string Category { get; }
    public IReadOnlyList<string> Commands { get; }

    public StubNodeCapability(string category, string[] commands)
    {
        Category = category;
        Commands = commands;
    }

    public bool CanHandle(string command) => Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

    public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => Task.FromResult(new NodeInvokeResponse { Ok = false, Error = "Setup stub - not implemented" });
}
