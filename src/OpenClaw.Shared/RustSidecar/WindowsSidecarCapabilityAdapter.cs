using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.RustSidecar;

/// <summary>
/// Adapts authenticated Rust runtime messages to the Windows-owned capability dispatcher.
/// Process launch, credential bootstrap, and runtime selection deliberately stay outside this type.
/// </summary>
internal sealed class WindowsSidecarCapabilityAdapter
{
    private readonly NodeCapabilityDispatcher _dispatcher;
    private readonly string _nodeId;
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dispatcherCommandIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, Admission> _admittedInvocations = new(StringComparer.Ordinal);
    private int _maxAdmittedInvocations;
    private bool _configurationStarted;
    private bool _configured;
    private SidecarRuntimeConfiguration? _configuration;

    internal WindowsSidecarCapabilityAdapter(string nodeId, IOpenClawLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        _nodeId = nodeId;
        _dispatcher = new NodeCapabilityDispatcher(this, () => nodeId, logger);
    }

    internal IReadOnlyList<INodeCapability> Capabilities => _dispatcher.Capabilities;
    internal bool IsConfigured => _configured;

    internal event EventHandler<NodeInvokeCompletedEventArgs>? InvokeCompleted
    {
        add => _dispatcher.InvokeCompleted += value;
        remove => _dispatcher.InvokeCompleted -= value;
    }

    internal void RegisterCapability(INodeCapability capability)
    {
        if (_configurationStarted)
            throw new InvalidOperationException("Sidecar capabilities are immutable after configuration starts.");
        if (_dispatcher.Capabilities.Contains(capability))
            return;
        var commands = capability.Commands.ToArray();
        if (commands.Distinct(StringComparer.OrdinalIgnoreCase).Count() != commands.Length ||
            commands.Any(_dispatcherCommandIdentities.Contains))
        {
            throw new SidecarProtocolException(
                "Sidecar capability commands collide in the Windows dispatcher.");
        }
        _dispatcher.RegisterCapability(capability);
        foreach (var command in commands)
        {
            _commands.Add(command);
            _dispatcherCommandIdentities.Add(command);
        }
    }

    internal JsonObject BeginConfiguration(
        ulong manifestGeneration,
        SidecarProtocolSelection selection,
        uint maxInputBytes = 1_048_576,
        uint maxOutputBytes = 1_048_576,
        uint defaultTimeoutMs = 30_000,
        uint maxTimeoutMs = 120_000,
        uint resultGraceMs = 250)
    {
        if (_configurationStarted)
            throw new InvalidOperationException("Sidecar configuration may be sent only once.");
        if (manifestGeneration == 0 || manifestGeneration > SidecarJson.MaxPortableInteger)
            throw new ArgumentOutOfRangeException(nameof(manifestGeneration));
        if (selection.ProtocolMajor != AuthenticatedSidecarChannel.ProtocolMajor ||
            selection.ProtocolMinor > AuthenticatedSidecarChannel.ProtocolMinor ||
            selection.FeatureBits > SidecarJson.MaxPortableInteger ||
            selection.Limits.MaxFrameBytes < 65 ||
            selection.Limits.MaxInFlight == 0 ||
            selection.Limits.BootstrapTimeoutMs == 0)
        {
            throw new SidecarProtocolException("Invalid negotiated sidecar selection.");
        }

        var capabilities = _dispatcher.Capabilities
            .Select(capability => capability.Category)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var commands = _commands.Order(StringComparer.Ordinal).ToArray();
        ValidateNames(capabilities, commands);
        if (maxInputBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (maxOutputBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        var boundedInputBytes = Math.Min(maxInputBytes, selection.Limits.MaxFrameBytes);
        var boundedOutputBytes = Math.Min(maxOutputBytes, selection.Limits.MaxFrameBytes);
        if (boundedOutputBytes < MinimumBridgeFailureBytes())
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (defaultTimeoutMs == 0 || maxTimeoutMs == 0 ||
            defaultTimeoutMs > maxTimeoutMs || resultGraceMs >= defaultTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTimeoutMs));
        }

        _configuration = new SidecarRuntimeConfiguration(
            manifestGeneration,
            capabilities,
            commands,
            Math.Min((ushort)8, selection.Limits.MaxInFlight),
            boundedInputBytes,
            boundedOutputBytes,
            defaultTimeoutMs,
            maxTimeoutMs,
            resultGraceMs);
        _configurationStarted = true;
        _maxAdmittedInvocations = _configuration.MaxConcurrency;
        return _configuration.ToConfigureMessage();
    }

