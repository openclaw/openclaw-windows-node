using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayPackageIdentityTests
{
    [Theory]
    [InlineData("OpenClawFoundation.OpenClawGateway", "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC")]
    [InlineData("OpenClaw.Gateway", "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US")]
    public void TrustedRegistration_RequiresExactIdentityPair(string name, string publisher) =>
        Assert.True(NativeGatewayPackageIdentity.IsTrusted(name, publisher));

    [Theory]
    [InlineData(NativeGatewayPackageIdentity.StoreName, NativeGatewayPackageIdentity.DevelopmentPublisher)]
    [InlineData(NativeGatewayPackageIdentity.DevelopmentName, NativeGatewayPackageIdentity.StorePublisher)]
    [InlineData(NativeGatewayPackageIdentity.StoreName, "CN=Unrelated")]
    [InlineData("Unrelated.Gateway", NativeGatewayPackageIdentity.StorePublisher)]
    [InlineData("openclawfoundation.openclawgateway", NativeGatewayPackageIdentity.StorePublisher)]
    [InlineData(NativeGatewayPackageIdentity.StoreName, "")]
    public void OtherRegistration_IsNotTrusted(string name, string publisher) =>
        Assert.False(NativeGatewayPackageIdentity.IsTrusted(name, publisher));

    private const string Patched = "OpenClawFoundation.OpenClawGateway-source";
    private const string PatchedFamily = Patched + "_rfcbke2p71se2";
    private const string StoreFamily = NativeGatewayPackageIdentity.StoreName + "_rfcbke2p71se2";

    // Together these rows fail if the new-setup and saved-profile predicates are swapped, or if the
    // exact-family check is dropped from saved profiles or applied to new setup.
    [Theory]
    // New setup while opted in selects only the patch, never the Store package.
    [InlineData(NativeGatewayPackageIdentity.StoreName, StoreFamily, null, "source", false)]
    [InlineData(Patched, PatchedFamily, null, "source", true)]
    // New setup without an opt-in selects the Store package; no family is expected.
    [InlineData(NativeGatewayPackageIdentity.StoreName, StoreFamily, null, null, true)]
    // A saved Store profile keeps resolving while opted in.
    [InlineData(NativeGatewayPackageIdentity.StoreName, StoreFamily, StoreFamily, "source", true)]
    // A saved profile resolves only its exact family.
    [InlineData(Patched, PatchedFamily, StoreFamily, "source", false)]
    [InlineData(NativeGatewayPackageIdentity.StoreName, StoreFamily, PatchedFamily, "source", false)]
    public void IsCandidate_UsesSelectionForNewSetupAndExactResolutionForSavedProfiles(
        string name, string family, string? expectedFamily, string? devPatch, bool expected) =>
        Assert.Equal(expected, NativeGatewayPackageIdentity.IsCandidate(
            name, NativeGatewayPackageIdentity.StorePublisher, family, expectedFamily, devPatch));


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseDevPatch_UnsetOrBlank_IsNoOptIn(string? value) =>
        Assert.Null(NativeGatewayPackageIdentity.ParseDevPatch(value));

    [Fact]
    public void ParseDevPatch_NormalizesToLowercase() =>
        Assert.Equal("source", NativeGatewayPackageIdentity.ParseDevPatch(" Source "));

    [Theory]
    [InlineData("-x")]
    [InlineData("x-")]
    [InlineData("a_b")]
    [InlineData("abcdefghijklmnop")]
    public void ParseDevPatch_InvalidName_Throws(string value) =>
        Assert.Throws<InvalidOperationException>(() => NativeGatewayPackageIdentity.ParseDevPatch(value));

    [Fact]
    public void IsSelectable_WithoutOptIn_IgnoresPatchedPackages()
    {
        Assert.True(NativeGatewayPackageIdentity.IsSelectable(
            NativeGatewayPackageIdentity.StoreName, NativeGatewayPackageIdentity.StorePublisher, null));
        Assert.False(NativeGatewayPackageIdentity.IsSelectable(
            Patched, NativeGatewayPackageIdentity.StorePublisher, null));
    }

    [Fact]
    public void IsSelectable_WithOptIn_SelectsOnlyThatPatchUnderStorePublisher()
    {
        Assert.True(NativeGatewayPackageIdentity.IsSelectable(Patched, NativeGatewayPackageIdentity.StorePublisher, "source"));
        Assert.False(NativeGatewayPackageIdentity.IsSelectable(
            NativeGatewayPackageIdentity.StoreName, NativeGatewayPackageIdentity.StorePublisher, "source"));
        Assert.False(NativeGatewayPackageIdentity.IsSelectable(Patched, "CN=Unrelated", "source"));
        Assert.False(NativeGatewayPackageIdentity.IsSelectable(
            "OpenClawFoundation.OpenClawGateway-other", NativeGatewayPackageIdentity.StorePublisher, "source"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("source")]
    [InlineData("other")]
    public void IsResolvable_TrustedIdentitiesAlways_PatchOnlyWhenOptedIn(string? devPatch)
    {
        Assert.True(NativeGatewayPackageIdentity.IsResolvable(
            NativeGatewayPackageIdentity.StoreName, NativeGatewayPackageIdentity.StorePublisher, devPatch));
        Assert.True(NativeGatewayPackageIdentity.IsResolvable(
            NativeGatewayPackageIdentity.DevelopmentName, NativeGatewayPackageIdentity.DevelopmentPublisher, devPatch));
        Assert.Equal(devPatch == "source",
            NativeGatewayPackageIdentity.IsResolvable(Patched, NativeGatewayPackageIdentity.StorePublisher, devPatch));
    }

    [Theory]
    [InlineData(NativeGatewayPackageIdentity.StoreName, "clawctl", "clawctl.exe")]
    [InlineData(NativeGatewayPackageIdentity.DevelopmentName, "openclaw", "openclaw.exe")]
    [InlineData(Patched, "openclaw", "openclaw-source.exe")]
    public void GetAliasFileName_SuffixesOnlyPatchedPackages(string packageName, string command, string expected) =>
        Assert.Equal(expected, NativeGatewayPackageIdentity.GetAliasFileName(packageName, command));

    [Fact]
    public void GetAliasFileName_UnknownCommand_Throws() =>
        Assert.Throws<ArgumentException>(() => NativeGatewayPackageIdentity.GetAliasFileName(Patched, "node"));

    [Fact]
    public void ValidatePackage_PatchedFamily_RequiresPatchedAliases()
    {
        NativeGatewayPaths.ValidatePackage(
            new NativeGatewayPackage(PatchedFamily, "1.0.0.0", Alias("openclaw-source.exe"), Alias("clawctl-source.exe")),
            PatchedFamily);

        Assert.Throws<InvalidOperationException>(() => NativeGatewayPaths.ValidatePackage(
            new NativeGatewayPackage(PatchedFamily, "1.0.0.0", Alias("openclaw.exe"), Alias("clawctl.exe")),
            PatchedFamily));
    }

    [Theory]
    [InlineData("OpenClawFoundation.OpenClawGateway-Source_rfcbke2p71se2")]
    [InlineData("OpenClawFoundation.OpenClawGateway-abcdefghijklmnop_rfcbke2p71se2")]
    [InlineData("OpenClawFoundation.OpenClawGateway-_rfcbke2p71se2")]
    public void ValidateFamilyName_RejectsInvalidPatch(string family) =>
        Assert.Throws<ArgumentException>(() => NativeGatewayPaths.ValidateFamilyName(family));

    private static string Alias(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", PatchedFamily, fileName);
}
