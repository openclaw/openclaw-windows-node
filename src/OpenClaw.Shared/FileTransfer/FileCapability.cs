using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.FileTransfer;

public sealed class FileCapability(IOpenClawLogger logger, FileOperations operations) : NodeCapabilityBase(logger)
{
    public override string Category => "file";
    public override IReadOnlyList<string> Commands => FileCommandParser.Commands;
    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) => ExecuteAsync(request, CancellationToken.None);
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var command = FileCommandParser.Parse(request.Command, request.Args);
            if (!command.PreflightOnly && (command.Operation == FileOperation.Create || command.Binary) && request.FileChannel is null)
                return Shape(request, new(FileJson.Element(new { ok = false, code = "DUPLEX_REQUIRED", message = "This operation requires an authenticated file channel." }),
                    new(false, "DUPLEX_REQUIRED", "This operation requires an authenticated file channel.")));
            return Shape(request, await operations.ExecuteAsync(command, request.FileChannel, cancellationToken).ConfigureAwait(false));
        }
        catch (FileCommandException ex)
        {
            return Shape(request, new(FileJson.Element(new { ok = false, code = ex.Code, message = ex.Message }), new(false, ex.Code, ex.Message)));
        }
        catch (NodeSandboxExecutionException ex)
        {
            return new() { Id = request.Id, Ok = false, Error = ex.Message, ErrorCode = ex.Category.ToString(),
                Diagnostic = new(ex.Category, ex.ExecutionMode), FileOutcome = new(false, ex.Category.ToString(), ex.Message) };
        }
    }

    internal static NodeInvokeResponse Shape(NodeInvokeRequest request, FileOperationResult result)
    {
        var outer = request.Command is "fs.listDir" or "terminal.upload" || result.InvalidRequest;
        return new()
        {
            Id = request.Id,
            Ok = !outer || result.Outcome.Ok,
            Payload = outer && !result.Outcome.Ok ? null : result.Payload,
            Error = outer && !result.Outcome.Ok ? result.Outcome.Message : null,
            ErrorCode = outer && !result.Outcome.Ok ? "INVALID_REQUEST" : null,
            FileOutcome = result.Outcome,
            FileExecution = result.Execution
        };
    }
}