    internal void ConfirmConfigured(JsonElement message)
    {
        EnsureMessageShape(message, "configured", "manifest");
        if (!_configurationStarted || _configured || _configuration is null)
            throw new SidecarProtocolException("Unexpected sidecar configuration acknowledgement.");
        var manifest = SidecarJson.RequiredObject(message, "manifest");
        EnsureProperties(manifest, "manifestGeneration", "capabilities", "commands");
        if (SidecarJson.RequiredUInt64(manifest, "manifestGeneration") !=
                _configuration.ManifestGeneration ||
            !ReadStringArray(manifest, "capabilities").SequenceEqual(
                _configuration.Capabilities,
                StringComparer.Ordinal) ||
            !ReadStringArray(manifest, "commands").SequenceEqual(
                _configuration.Commands,
                StringComparer.Ordinal))
        {
            throw new SidecarProtocolException("Runtime acknowledged a different sidecar manifest.");
        }
        _configured = true;
    }

    internal async Task<JsonObject?> HandleRuntimeMessageAsync(
        JsonElement message,
        CancellationToken connectionCancellation)
    {
        if (!_configured)
            throw new SidecarProtocolException("Runtime traffic arrived before sidecar configuration completed.");
        var type = SidecarJson.RequiredString(message, "type");
        return type switch
        {
            "admission-request" => HandleAdmission(message),
            "invoke" => await HandleInvocationAsync(message, connectionCancellation),
            "cancel" => HandleCancellation(message),
            "status" => HandleStatus(message),
            _ => throw new SidecarProtocolException($"Unexpected configured sidecar message '{type}'.")
        };
    }

    internal void CancelAll()
    {
        lock (_admissionLock)
            _admittedInvocations.Clear();
        _dispatcher.CancelAll();
    }

    private JsonObject HandleAdmission(JsonElement message)
    {
        EnsureMessageShape(message, "admission-request", "invocation");
        var invocation = ParseInvocation(SidecarJson.RequiredObject(message, "invocation"));
        JsonObject decision;
        if (invocation.NodeId != _nodeId)
        {
            decision = Denial("WRONG_NODE", "invocation targets another Windows node");
        }
        else if (!SidecarJson.IsPortableJson(invocation.Parameters))
        {
            decision = Denial(
                "SIDECAR_NON_PORTABLE_JSON",
                "sidecar message contains an integer outside the exact JSON range");
        }
        else if (!InputWithinLimit(invocation.Parameters))
        {
            decision = Denial("INPUT_TOO_LARGE", "command parameters exceed the runtime limit");
        }
        else if (!_commands.Contains(invocation.Command))
        {
            decision = Denial(
                "COMMAND_NOT_ADVERTISED",
                "command is not present in the authenticated Windows manifest");
        }
        else if (!TryAdmit(invocation))
        {
            decision = Denial(
                "ADMISSION_SATURATED",
                "invocation id is duplicated or the authenticated admission bound is full");
        }
        else
        {
            decision = new JsonObject { ["outcome"] = "allow" };
        }
        return AdmissionDecision(invocation.Id, decision);
    }

