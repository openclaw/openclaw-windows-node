using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenClaw.Shared;

internal sealed class ExecShellEvaluationTarget
{
    public string Command { get; init; } = "";
    public string? Shell { get; init; }
}

internal sealed class ExecShellParseResult
{
    public List<ExecShellEvaluationTarget> Targets { get; } = new();
    public string? Error { get; init; }
}

internal static class ExecShellWrapperParser
{
    private const int MaxDepth = 4;

    internal static ExecShellParseResult Expand(string command, string? shell = null)
    {
        var result = new ExecShellParseResult();

        if (string.IsNullOrWhiteSpace(command))
            return result;

        var pending = new Queue<(string Command, string? Shell, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((command, NormalizeShell(shell), 0));

        while (pending.Count > 0)
        {
            var (current, currentShell, depth) = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(current) || depth > MaxDepth)
                continue;

            var segments = SplitTopLevelCommands(current);
            var hasMultipleSegments = segments.Count > 1;

            foreach (var rawSegment in segments)
            {
                var segment = TrimMatchingQuotes(rawSegment.Trim());
                if (string.IsNullOrWhiteSpace(segment))
                    continue;

                if ((depth > 0 || hasMultipleSegments) && seen.Add($"{currentShell}|{segment}"))
                {
                    result.Targets.Add(new ExecShellEvaluationTarget
                    {
                        Command = segment,
                        Shell = currentShell
                    });
                }

                var wrapped = TryExtractWrappedPayload(segment);
                if (wrapped.Error != null)
                {
                    return new ExecShellParseResult { Error = wrapped.Error };
                }

                if (!string.IsNullOrWhiteSpace(wrapped.Payload))
                {
                    pending.Enqueue((wrapped.Payload!, wrapped.Shell ?? currentShell, depth + 1));
                }
            }
        }

        return result;
    }

    private static (string? Payload, string? Shell, string? Error) TryExtractWrappedPayload(string command)
    {
        var tokens = Tokenize(command);
        if (tokens.Length < 2)
            return default;

        var executable = Path.GetFileName(tokens[0]);
        if (string.IsNullOrWhiteSpace(executable))
            return default;

        if (executable.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("/c", StringComparison.OrdinalIgnoreCase) ||
                    tokens[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                    return string.IsNullOrWhiteSpace(payload)
                        ? ("", "cmd", "Shell wrapper payload was empty")
                        : (payload, "cmd", null);
                }
            }
        }

        if (executable.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePowerShellPayload(tokens, "powershell");
        }

        if (executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePowerShellPayload(tokens, "pwsh");
        }

        if (executable.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("bash.exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("sh.exe", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("-c", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                    return string.IsNullOrWhiteSpace(payload)
                        ? ("", "sh", "Shell wrapper payload was empty")
                        : (payload, "sh", null);
                }
            }
        }

        return default;
    }

    private static (string? Payload, string? Shell, string? Error) ParsePowerShellPayload(string[] tokens, string shell)
    {
        for (var i = 1; i < tokens.Length; i++)
        {
            var option = tokens[i];
            if (option.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-c", StringComparison.OrdinalIgnoreCase))
            {
                var payload = string.Join(" ", tokens, i + 1, tokens.Length - i - 1).Trim();
                return string.IsNullOrWhiteSpace(payload)
                    ? ("", shell, "Shell wrapper payload was empty")
                    : (payload, shell, null);
            }

            if (option.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-enc", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("-ec", StringComparison.OrdinalIgnoreCase))
            {
                var encoded = i + 1 < tokens.Length ? tokens[i + 1] : null;
                if (string.IsNullOrWhiteSpace(encoded))
                    return ("", shell, "Shell wrapper payload was empty");

                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    var payload = Encoding.Unicode.GetString(bytes).Trim();
                    return string.IsNullOrWhiteSpace(payload)
                        ? ("", shell, "EncodedCommand decoded to an empty payload")
                        : (payload, shell, null);
                }
                catch (FormatException)
                {
                    return ("", shell, "EncodedCommand could not be decoded");
                }
            }
        }

        return default;
    }

    private static List<string> SplitTopLevelCommands(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                current.Append(c);
                continue;
            }

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                current.Append(c);
                continue;
            }

            if (!inSingleQuotes && !inDoubleQuotes)
            {
                if (c == ';' || c == '&')
                {
                    FlushCurrent(parts, current);
                    if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
                        i++;
                    continue;
                }

                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                {
                    FlushCurrent(parts, current);
                    i++;
                    continue;
                }
            }

            current.Append(c);
        }

        FlushCurrent(parts, current);
        return parts;
    }

    private static string[] Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var escapeNext = false;

        foreach (var c in command)
        {
            if (escapeNext)
            {
                current.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\' && inDoubleQuotes)
            {
                escapeNext = true;
                continue;
            }

            if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (!inSingleQuotes && !inDoubleQuotes && char.IsWhiteSpace(c))
            {
                FlushCurrent(tokens, current);
                continue;
            }

            current.Append(c);
        }

        FlushCurrent(tokens, current);
        return tokens.ToArray();
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? NormalizeShell(string? shell) =>
        string.IsNullOrWhiteSpace(shell) ? "powershell" : shell.ToLowerInvariant();

    private static void FlushCurrent(List<string> parts, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        parts.Add(current.ToString());
        current.Clear();
    }
}
