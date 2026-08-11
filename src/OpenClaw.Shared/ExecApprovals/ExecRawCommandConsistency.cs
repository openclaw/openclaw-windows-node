using System;
using System.Collections.Generic;
using System.Text;
using OpenClaw.Shared.Commands;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Node-side consistency check between the request's <c>rawCommand</c> and its
/// <c>command</c> argv.
///
/// The gateway is the authority here and rejects a mismatch with
/// RAW_COMMAND_MISMATCH. This is defense in depth for the node: it makes the node
/// refuse a request whose human-readable text disagrees with the argv it would
/// actually run, so a display string can never be used to describe one command to
/// an operator while a different one executes.
///
/// Being defense in depth, it must never be STRICTER than the gateway, or valid
/// traffic breaks. Two accepted forms exist and both are honored:
///
///  1. The formatted argv. The gateway's formatter quotes only on space, double
///     quote, or empty string, while this product's display formatter also quotes on
///     the wider shell-metacharacter set. Both renderings are accepted because a
///     rawCommand that the gateway produced must not be rejected here, and because
///     the local display form is what this node's own prepare step echoes back.
///  2. For a canonical cmd carrier, the payload after <c>/c</c>. Upstream compares
///     rawCommand against the extracted inline shell text for wrapper invocations,
///     so `cmd.exe /d /s /c echo hi` legitimately carries `rawCommand: "echo hi"`.
///     Rejecting that form would break every real carrier invocation.
/// </summary>
internal static class ExecRawCommandConsistency
{
    internal static bool IsConsistent(string? rawCommand, IReadOnlyList<string> argv)
    {
        // Absent rawCommand imposes no constraint: it is optional upstream.
        if (rawCommand is null)
            return true;

        if (argv is null || argv.Count == 0)
            return false;

        foreach (var accepted in AcceptedForms(argv))
        {
            if (string.Equals(rawCommand, accepted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> AcceptedForms(IReadOnlyList<string> argv)
    {
        var array = argv as string[] ?? [.. argv];

        yield return ShellQuoting.FormatExecCommand(array);
        yield return FormatGatewayStyle(array);

        if (TryExtractCmdInlineCommand(argv, out var payload))
            yield return payload;
    }

    /// <summary>
    /// Extracts the inline command text a cmd invocation carries, using the same rule
    /// the gateway does: find the first /c, /k, -c, or -k switch and join everything
    /// after it with single spaces, with no re-quoting. The join is deliberately not
    /// quoting-aware, because the value being reproduced is the one the gateway
    /// compares against.
    ///
    /// Recognition here is intentionally permissive about which cmd image is named.
    /// This function decides only whether a display string is consistent with an argv;
    /// it grants nothing. Durable authorization is gated separately and strictly by
    /// CanonicalCmdCarrier.TryGetTrustedCanonicalPayload.
    /// </summary>
    private static bool TryExtractCmdInlineCommand(IReadOnlyList<string> argv, out string payload)
    {
        payload = string.Empty;
        if (argv.Count < 2 || !CanonicalCmdCarrier.IsCmdExecutable(argv[0]))
            return false;

        var switchIndex = -1;
        for (var i = 1; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token.Length == 2
                && (token[0] == '/' || token[0] == '-')
                && (token[1] is 'c' or 'C' or 'k' or 'K'))
            {
                switchIndex = i;
                break;
            }
        }

        if (switchIndex < 0 || switchIndex + 1 >= argv.Count)
            return false;

        var builder = new StringBuilder();
        for (var i = switchIndex + 1; i < argv.Count; i++)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(argv[i]);
        }

        payload = builder.ToString().Trim();
        return payload.Length > 0;
    }

    /// <summary>
    /// Mirrors the gateway's formatExecCommand: quote only when the argument contains
    /// whitespace or a double quote, or is empty; escape inner double quotes with a
    /// backslash. No other character triggers quoting.
    /// </summary>
    internal static string FormatGatewayStyle(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < argv.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');
            var arg = argv[i];
            if (arg.Length == 0 || NeedsGatewayQuotes(arg))
            {
                builder.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                builder.Append(arg);
            }
        }
        return builder.ToString();
    }

    private static bool NeedsGatewayQuotes(string arg)
    {
        foreach (var ch in arg)
        {
            if (char.IsWhiteSpace(ch) || ch == '"')
                return true;
        }
        return false;
    }
}