    private async Task<JsonObject> HandleInvocationAsync(
        JsonElement message,
        CancellationToken connectionCancellation)
    {
        EnsureMessageShape(message, "invoke", "invocation");
        var invocation = ParseInvocation(SidecarJson.RequiredObject(message, "invocation"));
        if (!SidecarJson.IsPortableJson(invocation.Parameters))
        {
            ReleasePendingAdmission(invocation.Id);
            return NonPortableJsonFailure(invocation.Id);
        }
        if (!InputWithinLimit(invocation.Parameters))
        {
            ReleasePendingAdmission(invocation.Id);
            return ResultFailure(
                invocation.Id,
                "INPUT_TOO_LARGE",
                "command parameters exceed the runtime limit");
        }
        var activation = TryActivateAdmission(invocation);
        if (activation == AdmissionActivation.Missing)
            return ResultFailure(invocation.Id, "ADMISSION_REQUIRED", "invocation was not admitted by the Windows host");
        if (activation == AdmissionActivation.Mismatch)
            return ResultFailure(invocation.Id, "ADMISSION_MISMATCH", "invocation changed after Windows admission");
        if (invocation.NodeId != _nodeId || !_commands.Contains(invocation.Command))
        {
            ReleaseAdmission(invocation.Id);
            return ResultFailure(invocation.Id, "COMMAND_NOT_ADVERTISED", "command is not present in the authenticated Windows manifest");
        }
        var executionTimeout = ResolveTimeout(invocation.TimeoutMs);
        if (executionTimeout == TimeSpan.Zero)
        {
            ReleaseAdmission(invocation.Id);
            return ResultFailure(invocation.Id, "HANDLER_TIMEOUT", "command handler deadline already elapsed");
        }

        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmissionOnExit = true;
        var request = new NodeInvokeRequest
        {
            Id = invocation.Id,
            Command = invocation.Command,
            Args = invocation.Parameters,
            SessionKey = invocation.SessionKey
        };

        try
        {
            Task dispatch;
            lock (_admissionLock)
            {
                var admission = _admittedInvocations.GetValueOrDefault(invocation.Id);
                if (admission is null || admission.CancellationRequested)
                    return ResultFailure(invocation.Id, "WINDOWS_CAPABILITY", "cancelled");

                // DispatchAsync registers the invocation synchronously before scheduling its
                // worker. Holding the admission lock closes the activation-to-registration
                // window: cancellation either wins above, or observes a registered invocation.
                dispatch = _dispatcher.DispatchAsync(
                    request,
                    async response =>
                    {
                        handlerCompleted.TrySetResult();
                        try
                        {
                            completion.TrySetResult(response.Ok
                                ? await BuildSuccessResultAsync(invocation.Id, response.Payload)
                                : await BuildCapabilityFailureAsync(invocation.Id, response.Error));
                        }
                        finally
                        {
                            ReleaseAdmission(invocation.Id);
                        }
                    },
                    async error =>
                    {
                        handlerCompleted.TrySetResult();
                        try
                        {
                            completion.TrySetResult(
                                await BuildCapabilityFailureAsync(invocation.Id, error));
                        }
                        finally
                        {
                            ReleaseAdmission(invocation.Id);
                        }
                    },
                    connectionCancellation);
            }
            await dispatch;
            if (executionTimeout is null)
                return await completion.Task.WaitAsync(connectionCancellation);
            try
            {
                await handlerCompleted.Task.WaitAsync(executionTimeout.Value, connectionCancellation);
            }
            catch (TimeoutException)
            {
                releaseAdmissionOnExit = false;
                _dispatcher.TryCancel(invocation.Id);
                return ResultFailure(invocation.Id, "HANDLER_TIMEOUT", "command handler exceeded its deadline");
            }
            return await completion.Task.WaitAsync(connectionCancellation);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryCancel(invocation.Id);
            throw;
        }
        finally
        {
            if (releaseAdmissionOnExit)
                ReleaseAdmission(invocation.Id);
        }
    }

    private JsonObject? HandleCancellation(JsonElement message)
    {
        EnsureMessageShape(message, "cancel", "invocationId");
        var invocationId = SidecarJson.RequiredString(message, "invocationId");
        var active = false;
        lock (_admissionLock)
        {
            if (_admittedInvocations.TryGetValue(invocationId, out var admission))
            {
                if (admission.Active)
                {
                    admission.CancellationRequested = true;
                    active = true;
                }
                else
                {
                    _admittedInvocations.Remove(invocationId);
                }
            }
        }
        if (active)
            _dispatcher.TryCancel(invocationId);
        return null;
    }

