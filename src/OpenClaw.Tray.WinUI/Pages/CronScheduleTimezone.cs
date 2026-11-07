namespace OpenClawTray.Pages;

/// <summary>
/// Decides which timezone a cron edit saves. An empty job zone clears the
/// combo. A zone that is not in the combo is kept until the user picks one.
/// </summary>
public static class CronScheduleTimezone
{
    public readonly record struct Edit(string? SelectedTag, string? PreservedUnlisted);

    public static Edit BeginEdit(string? jobTimezone, IEnumerable<string> listedTags)
    {
        if (string.IsNullOrEmpty(jobTimezone))
            return new Edit(null, null);

        foreach (var tag in listedTags)
        {
            if (string.Equals(tag, jobTimezone, StringComparison.Ordinal))
                return new Edit(jobTimezone, null);
        }

        return new Edit(null, jobTimezone);
    }

    public static string? ResolveSave(string? preservedUnlisted, string? selectedTag)
    {
        if (!string.IsNullOrEmpty(selectedTag))
            return selectedTag;
        return string.IsNullOrEmpty(preservedUnlisted) ? null : preservedUnlisted;
    }
}
