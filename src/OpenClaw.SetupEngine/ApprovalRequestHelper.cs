using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

internal enum ApprovalRequestKind
{
    Device,
    Node
}

internal static partial class ApprovalRequestHelper
{
    internal const string RequestIdEnvironmentVariable = "OPENCLAW_APPROVAL_REQUEST_ID";

    internal static async Task<PendingRequestBaseline> CapturePendingRequestBaselineAsync(
        SetupContext ctx,
        ApprovalRequestKind kind,
        CancellationToken ct)
    {
        var authorization = ctx.SharedGatewayToken ?? ctx.BootstrapToken;
        if (string.IsNullOrWhiteSpace(authorization))
            return PendingRequestBaseline.Fail("No gateway token is available to capture the pending approval baseline.");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = authorization };
        var noun = Noun(kind);
        var pending = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            $"""{ctx.WslPathPrefix} && openclaw {noun} list --json""",
            TimeSpan.FromSeconds(30),
            env,
            ct);

        var output = $"{pending.Stdout.Trim()} {pending.Stderr.Trim()}".Trim();
        if (pending.ExitCode != 0)
        {
            return PendingRequestBaseline.Fail(
                $"Could not capture pending {noun} before opening the setup socket (exit {pending.ExitCode}): {output}",
                IsPluginNotFoundError(output));
        }

        var parsed = TryReadPendingRequestIds(pending.Stdout.Trim());
        if (parsed.Success)
            return PendingRequestBaseline.SuccessResult(parsed.RequestIds);

        if (IsExplicitNoPendingMessage(pending.Stdout))
            return PendingRequestBaseline.SuccessResult([]);

        return PendingRequestBaseline.Fail(
            $"Could not capture pending {noun} before opening the setup socket: {parsed.Error}");
    }

    internal static bool IsSafeRequestId(string? requestId)
        => !string.IsNullOrWhiteSpace(requestId)
            && SafeRequestIdPattern().IsMatch(requestId.Trim());

    internal static string ApprovalCommand(ApprovalRequestKind kind)
        => $"openclaw {Noun(kind)} approve \"${RequestIdEnvironmentVariable}\" --json";

    // "plugins.entries.device-pair: plugin not found: device-pair" is emitted by older gateway
    // versions that ship without the device-pair plugin bundle or don't load it. Detecting this
    // lets callers return a Terminal (non-retriable) failure with actionable upgrade guidance.
    internal static bool IsPluginNotFoundError(string output)
        => output.Contains("plugin not found", StringComparison.OrdinalIgnoreCase)
            && output.Contains("device-pair", StringComparison.OrdinalIgnoreCase);

    internal const string PluginNotFoundMessage =
        "The gateway device-pair plugin is not loaded. " +
        "Upgrade your gateway to version 2026.6.0 or later and re-run setup.";

    internal static Dictionary<string, string> AddRequestIdEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string requestId)
    {
        if (!IsSafeRequestId(requestId))
            throw new ArgumentException("Unsafe approval request ID.", nameof(requestId));

        var result = new Dictionary<string, string>(environment)
        {
            [RequestIdEnvironmentVariable] = requestId.Trim()
        };
        return result;
    }

    internal static RequestIdParseResult TryReadSelectedRequestId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return RequestIdParseResult.NotFound("Approval output was empty.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("selected", out var selected) ||
                selected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return RequestIdParseResult.NotFound("Approval output did not include a selected request.");
            }

            return TryReadRequestId(selected);
        }
        catch (JsonException ex)
        {
            return RequestIdParseResult.NotFound($"Approval output was not valid JSON: {ex.Message}");
        }
    }

    internal static RequestIdParseResult TryReadApprovedRequestId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return RequestIdParseResult.NotFound("Approval output was empty.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadRequestId(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return RequestIdParseResult.NotFound($"Approval output was not valid JSON: {ex.Message}");
        }
    }

    internal static RequestIdParseResult TrySelectPendingRequestForDevice(
        string json,
        string? deviceId,
        IReadOnlySet<string> requestIdsBeforeConnect,
        bool matchNodeId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return RequestIdParseResult.NotFound("Operator device ID is missing, so no pending request can be bound to the socket setup opened.");

        if (string.IsNullOrWhiteSpace(json))
            return RequestIdParseResult.NotFound("Pending approval output was empty.");

        var wantedDeviceId = deviceId.Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pending", out var pending) ||
                pending.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return RequestIdParseResult.NotFound("No pending approval request was found.");
            }

            if (pending.ValueKind != JsonValueKind.Array)
                return RequestIdParseResult.NotFound("Pending approval output did not contain an array.");

            string? match = null;
            foreach (var item in pending.EnumerateArray())
            {
                if (!PendingItemMatchesDevice(item, wantedDeviceId, matchNodeId))
                    continue;

                if (!RoleMatchesSelection(item, matchNodeId))
                    continue;

                var parsed = TryReadRequestId(item);
                if (!parsed.Success)
                    return RequestIdParseResult.NotFound(parsed.Error ?? "Pending approval request did not include a safe request ID.");

                if (requestIdsBeforeConnect.Contains(parsed.RequestId!))
                    continue;

                if (match is not null)
                    return RequestIdParseResult.NotFound("Multiple new pending approval requests match the socket setup opened; refusing to auto-approve an ambiguous request.");

                match = parsed.RequestId;
            }

            return match is null
                ? RequestIdParseResult.NotFound("No new pending approval request matched the socket setup opened.")
                : RequestIdParseResult.Found(match);
        }
        catch (JsonException ex)
        {
            return RequestIdParseResult.NotFound($"Pending approval output was not valid JSON: {ex.Message}");
        }
    }

    internal static bool IsNothingToDrain(RequestIdParseResult parsed)
    {
        if (parsed.Success || string.IsNullOrWhiteSpace(parsed.Error))
            return false;

        return parsed.Error.Contains("No pending approval request was found.", StringComparison.Ordinal)
            || parsed.Error.Contains("No new pending approval request matched the socket setup opened.", StringComparison.Ordinal)
            || parsed.Error.Contains("Operator device ID is missing", StringComparison.Ordinal);
    }

    private static bool PendingItemMatchesDevice(JsonElement item, string wantedDeviceId, bool matchNodeId)
    {
        var matched = false;
        if (item.TryGetProperty("deviceId", out var deviceElement) &&
            deviceElement.ValueKind == JsonValueKind.String &&
            string.Equals(deviceElement.GetString()?.Trim(), wantedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            matched = true;
        }

        if (matchNodeId &&
            item.TryGetProperty("nodeId", out var nodeElement) &&
            nodeElement.ValueKind == JsonValueKind.String &&
            string.Equals(nodeElement.GetString()?.Trim(), wantedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            matched = true;
        }

        return matched;
    }

    private static bool RoleMatchesSelection(JsonElement item, bool matchNodeId)
    {
        if (!item.TryGetProperty("role", out var roleElement) ||
            roleElement.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var role = roleElement.GetString()?.Trim();
        if (string.IsNullOrEmpty(role))
            return true;

        return matchNodeId
            ? !string.Equals(role, "operator", StringComparison.OrdinalIgnoreCase)
            : string.Equals(role, "operator", StringComparison.OrdinalIgnoreCase);
    }

    internal static RequestIdParseResult TryReadSinglePendingRequestId(string json)
    {
        var all = TryReadPendingRequestIds(json);
        if (!all.Success)
            return RequestIdParseResult.NotFound(all.Error ?? "Could not read pending approval requests.");

        return all.RequestIds.Count switch
        {
            0 => RequestIdParseResult.NotFound("No pending approval request was found."),
            1 => RequestIdParseResult.Found(all.RequestIds[0]),
            _ => RequestIdParseResult.NotFound("Multiple pending approval requests were found; refusing to auto-approve an ambiguous request.")
        };
    }

    internal static PendingRequestIdsParseResult TryReadPendingRequestIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PendingRequestIdsParseResult.Fail("Pending approval output was empty.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pending", out var pending) ||
                pending.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return PendingRequestIdsParseResult.SuccessResult([]);
            }

            if (pending.ValueKind != JsonValueKind.Array)
                return PendingRequestIdsParseResult.Fail("Pending approval output did not contain an array.");

            var requestIds = new List<string>();
            foreach (var item in pending.EnumerateArray())
            {
                var parsed = TryReadRequestId(item);
                if (!parsed.Success)
                    return PendingRequestIdsParseResult.Fail(parsed.Error ?? "Pending approval request did not include a safe request ID.");

                requestIds.Add(parsed.RequestId!);
            }

            return PendingRequestIdsParseResult.SuccessResult(requestIds);
        }
        catch (JsonException ex)
        {
            return PendingRequestIdsParseResult.Fail($"Pending approval output was not valid JSON: {ex.Message}");
        }
    }

    private static RequestIdParseResult TryReadRequestId(JsonElement element)
    {
        if (!element.TryGetProperty("requestId", out var requestIdElement) ||
            requestIdElement.ValueKind != JsonValueKind.String)
        {
            return RequestIdParseResult.NotFound("Approval request did not include requestId.");
        }

        var requestId = requestIdElement.GetString()?.Trim();
        return IsSafeRequestId(requestId)
            ? RequestIdParseResult.Found(requestId!)
            : RequestIdParseResult.NotFound("Approval request ID contained unsafe characters.");
    }

    private static string Noun(ApprovalRequestKind kind)
        => kind switch
        {
            ApprovalRequestKind.Device => "devices",
            ApprovalRequestKind.Node => "nodes",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static bool IsExplicitNoPendingMessage(string output)
    {
        var message = output.Trim().TrimEnd('.');
        return string.Equals(message, "No pending device approvals", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message, "No pending node approvals", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled)]
    private static partial Regex SafeRequestIdPattern();
}

internal sealed record RequestIdParseResult(bool Success, string? RequestId, string? Error)
{
    public static RequestIdParseResult Found(string requestId) => new(true, requestId, null);
    public static RequestIdParseResult NotFound(string error) => new(false, null, error);
}

internal sealed record PendingRequestIdsParseResult(bool Success, IReadOnlyList<string> RequestIds, string? Error)
{
    public static PendingRequestIdsParseResult SuccessResult(IReadOnlyList<string> requestIds) => new(true, requestIds, null);
    public static PendingRequestIdsParseResult Fail(string error) => new(false, [], error);
}

internal sealed record PendingRequestBaseline(
    bool Success,
    IReadOnlySet<string> RequestIds,
    string? Error,
    bool PluginNotFound)
{
    public static PendingRequestBaseline SuccessResult(IEnumerable<string> requestIds) =>
        new(true, new HashSet<string>(requestIds, StringComparer.Ordinal), null, false);

    public static PendingRequestBaseline Fail(string error, bool pluginNotFound = false) =>
        new(false, new HashSet<string>(StringComparer.Ordinal), error, pluginNotFound);
}