    private JsonObject? HandleStatus(JsonElement message)
    {
        EnsureMessageShape(message, "status", "status");
        var status = SidecarJson.RequiredObject(message, "status");
        EnsureProperties(
            status,
            "state", "manifestGeneration", "runtimeVersion", "attempt", "reason");
        _ = SidecarJson.RequiredString(status, "state") switch
        {
            "configured" or "connecting" or "ready" or "backing-off" or
            "paused" or "draining" or "stopped" => true,
            _ => throw new SidecarProtocolException("Unknown sidecar runtime state.")
        };
        if (SidecarJson.RequiredUInt64(status, "manifestGeneration") != _configuration!.ManifestGeneration)
            throw new SidecarProtocolException("Sidecar status belongs to another manifest generation.");
        _ = SidecarJson.RequiredString(status, "runtimeVersion");
        _ = SidecarJson.RequiredUInt64(status, "attempt");
        var reason = status.GetProperty("reason");
        if (reason.ValueKind == JsonValueKind.String)
        {
            _ = reason.GetString() switch
            {
                "transport" or "gateway" or "request-timeout" or "event-lagged" or
                "activation" or "delivery-saturated" or "result-task" or
                "runtime-ended" or "shutdown" or "pairing" or "authentication" or
                "protocol" or "configuration" or "identity" => true,
                _ => throw new SidecarProtocolException("Unknown sidecar runtime reason.")
            };
        }
        else if (reason.ValueKind != JsonValueKind.Null)
        {
            throw new SidecarProtocolException("Sidecar runtime reason must be a string or null.");
        }
        return null;
    }

    private bool TryAdmit(SidecarInvocation invocation)
    {
        lock (_admissionLock)
        {
            if (_admittedInvocations.Count >= _maxAdmittedInvocations ||
                _admittedInvocations.ContainsKey(invocation.Id))
            {
                return false;
            }
            _admittedInvocations.Add(invocation.Id, new Admission(invocation));
            return true;
        }
    }

    private AdmissionActivation TryActivateAdmission(SidecarInvocation invocation)
    {
        lock (_admissionLock)
        {
            if (!_admittedInvocations.TryGetValue(invocation.Id, out var admission) || admission.Active)
                return AdmissionActivation.Missing;
            if (!InvocationEquals(admission.Invocation, invocation))
            {
                _admittedInvocations.Remove(invocation.Id);
                return AdmissionActivation.Mismatch;
            }
            admission.Active = true;
            return AdmissionActivation.Activated;
        }
    }

    private void ReleaseAdmission(string invocationId)
    {
        lock (_admissionLock)
            _admittedInvocations.Remove(invocationId);
    }

    private void ReleasePendingAdmission(string invocationId)
    {
        lock (_admissionLock)
        {
            if (_admittedInvocations.TryGetValue(invocationId, out var admission) && !admission.Active)
                _admittedInvocations.Remove(invocationId);
        }
    }

    private static bool InvocationEquals(SidecarInvocation left, SidecarInvocation right) =>
        left.Id == right.Id &&
        left.NodeId == right.NodeId &&
        left.Command == right.Command &&
        left.TimeoutMs == right.TimeoutMs &&
        left.IdempotencyKey == right.IdempotencyKey &&
        left.SessionKey == right.SessionKey &&
        SidecarJson.ValueEquals(left.Parameters, right.Parameters);

    private static JsonObject Denial(string code, string message) => new()
    {
        ["outcome"] = "deny",
        ["code"] = code,
        ["message"] = message
    };

    private static JsonObject AdmissionDecision(string invocationId, JsonObject decision) => new()
    {
        ["type"] = "admission-decision",
        ["invocationId"] = invocationId,
        ["decision"] = decision
    };

    internal static int MaximumAdmissionDecisionBytes(string invocationId)
    {
        var decisions = new[]
        {
            new JsonObject { ["outcome"] = "allow" },
            Denial("WRONG_NODE", "invocation targets another Windows node"),
            Denial(
                "SIDECAR_NON_PORTABLE_JSON",
                "sidecar message contains an integer outside the exact JSON range"),
            Denial("INPUT_TOO_LARGE", "command parameters exceed the runtime limit"),
            Denial(
                "COMMAND_NOT_ADVERTISED",
                "command is not present in the authenticated Windows manifest"),
            Denial(
                "ADMISSION_SATURATED",
                "invocation id is duplicated or the authenticated admission bound is full")
        };
        return decisions.Max(decision =>
            SidecarJson.Serialize(AdmissionDecision(invocationId, decision)).Length);
    }

