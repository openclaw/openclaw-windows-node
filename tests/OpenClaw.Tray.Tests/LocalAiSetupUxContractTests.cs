using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUxContractTests
{
    [Fact]
    public void WelcomePage_ShowsLocalAiAsIconBadgeBesideRecommended()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml.cs"));
        string badge = ExtractElement(xaml, "LocalAiAvailabilityBadge", "</Border>");

        Assert.Contains("WelcomeLocalAiAvailable", badge);
        Assert.Contains("Glyph=\"&#xE950;\"", badge);
        Assert.Contains("Local AI available", badge);
        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", badge);
        AssertInOrder(xaml, "Text=\"Recommended\"", "x:Name=\"LocalAiAvailabilityBadge\"");
        Assert.DoesNotContain("LocalAiAvailabilityPanel", xaml);
        Assert.DoesNotContain("LocalAiAvailabilityText", xaml);
        Assert.DoesNotContain("detected. Install a local gateway", source);
        Assert.Contains("AutomationProperties.SetName(", source);
    }

    /// <summary>
    /// The Welcome-page badge text and the accessible name it appends when Local AI is
    /// available must route through SetupLocalization (not hardcoded English), sharing one
    /// resw entry between the x:Uid-bound visual text and the code-behind accessible name.
    /// </summary>
    [Fact]
    public void WelcomePage_LocalAiAvailableBadgeAndAccessibleName_AreLocalizedInEverySupportedLocale()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        string badge = ExtractElement(xaml, "LocalAiAvailabilityBadge", "</Border>");

        Assert.Contains("x:Uid=\"Onboarding_Welcome_LocalAiAvailableBadge\"", badge);
        Assert.Contains(
            "SetupLocalization.GetString(\"Onboarding_Welcome_LocalAiAvailableBadge.Text\")", source);
        Assert.Contains("AutomationProperties.GetName(InstallChoice)", source);

        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            Assert.Contains("\"Onboarding_Welcome_LocalAiAvailableBadge.Text\"", resources);
        }
    }

    [Fact]
    public void CapabilitiesReview_SeparatesReasonActionFromDisabledOptions()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        string infoBar = ExtractElement(xaml, "LocalAiUnavailablePanel", "</InfoBar>");

        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("Severity=\"Informational\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("Message=\"This PC does not meet one or more Local AI requirements.\"", infoBar);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.DoesNotContain("<StackPanel", infoBar);
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiAvailabilityRecoveryPanel\"", xaml);
        Assert.Contains("x:Name=\"LocalAiAvailabilityProgressRing\"", xaml);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        AssertInOrder(
            xaml,
            "x:Name=\"LocalAiUnavailablePanel\"",
            "x:Name=\"LocalAiUnavailableDetailsButton\"",
            "x:Name=\"LocalAiAvailabilityRecoveryPanel\"",
            "x:Name=\"LocalAiRecheckAvailabilityButton\"",
            "x:Name=\"LocalAiInstallReviewCard\"",
            "x:Name=\"LocalAiOptionContent\"");
        Assert.Contains("LocalAiSetupAvailabilityCoordinator", source);
        Assert.Contains("TryApplyProbeFailure", source);
        Assert.Contains("ShowLocalAiProbeUnknown", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
        Assert.Contains("GetLocalAiHardwareAsync(forceRefresh: refreshHardwareProbe)", source);
        Assert.Contains("CanApplyLocalAiAvailability(checking.Generation, setupWindow)", source);
        AssertInOrder(
            source,
            "HostHardwareInfo hardware = await hardwareTask;",
            "if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))",
            "_localAiHardware = hardware;");
        AssertInOrder(
            source,
            "WslGlobalConfigStatus networkingStatus = forceNetworkingConsent",
            "if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))",
            "_localAiNetworkingStatus = networkingStatus;");
        AssertInOrder(
            source,
            "eligibility = LocalInferenceEligibility.Evaluate(",
            "if (eligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)",
            "TryApplyProbeFailure(",
            "ShowLocalAiProbeUnknown(incompleteSnapshot);");
        Assert.Contains("LocalAiOptionContent.IsHitTestVisible = isAvailable", source);
        Assert.Contains("LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55", source);
        Assert.Contains("LocalAiToggle.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiModelSelector.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiNetworkingConsentCheckBox.IsEnabled = isAvailable", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: false)", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: true)", source);

        string checkingMethod = ExtractMethod(source, "private void ShowLocalAiAvailabilityChecking");
        Assert.Contains("LocalAiToggle.IsOn = _config!.LocalAi.Enabled;", checkingMethod);
        Assert.DoesNotContain("_config!.LocalAi.Enabled = false;", checkingMethod);
        Assert.DoesNotContain("_config.SkipWizard = _skipWizardWithoutLocalAi;", checkingMethod);

        // A probe failure is retryable, not definitive: a successful recheck must restore the
        // user's prior Local AI selection instead of a transient failure having cleared it.
        string probeUnknownMethod = ExtractMethod(source, "private void ShowLocalAiProbeUnknown");
        Assert.Contains("LocalAiToggle.IsOn = _config!.LocalAi.Enabled;", probeUnknownMethod);
        Assert.DoesNotContain("_config!.LocalAi.Enabled = false;", probeUnknownMethod);
        Assert.DoesNotContain("_config.SkipWizard = _skipWizardWithoutLocalAi;", probeUnknownMethod);
    }

    [Fact]
    public void CapabilitiesReview_RecheckAffordance_HasLocalizedResourceKeys()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));

        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);

        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Strings",
                locale,
                "Resources.resw"));
            Assert.Contains("Onboarding_LocalAi_RecheckAvailabilityButton.Content", resources);
        }
    }

    /// <summary>
    /// The setup page's unavailable/checking/probe-error InfoBar title, message, and action, its
    /// accessibility help text, its probe-failure reason, and its "why unavailable" dialog must
    /// route through SetupLocalization (not hardcoded English) and have matching resw keys in
    /// every supported locale.
    /// </summary>
    [Fact]
    public void CapabilitiesReview_UnavailableAndProbeErrorCopy_IsLocalizedInEverySupportedLocale()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));

        Assert.Contains("x:Uid=\"Onboarding_LocalAi_UnavailableDetailsButton\"", xaml);

        string[] setupResourceCalls =
        [
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeFailureReason\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableDetailsDialogTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableDetailsDialogClose\")",
        ];
        foreach (string call in setupResourceCalls)
            Assert.Contains(call, source);

        // The setup page never hardcodes the English copy it used to.
        Assert.DoesNotContain("\"OpenClaw is checking Local AI requirements.\"", source);
        Assert.DoesNotContain(
            "\"OpenClaw could not verify Local AI requirements. Recheck availability to try again.\"", source);
        Assert.DoesNotContain(
            "\"Unavailable because this PC does not meet the Local AI requirements.\"", source);
        Assert.DoesNotContain("\"Why Local AI is unavailable\"", source);

        string[] resourceKeys =
        [
            "Onboarding_LocalAi_UnavailableDetailsButton.Content",
            "Onboarding_LocalAi_CheckingTitle",
            "Onboarding_LocalAi_CheckingMessage",
            "Onboarding_LocalAi_CheckingHelpText",
            "Onboarding_LocalAi_ProbeUnknownTitle",
            "Onboarding_LocalAi_ProbeUnknownMessage",
            "Onboarding_LocalAi_ProbeUnknownHelpText",
            "Onboarding_LocalAi_UnavailableTitle",
            "Onboarding_LocalAi_UnavailableMessage",
            "Onboarding_LocalAi_UnavailableHelpText",
            "Onboarding_LocalAi_ProbeFailureReason",
            "Onboarding_LocalAi_UnavailableDetailsDialogTitle",
            "Onboarding_LocalAi_UnavailableDetailsDialogClose",
        ];
        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            foreach (string key in resourceKeys)
                Assert.Contains($"\"{key}\"", resources);
        }
    }

    /// <summary>
    /// The detailed diagnostic reason (why hardware is unavailable — insufficient GPU memory,
    /// old driver, missing GPU, incomplete facts, etc.) is shared fact-only from
    /// LocalInferenceEligibilityDiagnostics; both the setup page and the Hub page localize it
    /// through their own resource-string helpers, with matching keys in every supported locale.
    /// </summary>
    [Fact]
    public void LocalAiUnavailableReason_IsLocaleNeutralInSharedAndLocalizedInBothUiOwners()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string diagnostics = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Shared", "Inference", "Catalog", "LocalInferenceEligibilityDiagnostics.cs"));
        string setupSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Presentation", "LocalAiPageViewModel.cs"));
        string hubPageSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "LocalAiPage.xaml.cs"));

        // Shared stays locale-neutral: facts and a kind enum, no English sentences.
        Assert.Contains("LocalInferenceUnavailableReasonKind", diagnostics);
        Assert.Contains("GetUnavailableReason", diagnostics);
        Assert.DoesNotContain("model weights, KV cache, and runtime workspace", diagnostics);
        Assert.DoesNotContain("No NVIDIA GPU was reported", diagnostics);

        // The Hub ViewModel is source-linked into this test project without a WinUI resource
        // host and must stay free of LocalizationHelper/resource-key literals; it only exposes
        // the locale-neutral reason for the View to format.
        Assert.DoesNotContain("LocalizationHelper", viewModelSource);
        Assert.Contains("LocalInferenceUnavailableReason? LocalAiUnavailableReason", viewModelSource);

        // The View (LocalAiPage.xaml.cs) and the setup page each format the reason locally,
        // through the shared LocalAi_Reason_* keys.
        string[] reasonKeys =
        [
            "LocalAi_Reason_RuntimeUnavailable",
            "LocalAi_Reason_NoNvidiaGpu",
            "LocalAi_Reason_UnknownModel",
            "LocalAi_Reason_HardwareFactsIncomplete",
            "LocalAi_Reason_InsufficientGpuMemory",
            "LocalAi_Reason_DriverTooOld",
            "LocalAi_Reason_CudaCapabilityTooLow",
            "LocalAi_Reason_Generic",
            "LocalAi_Reason_UnknownModelName",
            "LocalAi_Reason_UnknownDriverVersion",
            "LocalAi_Reason_UnknownMemoryAmount",
            "LocalAi_Reason_GigabytesFormat",
        ];
        foreach (string key in reasonKeys)
        {
            Assert.Contains($"\"{key}\"", hubPageSource);
            Assert.Contains($"\"{key}\"", setupSource);
        }

        // A thrown probe failure and a successful-but-incomplete read both resolve to
        // HardwareFactsIncomplete: one shared message, no separate "probe failure" key.
        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            foreach (string key in reasonKeys)
                Assert.Contains($"\"{key}\"", resources);
        }
    }

    [Fact]
    public void SetupWindow_LocalAiHardwareProbeCache_CanRefreshAfterFault()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));
        string method = ExtractMethod(source, "GetLocalAiHardwareAsync");

        Assert.Contains("bool forceRefresh = false", method);
        Assert.Contains("forceRefresh ||", method);
        Assert.Contains("_localAiHardwareProbeTask.IsFaulted", method);
        Assert.Contains("_localAiHardwareProbeTask.IsCanceled", method);
        Assert.Contains("_localAiHardwareProbeTask = Task.Run", method);
    }

    [Fact]
    public void LocalAiPage_InfoBarPrecedesAndDoesNotDisableReasonAction()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml.cs"));
        string infoBar = ExtractElement(xaml, "LocalAiUnavailableInfoBar", "</InfoBar>");

        AssertInOrder(
            xaml,
            "<ScrollViewer VerticalScrollBarVisibility=\"Auto\">",
            "<Grid HorizontalAlignment=\"Stretch\">",
            "<StackPanel Padding=\"24\" Spacing=\"12\" HorizontalAlignment=\"Stretch\" MaxWidth=\"900\">",
            "x:Uid=\"LocalAiPage_Intro\"",
            "x:Name=\"LocalAiUnavailableInfoBar\"",
            "x:Name=\"LocalAiEngineOption\"");
        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsTip\"", xaml);
        Assert.Contains("Target=\"{x:Bind LocalAiUnavailableDetailsButton}\"", xaml);
        Assert.Contains("x:Name=\"LocalAiEngineOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiModelOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiGatewayOption\"", xaml);
        Assert.DoesNotContain("SetOptionAvailability(", source);
        Assert.DoesNotContain("option.IsEnabled = isAvailable", source);
        Assert.Contains("ShowAvailabilityInfoBar", source);
        Assert.Contains("CanRecheckAvailability", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
        Assert.Contains("LocalAiUnavailableDetailsTip.IsOpen = !LocalAiUnavailableDetailsTip.IsOpen", source);
    }

    private static string ExtractElement(string source, string elementName, string closingTag)
    {
        int start = source.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {elementName}.");
        int end = source.IndexOf(closingTag, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the end of {elementName}.");
        return source[start..(end + closingTag.Length)];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        int previous = -1;
        foreach (string value in values)
        {
            int current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after the previous value.");
            previous = current;
        }
    }

    private static string ExtractMethod(string source, string methodName)
    {
        int nameStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(nameStart >= 0, $"Could not find method {methodName}.");
        int brace = source.IndexOf('{', nameStart);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");
        int depth = 0;
        for (int index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[nameStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find end of method {methodName}.");
    }
}
