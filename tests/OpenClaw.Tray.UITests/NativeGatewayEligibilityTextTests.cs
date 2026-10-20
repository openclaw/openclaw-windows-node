using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class NativeGatewayEligibilityTextTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Fact]
    public async Task Apply_UnavailablePreservesLocalizedParagraphsEmphasisAndAccessibleName()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var target = new TextBlock { Width = 560, TextWrapping = TextWrapping.Wrap };
            ui.Container.Children.Add(target);
            try
            {
                await TestSupport.WaitForRenderedConditionAsync(() => target.IsLoaded, "native guidance Loaded");
                AssertRich(target);
                var runCount = target.Inlines.Count;
                AssertRich(target);
                Assert.Equal(runCount, target.Inlines.Count);
                ui.Container.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.True(target.ActualHeight > 0);
                Assert.Equal(3, target.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split("\n\n", StringSplitOptions.None).Length);
                output.WriteLine($"Rendered guidance: {target.Text}");
                output.WriteLine($"Runs={runCount}; semibold=6; automation name matches full localized text.");
                await OnboardingArtworkRenderingTests.SaveProofAsync(target, "native-gateway-guidance", output);
            }
            finally
            {
                ui.Container.Children.Clear();
            }
        });
    }

    [Theory]
    [InlineData(NativeGatewayEligibility.Available)]
    [InlineData(NativeGatewayEligibility.UnsupportedPlatform)]
    [InlineData(NativeGatewayEligibility.CheckFailed)]
    public async Task Apply_OtherEligibilityClearsRichTextAndAutomationOverride(NativeGatewayEligibility eligibility)
    {
        await ui.RunOnUIAsync(() =>
        {
            var target = new TextBlock();
            AssertRich(target);
            NativeGatewayEligibilityTextTestAccess.Apply(target, eligibility);
            AssertPlain(target, NativeGatewayEligibilityTextTestAccess.Get(eligibility));
            AssertRich(target);
        });
    }

    [Theory]
    [InlineData("Onboarding_Native_CheckingSupport")]
    [InlineData("Onboarding_Native_UpdateLaunchFailed")]
    [InlineData("")]
    public async Task ApplyPlain_ClearsRichTextAndAutomationOverride(string resourceKey)
    {
        await ui.RunOnUIAsync(() =>
        {
            var target = new TextBlock();
            AssertRich(target);
            string text = resourceKey.Length == 0 ? "" : NativeGatewayEligibilityTextTestAccess.GetString(resourceKey);
            if (resourceKey.Length > 0)
                Assert.NotEqual(resourceKey, text);
            NativeGatewayEligibilityTextTestAccess.ApplyPlain(target, text);
            AssertPlain(target, text);
            AssertRich(target);
        });
    }

    private static void AssertRich(TextBlock target)
    {
        string text = NativeGatewayEligibilityTextTestAccess.Get(NativeGatewayEligibility.CapabilityUnavailable);
        Assert.NotEqual("Onboarding_Native_SupportUnavailable", text);
        NativeGatewayEligibilityTextTestAccess.Apply(target, NativeGatewayEligibility.CapabilityUnavailable);
        var runs = target.Inlines.Select(inline => Assert.IsType<Run>(inline)).ToArray();
        Assert.Equal(text, string.Concat(runs.Select(run => run.Text)));
        Assert.Equal(text, target.Text);
        Assert.Equal(text, AutomationProperties.GetName(target));
        Assert.Equal(text, Peer(target).GetName());
        var emphasized = runs.Where(run => run.FontWeight == FontWeights.SemiBold).ToArray();
        Assert.Equal(6, emphasized.Length);
        foreach (string suffix in new[] { "Lead", "ComingSoon", "InsiderProgram", "Channels", "WindowsVersion", "WindowsUpdate" })
        {
            string key = $"Onboarding_Native_SupportUnavailable{suffix}";
            string emphasis = NativeGatewayEligibilityTextTestAccess.GetString(key);
            Assert.NotEqual(key, emphasis);
            Assert.Contains(emphasized, run => run.Text == emphasis);
        }
    }

    private static void AssertPlain(TextBlock target, string text)
    {
        Assert.Equal(text, target.Text);
        Assert.Equal(text, string.Concat(target.Inlines.Select(inline => Assert.IsType<Run>(inline).Text)));
        Assert.Same(DependencyProperty.UnsetValue, target.ReadLocalValue(AutomationProperties.NameProperty));
        Assert.Equal(text, Peer(target).GetName());
        Assert.All(target.Inlines, inline => Assert.Equal(FontWeights.Normal, Assert.IsType<Run>(inline).FontWeight));
    }

    private static AutomationPeer Peer(TextBlock target) =>
        FrameworkElementAutomationPeer.FromElement(target)
        ?? FrameworkElementAutomationPeer.CreatePeerForElement(target);
}

// A friend-assembly grant also exposes Setup's linked FluentIconCatalog, which conflicts
// with the Tray copy in this test assembly. Bind only the production methods under test.
internal static class NativeGatewayEligibilityTextTestAccess
{
    internal static readonly Action<TextBlock, NativeGatewayEligibility> Apply =
        Bind<Action<TextBlock, NativeGatewayEligibility>>("NativeGatewayEligibilityText", "Apply");
    internal static readonly Action<TextBlock, string> ApplyPlain =
        Bind<Action<TextBlock, string>>("NativeGatewayEligibilityText", "ApplyPlain");
    internal static readonly Func<NativeGatewayEligibility, string> Get =
        Bind<Func<NativeGatewayEligibility, string>>("NativeGatewayEligibilityText", "Get");
    internal static readonly Func<string, string> GetString =
        Bind<Func<string, string>>("SetupLocalization", "GetString");

    private static T Bind<T>(string typeName, string methodName) where T : Delegate
    {
        var type = typeof(SetupWindow).Assembly.GetType($"OpenClaw.SetupEngine.UI.{typeName}", throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, methodName);
        return method.CreateDelegate<T>();
    }
}
