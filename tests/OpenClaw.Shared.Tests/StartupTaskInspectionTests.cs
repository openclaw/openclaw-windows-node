namespace OpenClaw.Shared.Tests;

public sealed class StartupTaskInspectionTests
{
    [Theory]
    [InlineData("expected", true)]
    [InlineData("disabled", false)]
    [InlineData("path", false)]
    [InlineData("executable", false)]
    [InlineData("arguments", false)]
    [InlineData("working-directory", false)]
    [InlineData("principal", false)]
    [InlineData("trigger", false)]
    public void OnlyExactEnabledStartupTaskCountsAsCompletedRegistration(string changed, bool matches)
    {
        var executable = Path.Combine(Path.GetTempPath(), "expected.exe");
        var description = new StartupTaskDescription(
            changed != "disabled",
            changed == "path" ? "\\other" : "\\OpenClaw",
            changed == "executable" ? Path.Combine(Path.GetTempPath(), "old.exe") : executable,
            changed == "arguments" ? "--other-profile" : "",
            changed == "working-directory" ? Path.GetTempPath() : "",
            changed == "principal" ? "S-1-5-18" : "S-1-5-21-123",
            changed != "trigger");
        Assert.Equal(matches, WindowsStartupTaskRegistration.MatchesExpectedTask(
            description, "OpenClaw", executable, "S-1-5-21-123"));
    }
}
