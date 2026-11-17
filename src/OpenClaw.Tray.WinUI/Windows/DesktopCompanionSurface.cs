using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Windows;

internal sealed record DesktopCompanionInputPrompt(
    string Title, string Message, string? ActionLabel, Action<bool> Complete);

internal sealed class DesktopCompanionPresentation
{
    internal const double DefaultMascotSize = OnboardingMascot.HeroSize;
    internal const double BubbleOverlap = 6;
    public event EventHandler? Changed;
    public double MascotSize { get; private set; } = DefaultMascotSize;
    public AppNotification? Notification { get; private set; }
    public bool IsChatOpen { get; private set; }
    public long Revision { get; private set; }
    public DesktopCompanionInputPrompt? InputPrompt { get; private set; }
    private int _modalDepth;

    /// <summary>True while a prompt or chat dialog needs the whole overlay window, not just the mascot.</summary>
    public bool IsModal => InputPrompt is not null || _modalDepth > 0;

    /// <summary>Marks a dialog hosted in the overlay window; dispose to release it.</summary>
    public IDisposable BeginModal()
    {
        _modalDepth++;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
        return new ModalScope(this);
    }

    private sealed class ModalScope(DesktopCompanionPresentation owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._modalDepth--;
            owner.Revision++;
            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }

    public void SetMascotSize(double size)
    {
        if (MascotSize == size) return;
        MascotSize = size;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowInputPrompt(DesktopCompanionInputPrompt? prompt)
    {
        if (ReferenceEquals(InputPrompt, prompt)) return;
        InputPrompt = prompt;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Present(AppNotification? notification)
    {
        if (notification is not null) IsChatOpen = false;
        Notification = notification;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowChat(bool open)
    {
        IsChatOpen = open;
        if (!open) Notification = null;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record DesktopCompanionSurfaceProps(
    DesktopCompanionPresentation Presentation,
    Action<FrameworkElement> MascotMounted);

internal sealed class DesktopCompanionSurface : Component<DesktopCompanionSurfaceProps>
{
    public override Element Render()
    {
        var props = Props;
        var presentation = props.Presentation;
        var mascot = UseRef<OnboardingMascot?>(null);
        var moodRevision = UseRef(-1L);
        var (_, invalidate) = UseState((presentation.Revision, presentation.MascotSize));
        UseEffect((Func<Action>)(() =>
        {
            EventHandler changed = (_, _) => invalidate((presentation.Revision, presentation.MascotSize));
            presentation.Changed += changed;
            invalidate((presentation.Revision, presentation.MascotSize));
            return () =>
            {
                presentation.Changed -= changed;
                if (mascot.Current is { } control) control.IsAnimationEnabled = false;
                mascot.Current = null;
            };
        }), presentation);

        var notification = presentation.Notification;
        var revision = presentation.Revision;
        var prompt = presentation.InputPrompt;
        var mood = notification?.Severity switch
        {
            null => OnboardingMascotMood.Idle,
            AppNotificationSeverity.Error => OnboardingMascotMood.Sad,
            AppNotificationSeverity.Warning => OnboardingMascotMood.Attentive,
            _ => OnboardingMascotMood.Happy,
        };
        return Grid([GridSize.Star()], [GridSize.Star()],
            Button(new XamlHostElement(
                    () => mascot.Current = new OnboardingMascot(),
                    element =>
                    {
                        var control = (OnboardingMascot)element;
                        control.Width = control.Height = presentation.MascotSize;
                        if (moodRevision.Current == revision) return;
                        moodRevision.Current = revision;
                        // Only arrivals restart the entrance, not theme/layout renders.
                        control.Mood = OnboardingMascotMood.Idle;
                        control.Mood = mood;
                    }).AccessibilityView(AccessibilityView.Raw))
                .Width(presentation.MascotSize).Height(presentation.MascotSize).Padding(0).BorderThickness(0)
                .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Stretch)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .Resources(resources => resources
                    .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .AutomationId("DesktopCompanionMascot")
                .AutomationName(LocalizationHelper.GetString("DesktopCompanion_Name"))
                .OnMount(props.MascotMounted),
            ContentDialog(prompt?.Title ?? "",
                VStack(8,
                    TextBlock(prompt?.Message ?? "").TextWrapping(TextWrapping.Wrap),
                    HyperlinkButton(prompt?.ActionLabel ?? "", onClick: () => prompt?.Complete(true))
                        .IsVisible(prompt?.ActionLabel is not null)),
                LocalizationHelper.GetString("ChatVoiceDialog_Dismiss")) with
            {
                IsOpen = prompt is not null,
                OnClosed = _ => prompt?.Complete(false),
            })
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"));
    }
}
