using System.Collections.Generic;
using System.Text.Json;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Phase 1 of the V2 exec approval pipeline: structural input validation.
/// Parses a raw NodeInvokeRequest into a ValidatedRunRequest or returns validation-failed.
/// Does not resolve executables, detect shell wrappers, or evaluate policy.
/// </summary>
public static class ExecApprovalV2InputValidator
{
    private const int DefaultTimeoutMs = 30_000;

    public static ExecApprovalV2ValidationOutcome Validate(NodeInvokeRequest request)
    {
        if (request.Args.ValueKind == JsonValueKind.Object
            && request.Args.TryGetProperty("command", out var commandElement)
            && commandElement.ValueKind == JsonValueKind.String)
        {
            return Deny("command-array-required");
        }

        var argv = TryParseArgv(request.Args, out bool malformedCommand);
        if (malformedCommand)
            return Deny("malformed-command");
        if (argv == null || argv.Length == 0)
            return Deny("missing-command");
        if (string.IsNullOrWhiteSpace(argv[0]))
            return Deny("empty-command");

        // cwd — optional, but empty/whitespace is a caller error; wrong type is a protocol violation
        string? cwd = null;
        if (request.Args.ValueKind == JsonValueKind.Object &&
            request.Args.TryGetProperty("cwd", out var cwdEl))
        {
            if (cwdEl.ValueKind != JsonValueKind.String)
                return Deny("malformed-cwd");
            var rawCwd = cwdEl.GetString();
            if (string.IsNullOrWhiteSpace(rawCwd))
                return Deny("empty-cwd");
            cwd = rawCwd;
        }

        // env — must be a JSON object if present; non-string values are a protocol violation
        IReadOnlyDictionary<string, string>? env = null;
        if (request.Args.ValueKind == JsonValueKind.Object &&
            request.Args.TryGetProperty("env", out var envEl))
        {
            if (envEl.ValueKind != JsonValueKind.Object)
                return Deny("malformed-env");
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in envEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                    return Deny("malformed-env");
                dict[prop.Name] = prop.Value.GetString() ?? "";
            }
            if (dict.Count > 0)
                return Deny("custom-env-not-supported");
        }

        // timeoutMs / timeout — positive integer; defaults to 30 000.
        // Upper-bound clamping (legacy safety limit) is enforced in the execution/policy phase, not here.
        var timeoutMs = DefaultTimeoutMs;
        if (request.Args.ValueKind == JsonValueKind.Object)
        {
            if (request.Args.TryGetProperty("timeoutMs", out var tmsEl))
            {
                if (tmsEl.ValueKind != JsonValueKind.Number || !tmsEl.TryGetInt32(out var v) || v <= 0)
                    return Deny("invalid-timeout");
                timeoutMs = v;
            }
            else if (request.Args.TryGetProperty("timeout", out var tEl))
            {
                if (tEl.ValueKind != JsonValueKind.Number || !tEl.TryGetInt32(out var v) || v <= 0)
                    return Deny("invalid-timeout");
                timeoutMs = v;
            }
        }

        // rawCommand — optional display text. When present it must agree with argv, so
        // the text shown to an operator can never describe a different command than the
        // one that will run. The gateway enforces this too (RAW_COMMAND_MISMATCH); the
        // node repeats it so a request that reaches the node by another path is still
        // checked.
        string? rawCommand = null;
        if (request.Args.ValueKind == JsonValueKind.Object &&
            request.Args.TryGetProperty("rawCommand", out var rawEl))
        {
            if (rawEl.ValueKind != JsonValueKind.String)
                return Deny("malformed-raw-command");
            rawCommand = rawEl.GetString();
            if (!ExecRawCommandConsistency.IsConsistent(rawCommand, argv))
                return Deny("raw-command-mismatch");
        }

        return ExecApprovalV2ValidationOutcome.Ok(new ValidatedRunRequest(
            argv,
            cwd,
            timeoutMs,
            env,
            TryGetString(request.Args, "agentId"),
            request.SessionKey,
            rawCommand));
    }

    private static ExecApprovalV2ValidationOutcome Deny(string reason)
        => ExecApprovalV2ValidationOutcome.Fail(ExecApprovalV2Result.ValidationFailed(reason));

    private static string[]? TryParseArgv(JsonElement args, out bool malformed)
    {
        malformed = false;
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("command", out var cmdEl))
            return null;

        if (cmdEl.ValueKind != JsonValueKind.Array)
        {
            malformed = true;
            return null;
        }

        var list = new List<string>();
        foreach (var item in cmdEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { malformed = true; return null; }
            list.Add(item.GetString() ?? "");
        }
        return list.Count > 0 ? [.. list] : null;
    }

    private static string? TryGetString(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(key, out var el) ||
            el.ValueKind != JsonValueKind.String)
            return null;
        return el.GetString();
    }
}
