namespace OpenClaw.SetupEngine;

internal static class SetupArgumentParser
{
    internal static bool TryParse(
        string[] args,
        IReadOnlyCollection<string> valueOptionNames,
        IReadOnlyCollection<string> flagOptionNames,
        out ParsedArguments parsedArguments,
        out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valueOptions = new HashSet<string>(valueOptionNames, StringComparer.OrdinalIgnoreCase);
        var flagOptions = new HashSet<string>(flagOptionNames, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                parsedArguments = new(values, flags);
                error = $"Unexpected argument '{token}'.";
                return false;
            }

            var equalsIndex = token.IndexOf('=');
            var name = equalsIndex >= 0 ? token[..equalsIndex] : token;
            var hasInlineValue = equalsIndex >= 0;

            if (valueOptions.Contains(name))
            {
                if (values.ContainsKey(name))
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} may only be specified once.";
                    return false;
                }

                var value = hasInlineValue
                    ? token[(equalsIndex + 1)..]
                    : i < args.Length - 1 ? args[i + 1] : null;
                if (string.IsNullOrWhiteSpace(value) ||
                    (!hasInlineValue && value.StartsWith("--", StringComparison.Ordinal)))
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} requires a value.";
                    return false;
                }

                values[name] = value;
                if (!hasInlineValue)
                    i++;
                continue;
            }

            if (flagOptions.Contains(name))
            {
                if (hasInlineValue)
                {
                    parsedArguments = new(values, flags);
                    error = $"{name} does not accept a value.";
                    return false;
                }

                flags.Add(name);
                continue;
            }

            parsedArguments = new(values, flags);
            error = $"Unknown option '{token}'.";
            return false;
        }

        parsedArguments = new(values, flags);
        error = null;
        return true;
    }

    internal sealed class ParsedArguments(
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string> flags)
    {
        internal string? GetValue(string name)
            => values.TryGetValue(name, out var value) ? value : null;

        internal bool HasFlag(string name)
            => flags.Contains(name);
    }
}

internal static class SetupWindowCommandLine
{
    internal const string ConfigOption = "--config";
    internal const string NoRollbackFlag = "--no-rollback-on-failure";

    private static readonly string[] s_valueOptionNames = [ConfigOption];
    private static readonly string[] s_flagOptionNames = [NoRollbackFlag];

    internal static bool TryParse(
        string[] args,
        out SetupWindowCommandLineArguments parsedArguments,
        out string? error)
    {
        if (!SetupArgumentParser.TryParse(
                args,
                s_valueOptionNames,
                s_flagOptionNames,
                out var parsed,
                out error))
        {
            parsedArguments = new(null, RollbackOnFailure: true);
            return false;
        }

        parsedArguments = new(
            parsed.GetValue(ConfigOption),
            RollbackOnFailure: !parsed.HasFlag(NoRollbackFlag));
        return true;
    }
}

internal sealed record SetupWindowCommandLineArguments(
    string? ConfigPath,
    bool RollbackOnFailure);
