using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcAvailabilityTests
{
    internal static ProbeOutput BaseProbe(bool session = false) => new()
    {
        Tier = IsolationTier.BaseContainer,
        NeedsDaclAugmentation = false,
        Probes = new()
        {
            BaseContainerApiPresent = true, BaseContainerSupportsDenyPaths = true,
            IsolationSessionAvailable = session, UiCapabilities = new(),
        },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void UnsupportedSku_NeverLoadsNativeSdk(bool? server)
    {
        var called = false;
        var result = MxcAvailability.Probe(null, () => { called = true; return BaseProbe(); }, () => server, () => true);
        Assert.False(called);
        Assert.True(result.ProbeSuppressedBySkuGate);
        Assert.False(result.CanRunSystemRunSandbox);
        Assert.Null(result.IsolationSessionCapability);
        Assert.Equal(server == true, result.IsWindowsUnsupported);
    }

    [Fact]
    public void NonWindows_DoesNotProbeSkuOrSdk()
    {
        var result = MxcAvailability.Probe(null, () => throw new Exception(), () => throw new Exception(), () => false);
        Assert.False(result.IsSdkReady);
        Assert.False(result.IsWindowsUnsupported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionOsCapability_IsIndependentOfProcessTier(bool session)
    {
        var probe = BaseProbe(session);
        var result = MxcAvailability.Probe(null, () => probe, () => false, () => true);
        Assert.True(result.CanRunSystemRunSandbox);
        Assert.Equal(session, result.IsolationSessionCapability);
        Assert.Equal(session, result.IsIsolationSessionAvailable);
    }

    [Fact]
    public void NativeLoadFailure_LeavesSessionUnknownNotFalse()
    {
        var result = MxcAvailability.Probe(null, () => throw new DllNotFoundException(), () => false, () => true);
        Assert.True(result.ProbeErrored);
        Assert.False(result.IsSdkReady);
        Assert.Null(result.IsolationSessionCapability);
        Assert.False(result.IsWindowsUnsupported);
    }

    [Theory]
    [InlineData(IsolationTier.AppContainerBfs, false, true)]
    [InlineData(IsolationTier.AppContainerDacl, false, true)]
    [InlineData(IsolationTier.AppContainerBfs, true, false)]
    [InlineData(IsolationTier.BaseContainer, false, false)]
    [InlineData(null, false, false)]
    public void OnlySuccessfulKnownWeakerTierWithoutBaseApi_ConfirmsUnsupportedWindows(
        IsolationTier? tier, bool baseApi, bool unsupported)
    {
        var probe = new ProbeOutput
        {
            Tier = tier, NeedsDaclAugmentation = false,
            Probes = new() { BaseContainerApiPresent = baseApi, UiCapabilities = new() },
        };
        var result = MxcAvailability.Probe(null, () => probe, () => false, () => true);
        Assert.Equal(unsupported, result.IsWindowsUnsupported);
    }

    [Fact]
    public void ErrorVerdict_CannotAuthorizeUncontainedExecution()
    {
        Assert.False(new MxcAvailability(false, false, false, [], probeErrored: true,
            isWindowsUnsupported: true).IsWindowsUnsupported);
        Assert.False(MxcAvailability.Probe(null, () => throw new EntryPointNotFoundException(),
            () => false, () => true).IsWindowsUnsupported);
    }

    [Theory]
    [InlineData("base-container", false, true)]
    [InlineData("base-container", true, false)]
    [InlineData("appcontainer-bfs", false, false)]
    [InlineData("appcontainer-dacl", true, false)]
    [InlineData(null, false, false)]
    public void ProcessAdmission_RequiresUnaugmentedBaseContainer(string? tier, bool dacl, bool admitted)
    {
        var availability = new MxcAvailability(true, true, true, [], isolationTier: tier, needsDaclAugmentation: dacl,
            supportsProtectedPathDenies: true);
        Assert.Equal(admitted, availability.CanRunSystemRunSandbox);
        Assert.True(availability.IsIsolationSessionAvailable);
    }
}