    private async Task<JsonObject> BuildSuccessResultAsync(string invocationId, object? payload)
    {
        try
        {
            using var output = new BoundedWriteStream(_configuration!.MaxOutputBytes);
            await JsonSerializer.SerializeAsync(
                output,
                payload,
                payload?.GetType() ?? typeof(object),
                SidecarJson.SerializerOptions);
            var payloadJson = output.WrittenMemory.Span;
            if (!SidecarJson.IsPortableJson(SidecarJson.Parse(payloadJson)))
                return NonPortableJsonFailure(invocationId);
            var payloadNode = JsonNode.Parse(
                payloadJson,
                documentOptions: new JsonDocumentOptions { MaxDepth = SidecarJson.MaxDepth });
            return new JsonObject
            {
                ["type"] = "result",
                ["invocationId"] = invocationId,
                ["result"] = new JsonObject
                {
                    ["outcome"] = "success",
                    ["payload"] = payloadNode
                }
            };
        }
        catch (SidecarOutputLimitException)
        {
            return OutputTooLargeFailure(invocationId);
        }
        catch (Exception)
        {
            return ResultFailure(
                invocationId,
                "RESULT_SERIALIZATION",
                "Windows capability result could not be serialized");
        }
    }

    internal static JsonObject OutputTooLargeFailure(string invocationId) =>
        ResultFailure(invocationId, "OUTPUT_TOO_LARGE", "Windows capability result exceeds the negotiated output bound");

    internal static JsonObject MessageTooLargeFailure(string invocationId) =>
        ResultFailure(
            invocationId,
            "SIDECAR_MESSAGE_TOO_LARGE",
            "complete sidecar message exceeds the authenticated payload limit");

    internal static JsonObject NonPortableJsonFailure(string invocationId) =>
        ResultFailure(
            invocationId,
            "SIDECAR_NON_PORTABLE_JSON",
            "sidecar message contains an integer outside the exact JSON range");

    private async Task<JsonObject> BuildCapabilityFailureAsync(string invocationId, string? error)
    {
        var message = error ?? "Windows capability failed";
        try
        {
            using var output = new BoundedWriteStream(_configuration!.MaxOutputBytes);
            await JsonSerializer.SerializeAsync(
                output,
                new JsonObject
                {
                    ["code"] = "WINDOWS_CAPABILITY",
                    ["message"] = message
                },
                SidecarJson.SerializerOptions);
            return ResultFailure(invocationId, "WINDOWS_CAPABILITY", message);
        }
        catch (SidecarOutputLimitException)
        {
            return OutputTooLargeFailure(invocationId);
        }
        catch (Exception)
        {
            return ResultFailure(
                invocationId,
                "RESULT_SERIALIZATION",
                "Windows capability result could not be serialized");
        }
    }

    private static JsonObject ResultFailure(string invocationId, string code, string message) => new()
    {
        ["type"] = "result",
        ["invocationId"] = invocationId,
        ["result"] = new JsonObject
        {
            ["outcome"] = "failure",
            ["code"] = code,
            ["message"] = message
        }
    };

    private static uint MinimumBridgeFailureBytes()
    {
        var failures = new[]
        {
            ("SIDECAR_MESSAGE_TOO_LARGE", "complete sidecar message exceeds the authenticated payload limit"),
            ("SIDECAR_NON_PORTABLE_JSON", "sidecar message contains an integer outside the exact JSON range"),
            ("SIDECAR_CHANNEL_RETIRED", "authenticated sidecar channel is no longer live")
        };
        return checked((uint)failures.Max(failure => SidecarJson.Serialize(new JsonObject
        {
            ["code"] = failure.Item1,
            ["message"] = failure.Item2
        }).Length));
    }

    private bool InputWithinLimit(JsonElement parameters) =>
        JsonSerializer.SerializeToUtf8Bytes(parameters, SidecarJson.SerializerOptions).Length <=
            _configuration!.MaxInputBytes;

    private TimeSpan? ResolveTimeout(ulong? requestedTimeoutMs)
    {
        if (requestedTimeoutMs == 0)
            return null;
        if (requestedTimeoutMs is null)
            return TimeSpan.FromMilliseconds(_configuration!.DefaultTimeoutMs);
        var bounded = Math.Min(requestedTimeoutMs.Value, _configuration!.MaxTimeoutMs);
        var effective = bounded > _configuration.ResultGraceMs
            ? bounded - _configuration.ResultGraceMs
            : 0;
        return TimeSpan.FromMilliseconds(effective);
    }

