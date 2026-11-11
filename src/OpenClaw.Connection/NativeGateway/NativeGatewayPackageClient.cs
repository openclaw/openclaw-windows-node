using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection.NativeGateway;

public sealed record NativeGatewayCommandResult(int ExitCode, string StandardOutput, string StandardError = "");

/// <summary>Invokes only the installed package's qualified control alias and checks its JSON contract.</summary>
public sealed class NativeGatewayPackageClient
{
    public const string IsolatedContract = "isolated-session-v1";

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A long startup override must not kill the start command at three minutes, and a short
    /// override must not shorten setup or other package commands.
    /// </summary>
    internal static readonly TimeSpan CommandTimeout =
        IsolatedGatewayRuntime.StartupTimeout > DefaultCommandTimeout
            ? IsolatedGatewayRuntime.StartupTimeout
            : DefaultCommandTimeout;

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
            throw MissingContract();
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
        catch (NativeGatewayContractException)
        {
            // This probe is the initial contract check, so output that is not JSON means no contract.
            throw MissingContract();
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            bool isObject = root.ValueKind == JsonValueKind.Object;
            if (!isObject || !root.TryGetProperty("integration", out _))
            {
                if (isObject && root.TryGetProperty("session", out _))
                    throw MissingContract();
                if (IsLegacyProofVersion(package.Version))
                    return NativeGatewayContract.Legacy;
                throw MissingContract();
            }
            try
            {
                RequireIntegration(root, "status");
            }
            catch (ResponseShapeException)
            {
                // This probe is the initial contract check, so any envelope problem means no contract.
                throw MissingContract();
            }
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
        NativeGatewayCommandResult result, string command) =>
        Decode(result, command, root =>
        {
            RequireSuccess(root, result.ExitCode);
            JsonElement prepared = RequiredObject(root, "companion");
            int selectedPort = RequiredInt(prepared, "companion.port");
            string token = RequiredString(prepared, "companion.token");
            if (selectedPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("The Gateway package returned an invalid Companion credential.");
            return new IsolatedGatewayConfiguration(selectedPort, token);
        });

    public async Task SetupAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["setup", "--json"], null, cancellationToken).ConfigureAwait(false);
        Decode(result, "setup", root =>
        {
            RequireSuccess(root, result.ExitCode);
            JsonElement session = RequiredObject(root, "session");
            if (RequiredString(session, "session.state") != "ready" ||
                string.IsNullOrWhiteSpace(RequiredString(session, "session.sandboxId")))
                throw new NativeGatewayContractException(
                    "The Gateway MSIX has no ready isolated session. Run clawctl setup again.");
        });
    }

    public async Task<IsolatedGatewayStatus> StatusAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["gateway-service", "status", "--json"], null, cancellationToken).ConfigureAwait(false);
        return Decode(result, "gateway-service status", root => ReadStatus(root, result.ExitCode));
    }

    private static IsolatedGatewayStatus ReadStatus(JsonElement root, int exitCode)
    {
        JsonElement gateway = RequiredObject(root, "gateway");
        string state = RequiredString(gateway, "gateway.state");
        if (state is not ("running" or "not-started" or "stopped" or "starting" or "unhealthy" or "unknown"))
            throw new InvalidOperationException("The Gateway package reported an unsupported lifecycle state.");
        if (state == "running" && exitCode != 0)
            throw PackageError(root);
        string? sessionState = OptionalState(root, "session",
            ["not-configured", "running", "stale", "unavailable", "failed", "unusable", "unknown"]);
        string? readinessState = OptionalState(gateway, "gateway.readiness",
            ["absent", "not-ready", "startup-eligible", "unavailable", "unknown"]);
        string? readinessReason = OptionalString(gateway, "gateway.readiness", "reason");
        bool supportsLaunch = SupportsLaunchReceipts(root);
        IsolatedGatewayLaunch? launch = ReadLaunch(root, supportsLaunch);
        if (state != "running")
            return new IsolatedGatewayStatus(
                state, null, null, [], sessionState, readinessState, readinessReason)
                { SupportsLaunchReceipts = supportsLaunch, Launch = launch };

        int port = RequiredInt(gateway, "gateway.port");
        if (!gateway.TryGetProperty("ownership", out JsonElement ownership) ||
            ownership.ValueKind != JsonValueKind.Object)
            throw new NativeGatewayContractException(
                "The Gateway is running, but Companion cannot verify isolated listener ownership, " +
                "so it did not send credentials. Update Windows to a build supporting " +
                "SystemBasicProcessInformation (Windows 11 26100.4770 or later), then retry. " +
                "Running clawctl setup again cannot add this Windows capability.");
        _ = RequiredString(ownership, "gateway.ownership.sandboxId");
        string sid = RequiredString(ownership, "gateway.ownership.agentUserSid");
        JsonElement entries = Property(ownership, "gateway.ownership.listeners");
        if (entries.ValueKind != JsonValueKind.Array)
            throw WrongType("gateway.ownership.listeners", "an array", entries.ValueKind);
        if (string.IsNullOrWhiteSpace(sid) || port is < 1 or > 65535)
            throw new InvalidOperationException("The Gateway package did not provide listener ownership.");
        var listeners = new List<IsolatedGatewayListener>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw WrongType("gateway.ownership.listeners[]", "an object", entry.ValueKind);
            int listenerPort = RequiredInt(entry, "gateway.ownership.listeners[].port");
            int processId = RequiredInt(entry, "gateway.ownership.listeners[].processId");
            DateTime created = RequiredDateTime(entry, "gateway.ownership.listeners[].processStartTimeUtc");
            ulong sequence = RequiredUInt64(entry, "gateway.ownership.listeners[].sequenceNumber");
            if (listenerPort is < 1 or > 65535 || processId <= 0 || created == default ||
                sequence == 0)
                throw new InvalidOperationException("The Gateway package returned an invalid listener identity.");
            listeners.Add(new IsolatedGatewayListener(listenerPort, processId, created, sequence));
        }
        if (!listeners.Any(listener => listener.Port == port))
            throw new InvalidOperationException("The Gateway package did not attribute the observed port to its session.");
        return new IsolatedGatewayStatus(
            state, port, sid, listeners, sessionState, readinessState, readinessReason)
            { SupportsLaunchReceipts = supportsLaunch, Launch = launch };
    }

    public async Task<IsolatedGatewayLaunch> StartAsync(
        NativeGatewayPackage package, string attemptId, CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        var result = await InvokeAsync(package,
            ["gateway-service", "start", "--attempt-id", attemptId, "--json"], null, cancellationToken)
            .ConfigureAwait(false);
        return Decode(result, "gateway-service start", root =>
        {
            if (!SupportsLaunchReceipts(root))
                throw Shape("integration.gatewayLaunch", "is missing");
            var launch = ReadLaunch(root, true, requireDisposition: true)
                ?? throw Shape("gateway.launch", "is missing");
            if (launch.Disposition == "created" && launch.AttemptId != attemptId)
                throw Shape("gateway.launch.attemptId", "does not match the requested attempt");
            RequireSuccess(root, result.ExitCode);
            return launch;
        });
    }

    public async Task StopAsync(
        NativeGatewayPackage package, string attemptId, CancellationToken cancellationToken)
    {
        RequireAttemptId(attemptId);
        var result = await InvokeAsync(package,
            ["gateway-service", "stop", "--if-attempt-id", attemptId, "--json"], null, cancellationToken)
            .ConfigureAwait(false);
        Decode(result, "gateway-service stop", root =>
        {
            if (!SupportsLaunchReceipts(root))
                throw Shape("integration.gatewayLaunch", "is missing");
            var launch = RequiredObject(RequiredObject(root, "gateway"), "gateway.launch");
            if (RequiredInt(launch, "gateway.launch.version") != 1)
                throw Shape("gateway.launch.version", UnsupportedVersionProblem);
            if (RequiredString(launch, "gateway.launch.attemptId") != attemptId)
                throw Shape("gateway.launch.attemptId", "does not match the requested attempt");
            var outcome = RequiredString(launch, "gateway.launch.stopOutcome");
            if (outcome == "refused")
            {
                var reason = RequiredString(launch, "gateway.launch.refusal");
                if (reason is not ("attempt-mismatch" or "ownership-unverified") ||
                    result.ExitCode == 0 || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False ||
                    RequiredString(RequiredObject(root, "error"), "error.type") != "launch_ownership_refused")
                    throw Shape("gateway.launch.refusal", "is not a recognized refusal that the response confirms");
                throw new NativeGatewayLaunchOwnershipException(reason);
            }
            if (outcome is not ("stopped" or "not-running"))
                throw Shape("gateway.launch.stopOutcome", "has an unsupported value");
            RequireSuccess(root, result.ExitCode);
        });
    }

    public async Task StartAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "start", cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        await RunLifecycleAsync(package, "stop", cancellationToken).ConfigureAwait(false);

    public async Task<NativeGatewayDiagnosticsBundle> CollectLogsAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        // Without --output the package writes the ZIP under its own state root and reports the path.
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["collect-logs", "--json"], null, cancellationToken).ConfigureAwait(false);
        return Decode(result, "collect-logs", root =>
        {
            RequireSuccess(root, result.ExitCode);
            JsonElement bundle = RequiredObject(root, "bundle");
            string? path = null;
            if (bundle.TryGetProperty("path", out JsonElement pathValue) && pathValue.ValueKind != JsonValueKind.Null)
                path = pathValue.ValueKind == JsonValueKind.String
                    ? pathValue.GetString()
                    : throw WrongType("bundle.path", "a string or null", pathValue.ValueKind);
            string included = RequiredString(bundle, "bundle.included");
            var notes = new List<string>();
            if (bundle.TryGetProperty("notes", out JsonElement entries))
            {
                if (entries.ValueKind != JsonValueKind.Array)
                    throw WrongType("bundle.notes", "an array", entries.ValueKind);
                foreach (JsonElement entry in entries.EnumerateArray())
                    notes.Add(entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()!
                        : throw WrongType("bundle.notes[]", "a string", entry.ValueKind));
            }
            return new NativeGatewayDiagnosticsBundle(path, included, notes);
        });
    }

    private async Task RunLifecycleAsync(
        NativeGatewayPackage package, string operation, CancellationToken cancellationToken)
    {
        NativeGatewayCommandResult result = await InvokeAsync(
            package, ["gateway-service", operation, "--json"], null, cancellationToken).ConfigureAwait(false);
        Decode(result, $"gateway-service {operation}", root => RequireSuccess(root, result.ExitCode));
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
            throw UnexpectedResponse(command, "the output is not valid JSON");
        }
    }

    private static NativeGatewayContractException UnexpectedResponse(string command, string detail) =>
        new($"The Gateway package returned an unexpected response to 'clawctl {command}': {detail}. " +
            "Credentials were not sent. Retry, and if this keeps happening, include this message in a bug report.");

    // Parses the result and validates the envelope for `command`, then reads the operation's fields.
    // A field failure becomes a NativeGatewayContractException that names the operation and field,
    // so it is not mistaken for a Gateway that never exposed the initial contract.
    private static T Decode<T>(NativeGatewayCommandResult result, string command, Func<JsonElement, T> read)
    {
        using JsonDocument document = Parse(result, command);
        try
        {
            RequireIntegration(document.RootElement, command);
            return read(document.RootElement);
        }
        catch (ResponseShapeException ex)
        {
            throw UnexpectedResponse(command, ex.Message);
        }
    }

    private static void Decode(NativeGatewayCommandResult result, string command, Action<JsonElement> read) =>
        Decode<bool>(result, command, root =>
        {
            read(root);
            return true;
        });

    // Carries only a literal field path and a fixed problem phrase, never an observed value, so the
    // message is safe to show and to export.
    private sealed class ResponseShapeException(string detail) : Exception(detail);

    private const string UnsupportedVersionProblem = "has an unsupported value (expected 1)";

    private static ResponseShapeException Shape(string field, string problem) =>
        new($"'{field}' {problem}");

    private static ResponseShapeException WrongType(string field, string expected, JsonValueKind found) =>
        Shape(field, $"must be {expected}, found {found.ToString().ToLowerInvariant()}");

    private static void RequireIntegration(JsonElement root, string command)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw WrongType("response", "a JSON object", root.ValueKind);
        if (RequiredInt(root, "schemaVersion") != 1)
            throw Shape("schemaVersion", UnsupportedVersionProblem);
        if (RequiredString(root, "command") != command)
            throw Shape("command", $"is not '{command}'");
        // A malformed marker names its field. Only a well-formed marker for another kind or version is an unsupported package.
        JsonElement integration = RequiredObject(root, "integration");
        string kind = RequiredString(integration, "integration.kind");
        int version = RequiredInt(integration, "integration.version");
        if (kind != "isolated-session" || version != 1)
            throw MissingContract();
        _ = SupportsLaunchReceipts(root);
    }

    private static bool SupportsLaunchReceipts(JsonElement root)
    {
        var integration = RequiredObject(root, "integration");
        if (!integration.TryGetProperty("gatewayLaunch", out var advertised)) return false;
        if (advertised.ValueKind != JsonValueKind.Object)
            throw WrongType("integration.gatewayLaunch", "an object", advertised.ValueKind);
        if (RequiredInt(advertised, "integration.gatewayLaunch.version") != 1)
            throw Shape("integration.gatewayLaunch.version", UnsupportedVersionProblem);
        return true;
    }

    private static IsolatedGatewayLaunch? ReadLaunch(
        JsonElement root, bool supported, bool requireDisposition = false)
    {
        var gateway = RequiredObject(root, "gateway");
        if (!gateway.TryGetProperty("launch", out var launch)) return null;
        if (!supported)
            throw Shape("gateway.launch", "is present but integration.gatewayLaunch is not advertised");
        if (launch.ValueKind != JsonValueKind.Object)
            throw WrongType("gateway.launch", "an object", launch.ValueKind);
        if (RequiredInt(launch, "gateway.launch.version") != 1)
            throw Shape("gateway.launch.version", UnsupportedVersionProblem);
        var id = RequiredString(launch, "gateway.launch.attemptId");
        if (!IsAttemptId(id))
            throw Shape("gateway.launch.attemptId", "is not a valid attempt identifier");
        string? disposition = null;
        if (requireDisposition || launch.TryGetProperty("disposition", out _))
        {
            disposition = RequiredString(launch, "gateway.launch.disposition");
            if (disposition is not ("created" or "adopted"))
                throw Shape("gateway.launch.disposition", "has an unsupported value");
        }
        return new(id, disposition);
    }

    private static bool IsAttemptId(string id) =>
        Guid.TryParseExact(id, "N", out var parsed) && parsed != Guid.Empty && parsed.ToString("N") == id;

    // The caller supplies this id, so a bad one is not a response-shape problem.
    private static void RequireAttemptId(string id)
    {
        if (!IsAttemptId(id))
            throw MissingContract();
    }

    // The initial contract marker (integration kind and version) is absent or unsupported.
    private static NativeGatewayContractException MissingContract() =>
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

    // The field helpers take the full dotted path (for example "gateway.port") so a failure names
    // the rejected field; the last segment is the JSON property that is read.
    private static string Leaf(string path) => path[(path.LastIndexOf('.') + 1)..];

    private static JsonElement Property(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(Leaf(path), out JsonElement value)
            ? value
            : throw Shape(path, "is missing");

    private static JsonElement RequiredObject(JsonElement element, string path)
    {
        JsonElement value = Property(element, path);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw WrongType(path, "an object", value.ValueKind);
    }

    private static string RequiredString(JsonElement element, string path)
    {
        JsonElement value = Property(element, path);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw WrongType(path, "a string", value.ValueKind);
    }

    private static string? OptionalState(
        JsonElement parent, string objectPath, string[] supported)
    {
        string objectName = Leaf(objectPath);
        if (!parent.TryGetProperty(objectName, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw WrongType(objectPath, "an object", value.ValueKind);
        string state = RequiredString(value, objectPath + ".state");
        return supported.Contains(state)
            ? state
            : throw new InvalidOperationException(
                $"The Gateway package reported an unsupported {objectName} state.");
    }

    private static string? OptionalString(JsonElement parent, string objectPath, string propertyName)
    {
        if (!parent.TryGetProperty(Leaf(objectPath), out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw WrongType(objectPath, "an object", value.ValueKind);
        if (!value.TryGetProperty(propertyName, out JsonElement property))
            return null;
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw WrongType($"{objectPath}.{propertyName}", "a string", property.ValueKind);
    }

    private static int RequiredInt(JsonElement element, string path)
    {
        JsonElement value = Property(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw WrongType(path, "a 32-bit integer", value.ValueKind);
    }

    private static DateTime RequiredDateTime(JsonElement element, string path)
    {
        JsonElement value = Property(element, path);
        return value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out DateTime result)
            ? result.ToUniversalTime()
            : throw WrongType(path, "an ISO 8601 timestamp", value.ValueKind);
    }

    private static ulong RequiredUInt64(JsonElement element, string path)
    {
        JsonElement value = Property(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong result)
            ? result
            : throw WrongType(path, "an unsigned 64-bit integer", value.ValueKind);
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
        timeout.CancelAfter(CommandTimeout);
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
            throw new TimeoutException(
                $"The Gateway package did not respond within {(int)CommandTimeout.TotalSeconds} seconds. Retry setup.");
        }
    }
}
