namespace OpenClawTray.Chat;

internal sealed class SessionPickerSnapshot
{
    private readonly ChannelGroup[] _groups;

    private SessionPickerSnapshot(ChannelGroup[] groups)
    {
        _groups = groups;
    }

    public static SessionPickerSnapshot Capture(ChannelGroup[] groups) =>
        new(groups.Select(group =>
            new ChannelGroup(group.AgentLabel, group.Sessions.ToArray())).ToArray());

    public bool Matches(ChannelGroup[] groups)
    {
        if (_groups.Length != groups.Length)
            return false;

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var previous = _groups[groupIndex];
            var current = groups[groupIndex];
            if (previous.AgentLabel != current.AgentLabel || previous.Sessions.Length != current.Sessions.Length)
                return false;

            for (var sessionIndex = 0; sessionIndex < current.Sessions.Length; sessionIndex++)
            {
                if (previous.Sessions[sessionIndex] != current.Sessions[sessionIndex])
                    return false;
            }
        }

        return true;
    }
}
