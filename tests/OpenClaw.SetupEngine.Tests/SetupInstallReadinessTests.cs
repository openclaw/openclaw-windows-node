namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupInstallReadinessTests
{
    [Fact]
    public void Admission_MatchesOriginalConditionsAcrossRoutesInspectionConsentAndOptionalFeatures()
    {
        foreach (var route in Enum.GetValues<SetupGatewayRoute>())
        for (var inspection = 0; inspection < 4; inspection++)
        for (var flags = 0; flags < 2048; flags++)
        {
            bool Flag(int bit) => (flags & (1 << bit)) != 0;
            var recovery = Flag(0);
            var draft = new SetupAccessDraft(new SetupConfig());
            draft.SelectRoute(route);
            if (inspection != 0)
            {
                draft.RecordWslInspection(new(false, null, null, inspection == 2, inspection == 2,
                    false, draft.Config.DistroName, false, 0, []));
                if (Flag(9)) draft.ConfirmReplacement(true);
                if (Flag(10)) draft.Config.ConfirmedDestructiveDistroName = "DifferentGateway";
                if (inspection == 3) draft.Config.DistroName = "DifferentGateway";
            }
            draft.SetLocalAiEnabled(Flag(1));
            draft.LocalAiReady = Flag(2);
            draft.LocalAiNetworkingConsentRequired = Flag(3);
            draft.Config.LocalAi.WslMirroredNetworkingConsent = Flag(4);
            draft.Config.Tailscale.Enabled = Flag(5);
            draft.TailscaleReady = Flag(6);
            draft.Config.Tailscale.AuthMode = Flag(7) ? TailscaleAuthMode.AuthKey : TailscaleAuthMode.Browser;
            draft.Config.Tailscale.AuthKey = Flag(8) ? "test-only" : " ";

            var expected = route == SetupGatewayRoute.ManagedWsl &&
                (recovery || (inspection is 1 or 2 &&
                    (inspection != 2 || Flag(9) && !Flag(10)))) &&
                (!recovery || Flag(1)) &&
                (!Flag(1) || Flag(2) && (!Flag(3) || Flag(4))) &&
                (!Flag(5) || Flag(6) && (!Flag(7) || Flag(8)));
            Assert.True(expected == draft.CanInstall(recovery),
                $"Admission mismatch: route={route}, inspection={inspection}, flags={flags}");
        }
    }

    [Fact]
    public void Requirements_IdentifyEveryBlockerWithoutChangingTheDraft()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        Assert.Equal([SetupInstallRequirement.WslInspection], draft.GetInstallRequirements());
        draft.RecordWslInspection(new(false, null, null, true, true, false,
            draft.Config.DistroName, false, 0, []));
        draft.SetLocalAiEnabled(true);
        draft.LocalAiNetworkingConsentRequired = true;
        draft.Config.Tailscale.Enabled = true;
        var before = System.Text.Json.JsonSerializer.Serialize(draft.Config);
        Assert.Equal(
            [SetupInstallRequirement.Replacement, SetupInstallRequirement.LocalAi,
                SetupInstallRequirement.NetworkingConsent, SetupInstallRequirement.Tailscale],
            draft.GetInstallRequirements());
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(draft.Config));
        Assert.False(draft.ReplacementConfirmed);

        draft.ConfirmReplacement(true);
        Assert.Equal(SetupInstallRequirement.LocalAi, draft.GetInstallRequirements()[0]);
        draft.LocalAiReady = true;
        Assert.Equal(SetupInstallRequirement.NetworkingConsent, draft.GetInstallRequirements()[0]);
        draft.Config.LocalAi.WslMirroredNetworkingConsent = true;
        Assert.Equal([SetupInstallRequirement.Tailscale], draft.GetInstallRequirements());
        draft.TailscaleReady = true;
        draft.Config.Tailscale.AuthMode = TailscaleAuthMode.AuthKey;
        Assert.Equal([SetupInstallRequirement.Tailscale], draft.GetInstallRequirements());
        draft.Config.Tailscale.AuthKey = "test-only";
        Assert.Empty(draft.GetInstallRequirements());
        Assert.True(draft.CanInstall());
        draft.ConfirmReplacement(false);
        Assert.Equal([SetupInstallRequirement.Replacement], draft.GetInstallRequirements());
        Assert.False(draft.CanInstall());
    }

    [Fact]
    public void ReplacementConsent_CannotSubstituteForAnExactTargetInspection()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.ConfirmReplacement(true);
        Assert.Equal([SetupInstallRequirement.WslInspection], draft.GetInstallRequirements());
        draft.RecordWslInspection(new(false, null, null, true, true, false,
            draft.Config.DistroName, false, 0, []));
        draft.ConfirmReplacement(true);
        draft.Config.ConfirmedDestructiveDistroName = "DifferentGateway";
        Assert.Equal([SetupInstallRequirement.Replacement], draft.GetInstallRequirements());
        draft.Config.DistroName += "-different";
        Assert.Equal([SetupInstallRequirement.WslInspection], draft.GetInstallRequirements());
        draft.ConfirmReplacement(true);
        Assert.Equal([SetupInstallRequirement.WslInspection], draft.GetInstallRequirements());
    }

    [Fact]
    public void OptionalOff_IsNotABlockerAndRecoveryRetainsPinnedRequirements()
    {
        var draft = new SetupAccessDraft(new SetupConfig());
        draft.Config.LocalAi.SelectedModelId = "pinned-model";
        draft.LocalAiNetworkingConsentRequired = true;
        Assert.Equal([SetupInstallRequirement.LocalAi], draft.GetInstallRequirements(localAiRecovery: true));
        draft.SetLocalAiEnabled(true);
        Assert.Equal([SetupInstallRequirement.LocalAi, SetupInstallRequirement.NetworkingConsent],
            draft.GetInstallRequirements(localAiRecovery: true));
        draft.LocalAiReady = true;
        draft.Config.LocalAi.WslMirroredNetworkingConsent = true;
        Assert.Empty(draft.GetInstallRequirements(localAiRecovery: true));
        Assert.Equal("pinned-model", draft.Config.LocalAi.SelectedModelId);
        Assert.False(draft.WslInspectionComplete);
        Assert.Equal([SetupInstallRequirement.WslInspection], draft.GetInstallRequirements());
        draft.SetLocalAiEnabled(false);
        draft.SelectRoute(SetupGatewayRoute.Existing);
        Assert.Equal(SetupInstallRequirement.ManagedWslRoute, draft.GetInstallRequirements()[0]);
    }
}
