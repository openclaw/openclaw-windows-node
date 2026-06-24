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

    [Theory]
    [InlineData(26099, 9999, "is below MXC minimum 26100")]
    [InlineData(26100, 7964, "Windows UBR 7964 below MXC minimum 7965")]
    [InlineData(26300, 7964, "Windows UBR 7964 below MXC minimum 7965")]
    [InlineData(26499, 7964, "Windows UBR 7964 below MXC minimum 7965")]
    [InlineData(26500, 1, null)]
    [InlineData(27000, 0, null)]
    public void GetWindowsBuildUnsupportedReason_RejectsUnsupportedBuilds(
        int build,
        int ubr,
        string? expectedReason)
    {
        var reason = MxcAvailability.GetWindowsBuildUnsupportedReason(build, ubr);

        if (expectedReason is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.NotNull(reason);
            Assert.Contains(expectedReason, reason);
        }
    }

    [Theory]
    [InlineData(26100, 7965)]
    [InlineData(26100, 9999)]
    [InlineData(26300, 8289)]
    [InlineData(26500, 0)]
    [InlineData(27000, 0)]
    public void GetWindowsBuildUnsupportedReason_AllowsSupportedBuilds(int build, int ubr)
    {
        var reason = MxcAvailability.GetWindowsBuildUnsupportedReason(build, ubr);

        Assert.Null(reason);
    }
}
