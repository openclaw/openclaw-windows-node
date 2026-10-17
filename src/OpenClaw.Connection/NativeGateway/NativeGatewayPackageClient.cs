using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection.NativeGateway;

public sealed record NativeGatewayCommandResult(int ExitCode, string StandardOutput, string StandardError = "");

/// <summary>Invokes only the installed package's qualified control alias and checks its JSON contract.</summary>
public sealed class NativeGatewayPackageClient
{
    public const string IsolatedContract = "isolated-session-v1";

    private readonly Func<NativeGatewayPackage, IReadOnlyList<string>, string?, CancellationToken,
        Task<NativeGatewayCommandResult>> _invoke;
    private readonly IOpenClawLogger _logger;

    public NativeGatewayPackageClient(IOpenClawLogger? logger = null)
        : this(InvokePackageAsync, logger)
    {
    }

    internal NativeGatewayPackageClient(
        Func<NativeGatewayPackage, IReadOnlyList<string>, CancellationToken,
            Task<NativeGatewayCommandResult>> invoke,
        IOpenClawLogger? logger = null)
        : this((package, arguments, _, cancellationToken) => invoke(package, arguments, cancellationToken), logger)
    {
    }

    internal NativeGatewayPackageClient(
        Func<NativeGatewayPackage, IReadOnlyList<string>, string?, CancellationToken,
            Task<NativeGatewayCommandResult>> invoke,
        IOpenClawLogger? logger = null)
    {
        _invoke = invoke;
        _logger = logger ?? NullLogger.Instance;
    }

    private async Task<NativeGatewayCommandResult> InvokeAsync(
        NativeGatewayPackage package,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        // Arguments never carry secrets: the restore token travels through stdin,
        // and stdin is never logged.
        string command = "clawctl " + string.Join(' ', arguments);
        _logger.Debug($"Running {command}");
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            NativeGatewayCommandResult result = await _invoke(
                package, arguments, standardInput, cancellationToken).ConfigureAwait(false);
            LogCompletion(command, result, Stopwatch.GetElapsedTime(startedAt));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"{command} failed after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0} ms: {ex.GetType().Name}: {TokenSanitizer.SanitizeLogMessage(ex.Message)}");
            throw;
        }
    }

    private void LogCompletion(string command, NativeGatewayCommandResult result, TimeSpan elapsed)
    {
        void Level(string message)
        {
            if (result.ExitCode == 0) _logger.Debug(message);
            else _logger.Warn(message);
        }
        Level($"{command} exited {result.ExitCode} in {elapsed.TotalMilliseconds:0} ms");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            Level($"{command} stderr: {Tail(TokenSanitizer.SanitizeLogMessage(result.StandardError.Trim()), 2000)}");
    }

    private static string Tail(string input, int max) =>
        input.Length <= max ? input : "..." + input[^max..];

    public async Task<NativeGatewayContract> DetectAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["status", "--json"], null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (IsLegacyProofVersion(package.Version))
                return NativeGatewayContract.Legacy;
            throw Unsupported();
        }
        JsonDocument document;
        try
        {
            document = Parse(result, "status");
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
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["companion", "prepare", "--port", port.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "--json"], null, cancellationToken)
            .ConfigureAwait(false);
        return ReadCompanionConfiguration(result, "companion prepare");
    }

    public async Task<IsolatedGatewayConfiguration> RestoreAsync(
        NativeGatewayPackage package, int port, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new InvalidOperationException("Companion's saved Gateway token cannot be restored safely.");
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["companion", "prepare", "--port", port.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "--restore-token-stdin", "--json"],
            token, cancellationToken).ConfigureAwait(false);
        return ReadCompanionConfiguration(result, "companion prepare");
    }

    public async Task<IsolatedGatewayConfiguration> CheckAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["companion", "prepare", "--check", "--json"], null, cancellationToken)
            .ConfigureAwait(false);
        return ReadCompanionConfiguration(result, "companion prepare");
    }

    private static IsolatedGatewayConfiguration ReadCompanionConfiguration(
        NativeGatewayCommandResult result, string command)
    {
        using JsonDocument document = Parse(result, command);
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
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["setup", "--json"], null, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result, "setup");
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
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["gateway-service", "status", "--json"], null, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result, "gateway-service status");
        JsonElement root = document.RootElement;
        RequireIntegration(root, "gateway-service status");
        JsonElement gateway = RequiredObject(root, "gateway");
        string state = RequiredString(gateway, "state");
        if (state is not ("running" or "not-started" or "stopped" or "starting" or "unhealthy" or "unknown"))
            throw new InvalidOperationException("The Gateway package reported an unsupported lifecycle state.");
        if (state == "running" && result.ExitCode != 0)
            throw PackageError(root);
        string? sessionState = OptionalState(root, "session",
            ["not-configured", "running", "stale", "unavailable", "failed", "unusable", "unknown"]);
        string? readinessState = OptionalState(gateway, "readiness",
            ["absent", "not-ready", "startup-eligible", "unavailable", "unknown"]);
        string? readinessReason = OptionalString(gateway, "readiness", "reason");
        if (state != "running")
            return new IsolatedGatewayStatus(
                state, null, null, [], sessionState, readinessState, readinessReason);

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
        return new IsolatedGatewayStatus(
            state, port, sid, listeners, sessionState, readinessState, readinessReason);
    }

    public async Task StartAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "start", cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "stop", cancellationToken).ConfigureAwait(false);

    private async Task RunLifecycleAsync(
        NativeGatewayPackage package, string operation, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["gateway-service", operation, "--json"], null, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = Parse(result, $"gateway-service {operation}");
        RequireIntegration(document.RootElement, $"gateway-service {operation}");
        RequireSuccess(document.RootElement, result.ExitCode);
    }

    private static JsonDocument Parse(NativeGatewayCommandResult result, string command)
    {
        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            // Stderr stays out of exception messages: the tray shows these too.
            // It is logged at the InvokeAsync boundary instead.
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"The Gateway package command 'clawctl {command}' failed with exit code {result.ExitCode} and returned no JSON result.",
                    ex);
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

    private static string? OptionalState(
        JsonElement parent, string objectName, string[] supported)
    {
        if (!parent.TryGetProperty(objectName, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported();
        string state = RequiredString(value, "state");
        return supported.Contains(state)
            ? state
            : throw new InvalidOperationException(
                $"The Gateway package reported an unsupported {objectName} state.");
    }

    private static string? OptionalString(JsonElement parent, string objectName, string propertyName)
    {
        if (!parent.TryGetProperty(objectName, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported();
        if (!value.TryGetProperty(propertyName, out JsonElement property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : throw Unsupported();
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
        string? standardInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(package.ClawCtlAliasPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The Gateway package's clawctl alias could not be started.");
        return await CompleteInvocationAsync(process, standardInput, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<NativeGatewayCommandResult> CompleteInvocationAsync(
        Process process,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), timeout.Token)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            return new NativeGatewayCommandResult(process.ExitCode, stdout, stderr);
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
