using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public class OnboardingFlowPolicyTests
{
    [Fact]
    public void ExistingGateway_ReviewsAccessThenAiWithoutWslInstallation()
    {
        Assert.Equal(
            [OnboardingStage.Welcome, OnboardingStage.Gateway, OnboardingStage.Capabilities, OnboardingStage.AiSetup, OnboardingStage.Ready],
            OnboardingFlowPolicy.GetStages(false, new SetupConfig()));
    }

    [Theory]
    [InlineData(SetupGatewayRoute.ManagedWsl, "chat")]
    [InlineData(SetupGatewayRoute.Existing, "chat")]
    [InlineData(SetupGatewayRoute.Remote, "chat")]
    [InlineData(SetupGatewayRoute.McpOnly, "settings")]
    [InlineData(SetupGatewayRoute.Deferred, "connection")]
    public void CompletionLaunchTarget_UsesTheSelectedSetupRoute(SetupGatewayRoute route, string target)
    {
        Assert.Equal(target, OnboardingFlowPolicy.GetCompletionLaunchTarget(route));
    }

    [Theory]
    [InlineData(SetupGatewayRoute.ManagedWsl, OnboardingAccessDestination.GatewayReview, true)]
    [InlineData(SetupGatewayRoute.Existing, OnboardingAccessDestination.AiSetup, false)]
    [InlineData(SetupGatewayRoute.Remote, OnboardingAccessDestination.AiSetup, false)]
    [InlineData(SetupGatewayRoute.McpOnly, OnboardingAccessDestination.CompleteWithoutGateway, false)]
    [InlineData(SetupGatewayRoute.Deferred, OnboardingAccessDestination.CompleteWithoutGateway, false)]
    public void BranchDestination_SeparatesNativeAiFromWslAndGatewayFreeCompletion(
        SetupGatewayRoute route, OnboardingAccessDestination destination, bool usesWsl)
    {
        Assert.Equal(destination, OnboardingFlowPolicy.GetAccessDestination(route));
        Assert.Equal(usesWsl, OnboardingFlowPolicy.UsesWslWorkspaceFinalization(route));
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Existing)]
    [InlineData(SetupGatewayRoute.Remote)]
    public void NativeGatewayRoutes_AlwaysReachFocusedAiDespiteManagedWizardSkip(SetupGatewayRoute route)
    {
        var config = new SetupConfig { SkipWizard = true };
        Assert.True(OnboardingFlowPolicy.RequiresAiSetup(route, config));
        var stages = OnboardingFlowPolicy.GetStages(route, config);
        Assert.Equal(OnboardingStage.Ready, stages[^1]);
        Assert.Contains(OnboardingStage.Capabilities, stages);
        Assert.DoesNotContain(OnboardingStage.GatewayReview, stages);
        Assert.DoesNotContain(OnboardingStage.Install, stages);
    }

    [Theory]
    [InlineData(SetupGatewayRoute.McpOnly)]
    [InlineData(SetupGatewayRoute.Deferred)]
    public void GatewayFreeRoutes_SkipAiEvenWhenManagedLocalAiSelectionIsRetained(SetupGatewayRoute route)
    {
        var config = new SetupConfig();
        config.LocalAi.Enabled = true;
        Assert.False(OnboardingFlowPolicy.RequiresAiSetup(route, config));
        var stages = OnboardingFlowPolicy.GetStages(route, config);
        Assert.Equal(OnboardingStage.Capabilities, stages[^1]);
        Assert.DoesNotContain(OnboardingStage.AiSetup, stages);
        Assert.DoesNotContain(OnboardingStage.Install, stages);
        Assert.True(config.LocalAi.Enabled);
    }

    [Fact]
    public void FreshSetup_HasCombinedAccessAndAnExplicitVerifiedDestinationStage()
    {
        Assert.Equal(
            [OnboardingStage.Welcome, OnboardingStage.Gateway, OnboardingStage.Capabilities, OnboardingStage.GatewayReview,
                OnboardingStage.Install, OnboardingStage.AiSetup, OnboardingStage.Ready],
            OnboardingFlowPolicy.GetStages(true, new SetupConfig()));
    }

    [Theory]
    [InlineData(SetupGatewayRoute.ManagedWsl, 7)]
    [InlineData(SetupGatewayRoute.Existing, 5)]
    [InlineData(SetupGatewayRoute.Remote, 5)]
    [InlineData(SetupGatewayRoute.McpOnly, 3)]
    [InlineData(SetupGatewayRoute.Deferred, 3)]
    public void CombinedAccess_IsOneStageRegardlessOfHeadlessSkipPermissions(SetupGatewayRoute route, int count)
    {
        foreach (var skip in new[] { false, true })
        {
            var config = new SetupConfig { SkipPermissions = skip };
            var stages = OnboardingFlowPolicy.GetStages(route, config);
            Assert.Equal(count, stages.Count);
            Assert.Single(stages, stage => stage == OnboardingStage.Capabilities);
            Assert.Equal(skip, config.SkipPermissions);
        }
    }

    [Fact]
    public void ExplicitWizardSkip_OmitsAiConfiguration()
    {
        var config = new SetupConfig { SkipWizard = true };
        Assert.False(OnboardingFlowPolicy.RequiresAiSetup(config));
        Assert.DoesNotContain(OnboardingStage.AiSetup, OnboardingFlowPolicy.GetStages(true, config));
    }

    [Fact]
    public void LocalAiStillRequiresVerification_WhenClassicWizardIsSkipped()
    {
        var config = new SetupConfig { SkipWizard = true };
        config.LocalAi.Enabled = true;
        Assert.True(OnboardingFlowPolicy.RequiresAiSetup(config));
        Assert.Contains(OnboardingStage.AiSetup, OnboardingFlowPolicy.GetStages(true, config));
    }

    [Fact]
    public void LocalAiRecovery_DoesNotReplayFirstRunIntroduction()
    {
        var config = new SetupConfig { SkipWizard = true };
        config.LocalAi.Enabled = true;
        Assert.Equal(
            [OnboardingStage.GatewayReview, OnboardingStage.Install, OnboardingStage.AiSetup, OnboardingStage.Ready],
            OnboardingFlowPolicy.GetStages(true, config, localAiRecovery: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractiveInstallation_DefersWizardAndWorkspaceFinalization(bool recovery)
    {
        var steps = OnboardingFlowPolicy.BuildInstallationSteps(recovery);
        Assert.NotEmpty(steps);
        Assert.DoesNotContain(steps, step => step is RunGatewayWizardStep or WindowsNodeBootstrapContextStep);
        if (recovery)
            Assert.DoesNotContain(steps, step => step is CreateWslInstanceStep or CleanupStaleDistroStep);
        else
        {
            Assert.Contains(steps, step => step is PairOperatorStep);
            Assert.Contains(steps, step => step is PairNodeStep);
            Assert.Contains(steps, step => step is VerifyEndToEndStep);
        }
    }

    [Fact]
    public void ClassicCompatibilityWizard_DoesNotPromiseTheNativeVerifiedChooser()
    {
        Assert.DoesNotContain(OnboardingStage.Ready, OnboardingFlowPolicy.GetStages(
            SetupGatewayRoute.ManagedWsl, new SetupConfig(), includeReadyChoice: false));
    }

    [Fact]
    public void HeadlessPipeline_KeepsItsWizardAndWorkspaceFinalization()
    {
        var steps = SetupStepFactory.BuildDefaultSteps();
        Assert.Contains(steps, step => step is RunGatewayWizardStep);
        Assert.Contains(steps, step => step is WindowsNodeBootstrapContextStep);
    }
}
