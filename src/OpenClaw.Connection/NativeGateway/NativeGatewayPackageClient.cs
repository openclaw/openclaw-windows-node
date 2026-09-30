using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Connection.NativeGateway;

public sealed record NativeGatewayCommandResult(int ExitCode, string StandardOutput);

/// <summary>Invokes only the installed package's qualified control alias and checks its JSON contract.</summary>
public sealed class NativeGatewayPackageClient
{
    public const string IsolatedContract = "isolated-session-v1";

    private readonly Func<NativeGatewayPackage, IReadOnlyList<string>, CancellationToken,
        Task<NativeGatewayCommandResult>> _invoke;

    public NativeGatewayPackageClient()
        : this(InvokePackageAsync)
    {
    }

    internal NativeGatewayPackageClient(
        Func<NativeGatewayPackage, IReadOnlyList<string>, CancellationToken,
            Task<NativeGatewayCommandResult>> invoke) => _invoke = invoke;

    public async Task<NativeGatewayContract> DetectAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (IsLegacyProofVersion(package.Version))
                return NativeGatewayContract.Legacy;
            throw Unsupported();
        }
        JsonDocument document;
        try
        {
            document = Parse(result.StandardOutput);
        }
        catch (InvalidOperationException) when (
            IsLegacyProofVersion(package.Version))
        {
            return NativeGatewayContract.Legacy;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("integration", out _))
            {
                if (root.TryGetProperty("session", out _))
                    throw Unsupported();
                if (IsLegacyProofVersion(package.Version))
                    return NativeGatewayContract.Legacy;
                throw Unsupported();
            }
            RequireIntegration(root, "status");
            return NativeGatewayContract.IsolatedSessionV1;
        }
    }

    public async Task<IsolatedGatewayConfiguration> PrepareAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["companion", "prepare", "--port", port.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "--json"], cancellationToken)
            .ConfigureAwait(false);
        return ReadCompanionConfiguration(result);
    }

    public async Task<IsolatedGatewayConfiguration> CheckAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["companion", "prepare", "--check", "--json"], cancellationToken)
            .ConfigureAwait(false);
        return ReadCompanionConfiguration(result);
    }

    private static IsolatedGatewayConfiguration ReadCompanionConfiguration(
        NativeGatewayCommandResult result)
    {
        using JsonDocument document = Parse(result.StandardOutput);
        JsonElement root = document.RootElement;
        RequireIntegration(root, "companion prepare");
        RequireSuccess(root, result.ExitCode);
        JsonElement prepared = RequiredObject(root, "companion");
        int selectedPort = RequiredInt(prepared, "port");
        string token = RequiredString(prepared, "token");
        if (selectedPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The Gateway package returned an invalid Companion credential.");
        return new IsolatedGatewayConfiguration(selectedPort, token);
    }

    public async Task SetupAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["setup", "--json"], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result.StandardOutput);
        JsonElement root = document.RootElement;
        RequireIntegration(root, "setup");
        RequireSuccess(root, result.ExitCode);
        JsonElement session = RequiredObject(root, "session");
        if (RequiredString(session, "state") != "ready" ||
            string.IsNullOrWhiteSpace(RequiredString(session, "sandboxId")))
            throw new NativeGatewayContractException(
                "The Gateway MSIX has no ready isolated session. Run clawctl setup again.");
    }

    public async Task<IsolatedGatewayStatus> StatusAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["gateway-service", "status", "--json"], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result.StandardOutput);
        JsonElement root = document.RootElement;
        RequireIntegration(root, "gateway-service status");
        JsonElement gateway = RequiredObject(root, "gateway");
        string state = RequiredString(gateway, "state");
        if (state is not ("running" or "not-started" or "stopped" or "starting" or "unhealthy" or "unknown"))
            throw new InvalidOperationException("The Gateway package reported an unsupported lifecycle state.");
        if (state == "running" && result.ExitCode != 0)
            throw PackageError(root);
        if (state != "running")
            return new IsolatedGatewayStatus(state, null, null, []);

        int port = RequiredInt(gateway, "port");
        if (!gateway.TryGetProperty("ownership", out JsonElement ownership) ||
            ownership.ValueKind != JsonValueKind.Object)
            throw new NativeGatewayContractException(
                "The Gateway is running, but Companion cannot verify isolated listener ownership, " +
                "so it did not send credentials. Update Windows to a build supporting " +
                "SystemBasicProcessInformation (Windows 11 26100.4770 or later), then retry. " +
                "Running clawctl setup again cannot add this Windows capability.");
        _ = RequiredString(ownership, "sandboxId");
        string sid = RequiredString(ownership, "agentUserSid");
        if (string.IsNullOrWhiteSpace(sid) || port is < 1 or > 65535 ||
            !ownership.TryGetProperty("listeners", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The Gateway package did not provide listener ownership.");
        var listeners = new List<IsolatedGatewayListener>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            int listenerPort = RequiredInt(entry, "port");
            int processId = RequiredInt(entry, "processId");
            DateTime created = RequiredDateTime(entry, "processStartTimeUtc");
            ulong sequence = RequiredUInt64(entry, "sequenceNumber");
            if (listenerPort is < 1 or > 65535 || processId <= 0 || created == default ||
                sequence == 0)
                throw new InvalidOperationException("The Gateway package returned an invalid listener identity.");
            listeners.Add(new IsolatedGatewayListener(listenerPort, processId, created, sequence));
        }
        if (!listeners.Any(listener => listener.Port == port))
            throw new InvalidOperationException("The Gateway package did not attribute the observed port to its session.");
        return new IsolatedGatewayStatus(state, port, sid, listeners);
    }

    public async Task StartAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "start", cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "stop", cancellationToken).ConfigureAwait(false);

    private async Task RunLifecycleAsync(
        NativeGatewayPackage package, string operation, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await _invoke(
            package, ["gateway-service", operation, "--json"], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result.StandardOutput);
        RequireIntegration(document.RootElement, $"gateway-service {operation}");
        RequireSuccess(document.RootElement, result.ExitCode);
    }

    private static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The Gateway package did not return a supported JSON contract. Update the Gateway MSIX.", ex);
        }
    }

    private static void RequireIntegration(JsonElement root, string command)
    {
        if (RequiredInt(root, "schemaVersion") != 1 ||
            RequiredString(root, "command") != command ||
            RequiredString(RequiredObject(root, "integration"), "kind") != "isolated-session" ||
            RequiredInt(RequiredObject(root, "integration"), "version") != 1)
            throw Unsupported();
    }

    private static NativeGatewayContractException Unsupported() =>
        new("The installed Gateway MSIX does not expose Companion's isolated-session contract. " +
            "Update the Gateway MSIX, then retry setup. Credentials were not sent.");

    private static bool IsLegacyProofVersion(string version) =>
        version is "0.0.0.0" or "0.0.0.1";

    private static void RequireSuccess(JsonElement root, int exitCode)
    {
        if (exitCode != 0 || !root.TryGetProperty("ok", out JsonElement ok) ||
            ok.ValueKind != JsonValueKind.True)
            throw PackageError(root);
    }

    private static InvalidOperationException PackageError(JsonElement root)
    {
        string? message = root.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return new InvalidOperationException(message ??
            "The Gateway package could not complete the request. Run clawctl status and retry.");
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw Unsupported();
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw Unsupported();
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
            throw Unsupported();
        return result;
    }

    private static DateTime RequiredDateTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTime(out DateTime result))
            throw Unsupported();
        return result.ToUniversalTime();
    }

    private static ulong RequiredUInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetUInt64(out ulong result))
            throw Unsupported();
        return result;
    }

    private static async Task<NativeGatewayCommandResult> InvokePackageAsync(
        NativeGatewayPackage package,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(package.ClawCtlAliasPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The Gateway package's clawctl alias could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return new NativeGatewayCommandResult(process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The Gateway package did not respond within three minutes. Retry setup.");
        }
    }
}
