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

    internal static bool IsSafeRequestId(string? requestId)
        => !string.IsNullOrWhiteSpace(requestId)
            && SafeRequestIdPattern().IsMatch(requestId.Trim());

    internal static string ApprovalCommand(ApprovalRequestKind kind)
        => $"openclaw {Noun(kind)} approve \"${RequestIdEnvironmentVariable}\" --json";

    // "plugins.entries.device-pair: plugin not found: device-pair" is emitted by older gateway
    // versions that ship without the device-pair plugin bundle or don't load it. Detecting this
    // lets callers return a Terminal (non-retriable) failure with actionable upgrade guidance.
    internal static bool IsPluginNotFoundError(string output)
        => output.Contains("plugin not found", StringComparison.OrdinalIgnoreCase);

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
