using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.Widgets;

namespace OpenClaw.Shared.Capabilities;

public sealed record WidgetHostStatus(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("pinnedCount")] int PinnedCount,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>Data-only widget publication, shared by Gateway node invocation and local MCP.</summary>
public sealed class WidgetCapability(
    IOpenClawLogger logger,
    WidgetSnapshotStore? store = null,
    Func<bool, WidgetHostStatus>? host = null,
    Func<bool>? isEnabled = null) : NodeCapabilityBase(logger)
{
    public const string OptInVariable = "OPENCLAW_ENABLE_WIDGETS_POC";
    private static readonly string[] AllCommands = ["widgets.list", "widgets.upsert", "widgets.delete"];
    public override string Category => "widgets";
    public override IReadOnlyList<string> Commands => Enabled ? AllCommands : [];
    private bool Enabled => isEnabled?.Invoke() ?? true;

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
        ExecuteAsync(request, CancellationToken.None);

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enabled || store is null)
            return Task.FromResult(Error("Windows widget proof of concept is not enabled."));
        try
        {
            object payload;
            switch (request.Command)
            {
                case "widgets.list":
                    payload = new { widgets = store.List(), host = Host(update: false), requiresUserPin = true };
                    break;
                case "widgets.upsert":
                    store.Upsert(WidgetSnapshotStore.Parse(request.Args));
                    payload = new { saved = true, host = Host(update: true), requiresUserPin = true };
                    break;
                case "widgets.delete":
                    var id = GetStringArg(request.Args, "id")
                        ?? throw new ArgumentException("Widget id is required.");
                    payload = new { deleted = store.Delete(id), host = Host(update: true), requiresUserPin = true };
                    break;
                default:
                    return Task.FromResult(Error($"Unknown command: {request.Command}"));
            }
            return Task.FromResult(Success(payload));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Widget command failed: {ex.GetType().Name}");
            return Task.FromResult(Error(ex.Message));
        }
    }

    private WidgetHostStatus Host(bool update) => host?.Invoke(update)
        ?? new(false, 0, "A packaged Windows widget provider is not available. The snapshot is cached only.");
}
