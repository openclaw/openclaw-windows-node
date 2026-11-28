using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Lists and approves gateway pairing requests for setup. Payloads match the gateway
/// <c>device.pair.list</c>/<c>node.pair.list</c> shape (<c>{pending:[...], paired:[...]}</c>)
/// on both transports, so <see cref="ApprovalRequestHelper"/> parsing is transport-independent.
/// </summary>
internal interface ISetupPairingRequests
{
    Task<PairingRequestResult> ListAsync(ApprovalRequestKind kind, TimeSpan timeout, CancellationToken ct);
    Task<PairingRequestResult> ApproveAsync(ApprovalRequestKind kind, string requestId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Output is the success payload (stdout JSON or RPC payload JSON) or the failure text.
/// FailureDetail is "exit N" for the CLI and "gateway rpc" for the WebSocket.
/// </summary>
internal sealed record PairingRequestResult(bool Success, string Output, string FailureDetail)
{
    public static PairingRequestResult Ok(string output) => new(true, output, "");
    public static PairingRequestResult Failed(string output, string detail) => new(false, output, detail);
}

/// <summary>
/// Cold <c>openclaw</c> CLI transport. Used before a paired setup operator session exists.
/// </summary>
internal sealed class CliPairingRequests(SetupContext ctx) : ISetupPairingRequests
{
    public Task<PairingRequestResult> ListAsync(ApprovalRequestKind kind, TimeSpan timeout, CancellationToken ct)
        => RunAsync(
            $"""{ctx.WslPathPrefix} && openclaw {ApprovalRequestHelper.Noun(kind)} list --json""",
            requestId: null,
            timeout,
            ct);

    public Task<PairingRequestResult> ApproveAsync(ApprovalRequestKind kind, string requestId, TimeSpan timeout, CancellationToken ct)
        => RunAsync(
            $"""{ctx.WslPathPrefix} && {ApprovalRequestHelper.ApprovalCommand(kind)}""",
            requestId,
            timeout,
            ct);

    private async Task<PairingRequestResult> RunAsync(
        string command,
        string? requestId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(token))
            return PairingRequestResult.Failed("No gateway token is available for pairing requests.", "no gateway token");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };
        if (requestId is not null)
            env = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId);

        var result = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            command,
            timeout,
            env,
            ct,
            inputViaStdin: true);

        return result.ExitCode == 0
            ? PairingRequestResult.Ok(result.Stdout.Trim())
            : PairingRequestResult.Failed(
                $"{result.Stdout.Trim()} {result.Stderr.Trim()}".Trim(),
                $"exit {result.ExitCode}");
    }
}

/// <summary>
/// Connected setup operator WebSocket used for pairing RPCs after operator pairing. The
/// session owns the client and disconnects it on dispose. The gateway grants the setup
/// operator <c>operator.admin</c> + <c>operator.pairing</c>, which the pair list/approve
/// methods require.
/// </summary>
internal sealed class SetupOperatorPairingSession : ISetupPairingRequests, IAsyncDisposable
{
    private readonly Func<string, object?, TimeSpan, CancellationToken, Task<JsonElement>> _send;
    private readonly Func<ValueTask> _dispose;
    private bool _disposed;

    internal SetupOperatorPairingSession(
        Func<string, object?, TimeSpan, CancellationToken, Task<JsonElement>> send,
        Func<ValueTask> dispose)
    {
        _send = send;
        _dispose = dispose;
    }

    internal static SetupOperatorPairingSession FromConnectedClient(OpenClawGatewayClient client)
        => new(
            (method, parameters, timeout, ct) =>
                client.SendWizardRequestAsync(method, parameters, (int)timeout.TotalMilliseconds).WaitAsync(ct),
            async () =>
            {
                await client.DisconnectAsync();
                client.Dispose();
            });

    internal static async Task<(SetupOperatorPairingSession? Session, StepResult? Failure)> OpenAsync(
        SetupContext ctx,
        string identityPath,
        CancellationToken ct)
    {
        var identity = new DeviceIdentity(identityPath);
        identity.Initialize();

        var credential = identity.DeviceToken ?? ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrEmpty(credential))
            return (null, StepResult.Fail("No credential available for the setup operator session"));

        var client = new OpenClawGatewayClient(
            ctx.GatewayUrl!,
            credential,
            logger: new SetupOpenClawLogger(ctx.Logger),
            identityPath: identityPath);
        try
        {
            PairOperatorStep.ApplyReconnectAuthorization(client, ctx);
            client.UseV2Signature = true;
            var outcome = await PairOperatorStep.WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);
            if (outcome == PairOperatorStep.ConnectionOutcome.Connected)
            {
                var session = FromConnectedClient(client);
                client = null;
                return (session, null);
            }

            return (null, PairOperatorStep.ConnectionFailureResult(ctx, "Setup operator session failed", outcome));
        }
        finally
        {
            if (client is not null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    public Task<PairingRequestResult> ListAsync(ApprovalRequestKind kind, TimeSpan timeout, CancellationToken ct)
        => SendAsync(MethodPrefix(kind) + ".list", parameters: null, timeout, ct);

    public Task<PairingRequestResult> ApproveAsync(ApprovalRequestKind kind, string requestId, TimeSpan timeout, CancellationToken ct)
        => SendAsync(MethodPrefix(kind) + ".approve", new { requestId = requestId.Trim() }, timeout, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _dispose();
    }

    private async Task<PairingRequestResult> SendAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var payload = await _send(method, parameters, timeout, ct);
            return PairingRequestResult.Ok(payload.GetRawText());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PairingRequestResult.Failed(ex.Message, "gateway rpc");
        }
    }

    private static string MethodPrefix(ApprovalRequestKind kind)
        => kind switch
        {
            ApprovalRequestKind.Device => "device.pair",
            ApprovalRequestKind.Node => "node.pair",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
