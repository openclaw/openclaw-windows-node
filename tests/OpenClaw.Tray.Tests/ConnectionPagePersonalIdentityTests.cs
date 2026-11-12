using System.Xml.Linq;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPagePersonalIdentityTests
{
    [Theory]
    [InlineData(GatewayErrorKind.PersonalIdentityUnverified, "ConnectionPage_PersonalIdentityUnverified",
        "Votre identité Tailscale n’a pas pu être vérifiée. Reconnectez cette passerelle. Les identifiants partagés ne seront pas utilisés.")]
    [InlineData(GatewayErrorKind.PersonalIdentityReverificationRequired, "ConnectionPage_PersonalIdentityReverificationRequired",
        "Votre identité Tailscale doit être vérifiée à nouveau. Reconnectez cette passerelle. Les identifiants partagés ne seront pas utilisés.")]
    public void PersonalFailureUsesLocalizedGuidanceInsteadOfRawDiagnostics(GatewayErrorKind kind, string detailKey, string french)
    {
        const string raw = "untrusted-remote-detail-314159";
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorErrorKind = kind,
            OperatorError = raw,
        };
        var plan = ConnectionPagePlan.Build(snapshot, new GatewayRecord
        {
            Id = "personal", Url = "wss://personal.example.ts.net",
            OperatorAuthentication = OperatorAuthenticationMode.PersonalTailscale,
        }, self: null, settings: null, savedGatewayCount: 1);

        Assert.Equal(ConnectionPageMode.Recovery, plan.Mode);
        Assert.Equal("ConnectionPage_PersonalIdentityHeader", plan.StripHeadlineResourceKey);
        Assert.Equal(detailKey, plan.StripSubResourceKey);
        Assert.Equal(detailKey, Assert.Single(plan.RecoveryBulletResourceKeys));
        Assert.DoesNotContain(raw, plan.StripSub);
        Assert.Null(plan.RecoveryDetail);
        Assert.DoesNotContain(plan.Recovery, new[] { RecoveryCategory.Auth, RecoveryCategory.TokenDrift, RecoveryCategory.Scope });
        var document = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "fr-fr", "Resources.resw"));
        Assert.Equal(french, document.Root!.Elements("data")
            .Single(element => (string?)element.Attribute("name") == detailKey).Element("value")!.Value);
    }
}
