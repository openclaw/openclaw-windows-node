using System.Collections.Immutable;

namespace OpenClawTray.Presentation;

internal enum TrayMenuElementKind
{
    BrandHeader,
    DashboardGlance,
    ActiveSession,
    GatewayCard,
    DeviceCard,
    SessionsSummary,
    UsageSummary,
    Action,
    Flyout,
    Separator,
    Header,
    StatusCard,
    ErrorText,
    KeyValue,
    Spacer,
    SessionCard,
    Capability,
    UsageTotals,
    UsageProvider,
    UsageWindow,
    Toggle,
    Text,
}

internal enum TrayMenuIconIdentity
{
    None,
    Approvals,
    Permissions,
    Dashboard,
    Chat,
    Canvas,
    Diagnostics,
    Setup,
    Settings,
    About,
    Close,
    System,
    Terminal,
    Browser,
    Camera,
    Screen,
    Location,
    Voice,
    Speech,
    Clipboard,
    TextToSpeech,
    Device,
    App,
    Document,
}

internal enum TrayMenuAccent
{
    Neutral,
    Success,
    Caution,
    Critical,
}

internal sealed record TrayMenuElement
{
    internal required TrayMenuElementKind Kind { get; init; }
    internal string Text { get; init; } = "";
    internal string? Detail { get; init; }
    internal string? Secondary { get; init; }
    internal string? Tertiary { get; init; }
    internal string? Error { get; init; }
    internal string? Badge { get; init; }
    internal TrayMenuIconIdentity Icon { get; init; }
    internal string? ActionId { get; init; }
    internal bool IsEnabled { get; init; } = true;
    internal bool? IsChecked { get; init; }
    internal double? ProgressPercent { get; init; }
    internal string? Accelerator { get; init; }
    internal string? AutomationName { get; init; }
    internal TrayMenuAccent Accent { get; init; }
    internal ImmutableArray<TrayMenuElement> Children { get; init; } = [];
}

internal sealed record ConnectionTogglePresentation(
    bool IsOn,
    bool IsEnabled,
    string ToolTip,
    string AutomationName);

internal sealed class TrayMenuPresentation : IEquatable<TrayMenuPresentation>
{
    internal TrayMenuPresentation(
        ImmutableArray<TrayMenuElement> items,
        ConnectionTogglePresentation connectionToggle)
    {
        Items = items;
        ConnectionToggle = connectionToggle;
    }

    internal ImmutableArray<TrayMenuElement> Items { get; }
    internal ConnectionTogglePresentation ConnectionToggle { get; }

    public bool Equals(TrayMenuPresentation? other) =>
        other is not null &&
        ConnectionToggle == other.ConnectionToggle &&
        ElementsEqual(Items, other.Items);

    public override bool Equals(object? obj) => Equals(obj as TrayMenuPresentation);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ConnectionToggle);
        AddElementsHash(ref hash, Items);
        return hash.ToHashCode();
    }

    private static bool ElementsEqual(
        ImmutableArray<TrayMenuElement> left,
        ImmutableArray<TrayMenuElement> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.Kind != b.Kind ||
                a.Text != b.Text ||
                a.Detail != b.Detail ||
                a.Secondary != b.Secondary ||
                a.Tertiary != b.Tertiary ||
                a.Error != b.Error ||
                a.Badge != b.Badge ||
                a.Icon != b.Icon ||
                a.ActionId != b.ActionId ||
                a.IsEnabled != b.IsEnabled ||
                a.IsChecked != b.IsChecked ||
                a.ProgressPercent != b.ProgressPercent ||
                a.Accelerator != b.Accelerator ||
                a.AutomationName != b.AutomationName ||
                a.Accent != b.Accent ||
                !ElementsEqual(a.Children, b.Children))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddElementsHash(ref HashCode hash, ImmutableArray<TrayMenuElement> elements)
    {
        foreach (var element in elements)
        {
            hash.Add(element.Kind);
            hash.Add(element.Text);
            hash.Add(element.Detail);
            hash.Add(element.Secondary);
            hash.Add(element.Tertiary);
            hash.Add(element.Error);
            hash.Add(element.Badge);
            hash.Add(element.Icon);
            hash.Add(element.ActionId);
            hash.Add(element.IsEnabled);
            hash.Add(element.IsChecked);
            hash.Add(element.ProgressPercent);
            hash.Add(element.Accelerator);
            hash.Add(element.AutomationName);
            hash.Add(element.Accent);
            AddElementsHash(ref hash, element.Children);
        }
    }
}
