using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

internal sealed record BrowserWslSetupFailure(string ReadState, bool Truncated, int Records,
    string? FailedStep, string? LastStep, string? LastOutcome, int? CommandExit,
    bool? CommandTimedOut, double? CommandElapsedMs, string[] ObservedCodes);

// Proof-only projection of the fixture's own JSONL. Never return messages, paths, argv or streams.
internal static class BrowserWslSetupDiagnostics
{
    internal const int MaxBytes = 262144;
    private const int MaxRecords = 1024;
    private static readonly HashSet<string> Steps = new(StringComparer.Ordinal)
    {
        "validate-distro-path", "preflight-os", "preflight-wsl", "preflight-port",
        "ensure-wsl-platform", "cleanup-distro", "cleanup-gateway", "wsl-create",
        "wsl-configure", "validate-wsl-lockdown", "install-cli", "configure-gateway",
        "install-service", "start-gateway", "restart-gateway", "mint-token",
        "pair-operator", "pair-node", "verify-e2e", "run-wizard",
        "windows-node-context", "start-keepalive"
    };
    private static readonly (string Needle, string Code)[] Codes =
    [
        ("StateDatabaseCoordinatorContentionError", "state_coordinator_contention"),
        ("GatewayRestartPreparationError", "gateway_restart_preparation"),
        ("Cannot record restart intent for the serving Gateway", "restart_intent_refused"),
        ("Cannot verify a live serving Gateway owner", "serving_owner_unverified"),
        ("ECONNREFUSED", "connection_refused"),
        ("ETIMEDOUT", "connection_timeout")
    ];
    private sealed record Command(int StepSequence, int Exit, bool? TimedOut, double? ElapsedMs, string[] Codes);

    internal static BrowserWslSetupFailure Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var count = (int)Math.Min(length, MaxBytes);
            var truncated = length > count;
            stream.Position = length - count;
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            // Starting at an arbitrary byte can split UTF-8 or a JSON record. Drop that first fragment.
            var offset = truncated ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
            if (truncated && offset == 0) return Empty("partial", true);
            return Project(new UTF8Encoding(false, true).GetString(bytes, offset, count - offset), truncated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Empty("unavailable", false);
        }
    }

    internal static BrowserWslSetupFailure Project(string jsonl, bool truncated = false)
    {
        if (Encoding.UTF8.GetByteCount(jsonl) > MaxBytes) return Empty("bounded", true);
        string? step = null, outcome = null, failedStep = null, failedRawStep = null;
        Command? lastCommand = null, failedCommand = null;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        int records = 0, stepSequence = 0;
        int? failedSequence = null;
        bool partial = false;
        var recent = new Queue<string>(MaxRecords);
        using (var reader = new StringReader(jsonl))
        {
            while (reader.ReadLine() is { } line)
            {
                if (recent.Count == MaxRecords) { recent.Dequeue(); truncated = true; partial = true; }
                recent.Enqueue(line);
            }
        }
        foreach (var line in recent)
        {
            records++;
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                var message = String(root, "msg");
                var rawStep = String(data, "step_id");
                if (rawStep is not null && (message?.StartsWith("step.started:", StringComparison.Ordinal) == true || message?.StartsWith("step.completed:", StringComparison.Ordinal) == true))
                {
                    if (message.StartsWith("step.started:", StringComparison.Ordinal))
                    {
                        stepSequence++;
                        lastCommand = null;
                    }
                    step = ClosedStep(rawStep, message);
                    var value = String(data, "outcome");
                    outcome = value is "Success" or "Failed" or "Skipped" or "Cancelled" ? value : value is null ? null : "other";
                    if (value == "Failed" && failedStep is null)
                    {
                        failedStep = step; failedRawStep = rawStep; failedSequence = stepSequence;
                        failedCommand = lastCommand?.StepSequence == stepSequence ? lastCommand : null;
                        if (failedCommand is not null) codes.UnionWith(failedCommand.Codes);
                        codes.UnionWith(Classify(data));
                    }
                }
                // Freeze the command preceding the primary failed step, not later rollback commands.
                if (failedStep is null && message?.StartsWith("cmd.done:", StringComparison.Ordinal) == true && data.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var exitCode))
                {
                    bool? timedOut = data.TryGetProperty("timed_out", out var timed) && timed.ValueKind is JsonValueKind.True or JsonValueKind.False ? timed.GetBoolean() : null;
                    double? elapsed = data.TryGetProperty("elapsed_ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 3600000 ? number : null;
                    // Correlate by the step-start boundary because all sanitized IDs may be identical.
                    lastCommand = new(stepSequence, exitCode, timedOut, elapsed, exitCode == 0 ? [] : Classify(data));
                }
                if (failedRawStep is not null && failedSequence == stepSequence && rawStep == failedRawStep && message?.StartsWith("step.exception:", StringComparison.Ordinal) == true) codes.UnionWith(Classify(data));
            }
            catch (JsonException) { partial = true; }
        }
        return new(partial ? "partial" : records == 0 ? "empty" : "observed", truncated, records,
            failedStep, step, outcome, failedCommand?.Exit, failedCommand?.TimedOut,
            failedCommand?.ElapsedMs, codes.Order(StringComparer.Ordinal).ToArray());
    }

    private static string ClosedStep(string rawStep, string message)
    {
        if (Steps.Contains(rawStep)) return rawStep;
        // SetupLogger intentionally redacts *_id metadata. Its completion message has
        // the public step ID. Match a closed producer prefix, never export arbitrary text.
        if (rawStep == "[REDACTED]")
            foreach (var known in Steps)
                if (message.StartsWith($"step.completed: {known} → ", StringComparison.Ordinal))
                    return known;
        return "other";
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string[] Classify(JsonElement data) => Codes.Where(pair =>
        new[] { "message", "exception", "exception_type", "stdout", "stderr" }
            .Any(field => String(data, field)?.Contains(pair.Needle, StringComparison.Ordinal) == true))
        .Select(pair => pair.Code).ToArray();
    private static BrowserWslSetupFailure Empty(string state, bool truncated) =>
        new(state, truncated, 0, null, null, null, null, null, null, []);
}
