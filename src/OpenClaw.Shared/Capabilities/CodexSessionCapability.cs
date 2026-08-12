using OpenClaw.Shared.Codex;

namespace OpenClaw.Shared.Capabilities;

internal sealed class CodexSessionCapability : NodeCapabilityBase
{
    public const string ThreadsListCommand = "codex.appServer.threads.list.v1";
    public const string ThreadsHistoryListCommand = "codex.appServer.threads.history.list.v1";
    public const string ThreadTurnsListCommand = "codex.appServer.thread.turns.list.v1";

    private static readonly string[] CommandNames =
    [
        ThreadsListCommand,
        ThreadsHistoryListCommand,
        ThreadTurnsListCommand,
    ];

    private readonly CodexSessionCatalogService _catalog;

    internal CodexSessionCapability(IOpenClawLogger logger, CodexSessionCatalogService catalog)
        : base(logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public override string Category => "codex-app-server-threads";

    public override IReadOnlyList<string> Commands => CommandNames;

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) =>
        ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = request.Command switch
            {
                ThreadsListCommand => await _catalog.ListThreadsAsync(
                    request.Args,
                    cancellationToken).ConfigureAwait(false),
                ThreadsHistoryListCommand => await _catalog.ListThreadHistoryAsync(
                    request.Args,
                    cancellationToken).ConfigureAwait(false),
                ThreadTurnsListCommand => await _catalog.ListThreadTurnsAsync(
                    request.Args,
                    cancellationToken).ConfigureAwait(false),
                _ => default,
            };
            return payload.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? Error($"Unknown command: {request.Command}")
                : Success(payload);
        }
        catch (CodexSessionCatalogValidationException exception)
        {
            return Error(exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Error(request.Command == ThreadTurnsListCommand
                ? "Codex app-server transcript is unavailable"
                : "Codex app-server catalog is unavailable");
        }
    }
}
