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
}