    private static SidecarInvocation ParseInvocation(JsonElement invocation)
    {
        EnsureProperties(
            invocation,
            "id", "nodeId", "command", "params", "timeoutMs", "idempotencyKey", "sessionKey");
        if (!invocation.TryGetProperty("params", out var parameters))
            throw new SidecarProtocolException("Sidecar invocation is missing params.");
        var timeoutMs = OptionalUInt64(invocation, "timeoutMs");
        var idempotencyKey = OptionalString(invocation, "idempotencyKey");
        var id = SidecarJson.RequiredString(invocation, "id");
        var nodeId = SidecarJson.RequiredString(invocation, "nodeId");
        var command = SidecarJson.RequiredString(invocation, "command");
        if (id.Length == 0 || nodeId.Length == 0 || command.Length == 0)
            throw new SidecarProtocolException("Sidecar invocation identifiers must not be empty.");
        return new SidecarInvocation(
            id,
            nodeId,
            command,
            SidecarJson.NormalizeValue(parameters),
            timeoutMs,
            idempotencyKey,
            OptionalString(invocation, "sessionKey"));
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when value.GetString() is { Length: > 0 } text => text,
            JsonValueKind.String => throw new SidecarProtocolException(
                $"Sidecar field '{name}' must not be empty when present."),
            _ => throw new SidecarProtocolException($"Sidecar field '{name}' must be a string or null.")
        };
    }

    private static ulong? OptionalUInt64(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.TryGetUInt64(out var result) && result <= SidecarJson.MaxPortableInteger)
            return result;
        throw new SidecarProtocolException(
            $"Sidecar field '{name}' must be a portable unsigned integer or null.");
    }

    private static void ValidateNames(IReadOnlyList<string> capabilities, IReadOnlyList<string> commands)
    {
        foreach (var name in capabilities.Concat(commands))
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 ||
                name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            {
                throw new SidecarProtocolException($"Invalid sidecar manifest name '{name}'.");
            }
        }
        if (commands.Any(command =>
                command.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("system.", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SidecarProtocolException(
                "The current OpenClaw sidecar bridge does not yet admit the system command namespace.");
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new SidecarProtocolException($"Sidecar field '{name}' must be an array.");
        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new SidecarProtocolException($"Sidecar field '{name}' must contain strings.");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static void EnsureMessageShape(JsonElement message, string type, params string[] fields)
    {
        EnsureProperties(message, ["type", .. fields]);
        if (SidecarJson.RequiredString(message, "type") != type)
            throw new SidecarProtocolException($"Expected sidecar message '{type}'.");
    }

    private static void EnsureProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new SidecarProtocolException("Sidecar message must be an object.");
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!allowed.Contains(property.Name))
                throw new SidecarProtocolException($"Unknown sidecar field '{property.Name}'.");
        }
        if (count != allowed.Count || allowed.Any(name => !value.TryGetProperty(name, out _)))
            throw new SidecarProtocolException("Sidecar message is missing a required field.");
    }

    private sealed record SidecarInvocation(
        string Id,
        string NodeId,
        string Command,
        JsonElement Parameters,
        ulong? TimeoutMs,
        string? IdempotencyKey,
        string? SessionKey);

    private sealed class Admission(SidecarInvocation invocation)
    {
        internal SidecarInvocation Invocation { get; } = invocation;
        internal bool Active { get; set; }
        internal bool CancellationRequested { get; set; }
    }

    private enum AdmissionActivation
    {
        Activated,
        Missing,
        Mismatch
    }

    private sealed class BoundedWriteStream(uint maximumBytes) : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly long _maximumBytes = maximumBytes;

        internal ReadOnlyMemory<byte> WrittenMemory => _inner.GetBuffer().AsMemory(0, checked((int)_inner.Length));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || _inner.Length > _maximumBytes - additionalBytes)
                throw new SidecarOutputLimitException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SidecarOutputLimitException : Exception;
}
