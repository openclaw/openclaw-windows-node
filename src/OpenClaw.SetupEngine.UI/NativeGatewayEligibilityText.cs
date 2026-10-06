using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace OpenClaw.SetupEngine.UI;

internal static class NativeGatewayEligibilityText
{
    private static readonly string[] UnavailableEmphasisKeys =
    [
        "Onboarding_Native_SupportUnavailableLead",
        "Onboarding_Native_SupportUnavailableComingSoon",
        "Onboarding_Native_SupportUnavailableInsiderProgram",
        "Onboarding_Native_SupportUnavailableChannels",
        "Onboarding_Native_SupportUnavailableWindowsVersion",
        "Onboarding_Native_SupportUnavailableWindowsUpdate",
    ];

    internal static string Get(NativeGatewayEligibility eligibility) => eligibility switch
    {
        NativeGatewayEligibility.Available => SetupLocalization.GetString("Onboarding_Native_SupportAvailable"),
        NativeGatewayEligibility.CapabilityUnavailable =>
            SetupLocalization.GetString("Onboarding_Native_SupportUnavailable"),
        NativeGatewayEligibility.UnsupportedPlatform => SetupLocalization.GetString("Onboarding_Native_UnsupportedPlatform"),
        NativeGatewayEligibility.CheckFailed => SetupLocalization.GetString("Onboarding_Native_SupportCheckFailed"),
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility)),
    };

    internal static void Apply(TextBlock target, NativeGatewayEligibility eligibility)
    {
        string text = Get(eligibility);

        if (eligibility != NativeGatewayEligibility.CapabilityUnavailable ||
            !TryGetEmphasisRanges(text, out var ranges))
        {
            ApplyPlain(target, text);
            return;
        }

        target.Inlines.Clear();
        AutomationProperties.SetName(target, text);
        int position = 0;
        foreach (var range in ranges)
        {
            if (range.Start > position)
                target.Inlines.Add(new Run { Text = text[position..range.Start] });

            target.Inlines.Add(new Run
            {
                Text = text.Substring(range.Start, range.Length),
                FontWeight = FontWeights.SemiBold,
            });
            position = range.Start + range.Length;
        }

        if (position < text.Length)
            target.Inlines.Add(new Run { Text = text[position..] });
    }

    internal static void ApplyPlain(TextBlock target, string text)
    {
        target.Inlines.Clear();
        target.ClearValue(AutomationProperties.NameProperty);
        target.Text = text;
    }

    private static bool TryGetEmphasisRanges(
        string text,
        out List<(int Start, int Length)> ranges)
    {
        return NativeGatewayEmphasisRanges.TryGet(
            text,
            UnavailableEmphasisKeys.Select(SetupLocalization.GetString),
            out ranges);
    }
}
