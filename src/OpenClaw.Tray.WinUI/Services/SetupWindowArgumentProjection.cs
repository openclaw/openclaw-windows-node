namespace OpenClawTray.Services;

internal static class SetupWindowArgumentProjection
{
    private static readonly HashSet<string> s_hostFlags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "--post-setup-restart",
        };

    private static readonly HashSet<string> s_hostValueOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "--wait-for-pid",
            "--post-setup-launch",
        };

    internal static string[] Project(
        string[]? processArguments,
        Func<string, bool> isDeepLinkArgument,
        int currentProcessId)
    {
        if (processArguments is not { Length: > 1 })
            return [];

        var setupArguments = new List<string>();
        for (var i = 1; i < processArguments.Length; i++)
        {
            var token = processArguments[i];
            if (i == 1 && isDeepLinkArgument(token))
                continue;

            if (s_hostFlags.Contains(token))
                continue;

            if (s_hostValueOptions.Contains(token))
            {
                if (i < processArguments.Length - 1 &&
                    IsValidHostValue(token, processArguments[i + 1], currentProcessId))
                {
                    i++;
                    continue;
                }

                setupArguments.Add(token);
                continue;
            }

            setupArguments.Add(token);
        }

        return [.. setupArguments];
    }

    private static bool IsValidHostValue(
        string option,
        string value,
        int currentProcessId)
    {
        if (string.Equals(option, "--wait-for-pid", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                       value,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var pid) &&
                   pid > 0 &&
                   pid != currentProcessId;
        }

        return string.Equals(
            value,
            "chat",
            StringComparison.OrdinalIgnoreCase);
    }
}
