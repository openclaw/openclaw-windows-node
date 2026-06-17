using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcAvailabilityTests
{
    [Fact]
    public void Probe_NonWindows_ReturnsUnsupported()
    {
        // We can't easily fake the OS on Windows test runs, so just exercise
        // the public probe and assert structural invariants. The concrete
        // unsupported-platform path is exercised on Linux/macOS CI (when added).
        var availability = MxcAvailability.Probe();

        // Either fully supported, or has at least one reason explaining why not.
        if (!availability.HasAnyBackend)
        {
            Assert.NotEmpty(availability.UnsupportedReasons);
        }
    }

    [Fact]
    public void Probe_Result_IsConsistent()
    {
        var availability = MxcAvailability.Probe();

        // isolation_session implies appcontainer + wxc-exec.
        if (availability.IsIsolationSessionAvailable)
        {
            Assert.True(availability.IsAppContainerAvailable);
            Assert.True(availability.IsWxcExecResolvable);
        }

        // wxc-exec resolvable implies a path is captured.
        if (availability.IsWxcExecResolvable)
        {
            Assert.False(string.IsNullOrWhiteSpace(availability.WxcExecPath));
        }

        // HasAnyBackend requires: a backend supported AND wxc-exec resolvable.
        Assert.Equal(
            (availability.IsAppContainerAvailable || availability.IsIsolationSessionAvailable)
                && availability.IsWxcExecResolvable,
            availability.HasAnyBackend);
    }

    [Fact]
    public void Constructor_StoresAllFields()
    {
        var reasons = new List<string> { "test reason" };
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\fake\\wxc-exec.exe",
            unsupportedReasons: reasons);

        Assert.True(availability.IsAppContainerAvailable);
        Assert.False(availability.IsIsolationSessionAvailable);
        Assert.True(availability.IsWxcExecResolvable);
        Assert.Equal("C:\\fake\\wxc-exec.exe", availability.WxcExecPath);
        Assert.Single(availability.UnsupportedReasons);
        Assert.True(availability.HasAnyBackend);
    }

    [Fact]
    public void ParseProbeOutput_ValidTier_ReportsSupported()
    {
        var result = MxcAvailability.ParseProbeOutput(
            exitCode: 0,
            stdout: "{\"tier\":\"base-container\",\"needsDaclAugmentation\":false,\"warnings\":[]}",
            stderr: "");

        Assert.True(result.Supported);
        Assert.Equal("base-container", result.Tier);
        Assert.False(result.NeedsDaclAugmentation);
        Assert.Empty(result.Warnings);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ParseProbeOutput_CapturesWarningsAndDaclFlag()
    {
        var result = MxcAvailability.ParseProbeOutput(
            exitCode: 0,
            stdout: "{\"tier\":\"appcontainer-dacl\",\"needsDaclAugmentation\":true,\"warnings\":[\"fell back to dacl\"]}",
            stderr: "");

        Assert.True(result.Supported);
        Assert.Equal("appcontainer-dacl", result.Tier);
        Assert.True(result.NeedsDaclAugmentation);
        Assert.Equal(new[] { "fell back to dacl" }, result.Warnings);
    }

    [Fact]
    public void ParseProbeOutput_NonZeroExit_ReportsUnsupported()
    {
        var result = MxcAvailability.ParseProbeOutput(
            exitCode: 1,
            stdout: "",
            stderr: "unsupported os");

        Assert.False(result.Supported);
        Assert.Null(result.Tier);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("does not support", result.FailureReason!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"needsDaclAugmentation\":false}")]
    [InlineData("{\"tier\":\"\"}")]
    [InlineData("not json at all")]
    public void ParseProbeOutput_MissingOrInvalidTier_ReportsUnsupported(string stdout)
    {
        var result = MxcAvailability.ParseProbeOutput(exitCode: 0, stdout: stdout, stderr: "");

        Assert.False(result.Supported);
        Assert.Null(result.Tier);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void Probe_WhenProbeReportsTier_ReportsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(0, "{\"tier\":\"base-container\",\"warnings\":[]}", string.Empty));

            Assert.True(availability.IsAppContainerAvailable);
            Assert.True(availability.IsWxcExecResolvable);
            Assert.Equal(fakeExe, availability.WxcExecPath);
            Assert.Empty(availability.UnsupportedReasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Probe_WhenProbeReportsNoTier_ReportsUnavailableWithReason()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(1, string.Empty, "unsupported os build"));

            // wxc-exec is present, but the host probe said no → not a setup issue.
            Assert.True(availability.IsWxcExecResolvable);
            Assert.False(availability.IsAppContainerAvailable);
            Assert.False(availability.HasAnyBackend);
            Assert.NotEmpty(availability.UnsupportedReasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }
}
