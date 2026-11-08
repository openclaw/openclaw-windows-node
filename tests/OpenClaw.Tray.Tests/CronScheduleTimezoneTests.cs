using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public class CronScheduleTimezoneTests
{
    private static readonly string[] Listed = ["UTC", "America/Los_Angeles", "Europe/London"];

    [Fact]
    public void EmptyZone_ClearsTheComboAndSavesNoZone()
    {
        var edit = CronScheduleTimezone.BeginEdit(null, Listed);

        Assert.Null(edit.SelectedTag);
        Assert.Null(edit.PreservedUnlisted);
        Assert.Null(CronScheduleTimezone.ResolveSave(edit.PreservedUnlisted, null));
    }

    [Fact]
    public void ListedZone_IsSelected()
    {
        var edit = CronScheduleTimezone.BeginEdit("Europe/London", Listed);

        Assert.Equal("Europe/London", edit.SelectedTag);
        Assert.Null(edit.PreservedUnlisted);
        Assert.Equal("Europe/London", CronScheduleTimezone.ResolveSave(edit.PreservedUnlisted, edit.SelectedTag));
    }

    [Fact]
    public void UnlistedZone_IsKeptWhenTheComboStaysEmpty()
    {
        var edit = CronScheduleTimezone.BeginEdit("Asia/Kathmandu", Listed);

        Assert.Null(edit.SelectedTag);
        Assert.Equal("Asia/Kathmandu", edit.PreservedUnlisted);
        Assert.Equal("Asia/Kathmandu", CronScheduleTimezone.ResolveSave(edit.PreservedUnlisted, null));
    }

    [Fact]
    public void CronPage_UsesTheTimezoneEditForSave()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs"));

        Assert.Contains("CronScheduleTimezone.BeginEdit", source, StringComparison.Ordinal);
        Assert.Contains("FormTimezone.SelectedIndex = -1", source, StringComparison.Ordinal);
        Assert.Contains("CronScheduleTimezone.ResolveSave", source, StringComparison.Ordinal);
        Assert.Contains("if (kind != \"cron\")", source, StringComparison.Ordinal);
        var editMethod = ExtractMethod(source, "OnEditJobClick");
        Assert.Contains("FormTimezone.SelectedIndex = -1", editMethod, StringComparison.Ordinal);
        Assert.Contains("if (kind == \"cron\")", editMethod, StringComparison.Ordinal);
        Assert.True(
            editMethod.IndexOf("FormTimezone.SelectedIndex = -1", StringComparison.Ordinal)
                < editMethod.IndexOf("if (kind == \"cron\")", StringComparison.Ordinal),
            "Editing any job must clear the previous timezone selection before cron-specific initialization.");
        Assert.Contains("_preservedScheduleTz = null;", ExtractMethod(source, "OnScheduleKindChanged"), StringComparison.Ordinal);
        Assert.Contains("_preservedScheduleTz = null;", ExtractMethod(source, "OnFormCancelClick"), StringComparison.Ordinal);
        Assert.Contains("_preservedScheduleTz = null;", ExtractMethod(source, "ResetForm"), StringComparison.Ordinal);
        Assert.Contains("_preservedScheduleTz = null;", ExtractMethod(source, "SaveFormAsync"), StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    [Fact]
    public void UnlistedZone_YieldsWhenTheUserPicksAListedZone()
    {
        var edit = CronScheduleTimezone.BeginEdit("Asia/Kathmandu", Listed);

        Assert.Equal("UTC", CronScheduleTimezone.ResolveSave(edit.PreservedUnlisted, "UTC"));
    }

    [Fact]
    public void StartingAnotherEdit_ReplacesPreviouslyPreservedState()
    {
        var first = CronScheduleTimezone.BeginEdit("Asia/Kathmandu", Listed);
        var second = CronScheduleTimezone.BeginEdit(null, Listed);

        Assert.Equal("Asia/Kathmandu", first.PreservedUnlisted);
        Assert.Null(second.PreservedUnlisted);
        Assert.Null(CronScheduleTimezone.ResolveSave(second.PreservedUnlisted, second.SelectedTag));
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var markers = new[]
        {
            $"private void {methodName}",
            $"private async Task {methodName}"
        };
        var marker = markers.FirstOrDefault(candidate => source.Contains(candidate, StringComparison.Ordinal));
        Assert.NotNull(marker);

        var start = source.IndexOf(marker!, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {methodName}.");

        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Could not find {methodName} body.");

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        Assert.Fail($"Could not parse {methodName} body.");
        return string.Empty;
    }
}
