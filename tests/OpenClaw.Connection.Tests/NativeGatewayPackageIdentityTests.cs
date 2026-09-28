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
}
