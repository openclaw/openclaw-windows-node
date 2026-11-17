using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.FileTransfer;

internal static class FileOutcomeTelemetry
{
    internal static NodeToolExecutionMode? ExecutionMode(NodeInvokeResponse response) =>
        response.FileExecution?.ExecutionMode switch
        {
            "sandbox" => NodeToolExecutionMode.Sandbox,
            "host" => NodeToolExecutionMode.Host,
            _ => null
        };
}
