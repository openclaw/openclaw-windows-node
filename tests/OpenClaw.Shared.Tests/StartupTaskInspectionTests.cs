namespace OpenClaw.Shared.Tests;

public sealed class StartupTaskInspectionTests
{
    [Theory]
    [InlineData(true, 0, StartupTaskRegistrationOutcome.Registered)]
    [InlineData(true, unchecked((int)0x80070005), StartupTaskRegistrationOutcome.Rejected)]
    [InlineData(true, 1, StartupTaskRegistrationOutcome.Unknown)]
    [InlineData(true, unchecked((int)0x80004005), StartupTaskRegistrationOutcome.Unknown)]
    [InlineData(false, 0, StartupTaskRegistrationOutcome.Unknown)]
    [InlineData(false, unchecked((int)0x80070005), StartupTaskRegistrationOutcome.Unknown)]
    [InlineData(true, null, StartupTaskRegistrationOutcome.Unknown)]
    public void SetupClassificationUsesCompletedHresultNotLocalizedOutput(
        bool completed, int? hresult, StartupTaskRegistrationOutcome expected) =>
        Assert.Equal(expected, WindowsStartupTaskRegistration.ClassifySetupResult(completed, hresult));

    [Fact]
    public void SetupRegistrationRequestsHresultWithoutChangingLegacyRegistration()
    {
        var executable = Path.Combine(Path.GetTempPath(), "OpenClaw.exe");
        var setup = WindowsStartupTaskRegistration.CreateSetupRegisterProcessStartInfo(executable);
        var legacy = WindowsStartupTaskRegistration.CreateRegisterProcessStartInfo(executable);
        Assert.Equal(WindowsStartupTaskRegistration.ResolveSchtasksPath(), setup.FileName);
        Assert.False(setup.UseShellExecute);
        Assert.Contains("/HRESULT", setup.ArgumentList);
        Assert.DoesNotContain("/HRESULT", legacy.ArgumentList);
        Assert.Equal(legacy.ArgumentList, setup.ArgumentList.Where(argument => argument != "/HRESULT"));
    }

    [Fact]
    public void MissingTaskIsAbsentWithClrProjectedNotFoundError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var taskName = "OpenClaw-absent-test-" + Guid.NewGuid().ToString("N");
        Assert.Equal(StartupTaskInspection.Absent,
            WindowsStartupTaskRegistration.InspectStrict(taskName, null));
    }

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
