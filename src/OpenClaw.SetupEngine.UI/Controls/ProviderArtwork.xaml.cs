using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed partial class ProviderArtwork : UserControl
{
    public static readonly DependencyProperty DescriptorProperty = DependencyProperty.Register(
        nameof(Descriptor), typeof(ProviderArtworkDescriptor), typeof(ProviderArtwork), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(ProviderArtworkSession), typeof(ProviderArtwork), new PropertyMetadata(null, Changed));
    private readonly ProviderArtworkGeneration _generation = new();
    private ProviderArtworkSession? _observedSession;

    public ProviderArtworkDescriptor? Descriptor
    {
        get => (ProviderArtworkDescriptor?)GetValue(DescriptorProperty);
        set => SetValue(DescriptorProperty, value);
    }
    public ProviderArtworkSession? Session
    {
        get => (ProviderArtworkSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public ProviderArtwork()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ProviderArtwork)sender).Refresh();
    private void OnLoaded(object sender, RoutedEventArgs args) => Refresh();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ObserveSession(null);
        _generation.Stop();
        ClearImage();
    }

    private void ObserveSession(ProviderArtworkSession? session)
    {
        if (ReferenceEquals(session, _observedSession))
            return;
        if (_observedSession is not null)
            _observedSession.Closed -= OnSessionClosed;
        _observedSession = session;
        if (session is not null)
            session.Closed += OnSessionClosed;
    }

    private void OnSessionClosed()
    {
        _generation.Stop();
        ClearImage();
        ObserveSession(null);
    }

    private void ClearImage()
    {
        ArtworkImage.Source = null;
        ArtworkImage.Visibility = Visibility.Collapsed;
        FallbackIcon.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(this, null);
    }

    private void Refresh()
    {
        _generation.Stop();
        if (!IsLoaded)
            return;
        ObserveSession(Session);
        ClearImage();
        var descriptor = Descriptor;
        FallbackIcon.Glyph = descriptor?.Fallback switch
        {
            ProviderArtworkFallback.Pair => FluentIconCatalog.Devices,
            ProviderArtworkFallback.Install => FluentIconCatalog.Setup,
            ProviderArtworkFallback.Configure => FluentIconCatalog.Settings,
            ProviderArtworkFallback.Verified => FluentIconCatalog.StatusOk,
            ProviderArtworkFallback.Code => FluentIconCatalog.Develop,
            ProviderArtworkFallback.Account => FluentIconCatalog.Operator,
            _ => FluentIconCatalog.Lock
        };
        if (descriptor is null || Session is not { } session || session.Token.IsCancellationRequested)
            return;
        var request = _generation.Begin(session.Token);
        AsyncEventHandlerGuard.Run(
            () => LoadAsync(descriptor, session, request.Generation, request.Token),
            onError: _ => ShowFailure(ProviderArtworkStatus.InvalidImage, request.Generation));
    }

    private async Task LoadAsync(ProviderArtworkDescriptor descriptor, ProviderArtworkSession session,
        int generation, CancellationToken ct)
    {
        try
        {
            DecodedProviderArtwork decoded;
            if (descriptor.BundledFileName is { } file)
            {
                if (!file.StartsWith("ProviderIcon-", StringComparison.Ordinal) ||
                    !file.EndsWith(".svg", StringComparison.Ordinal) ||
                    GatewayAiSetupPresentation.GetBundledProviderIconFileName(file[13..^4]) != file)
                {
                    ShowFailure(ProviderArtworkStatus.Blocked, generation);
                    return;
                }
                // WinUI resolves library resources in unpackaged installs too; Windows Storage
                // ms-appx stream resolution requires package identity.
                var uri = new Uri("ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/ProviderIcons/" + file);
                decoded = await LoadBundledAsync(uri, generation, ct);
            }
            else if (descriptor.RemoteUri is { } remote)
            {
                var loaded = await session.Loader.LoadAsync(remote, ct);
                decoded = loaded.Data is { } data ? await ProviderArtworkDecoder.DecodeAsync(data, ct) :
                    new(loaded.Status);
            }
            else
            {
                if (descriptor.RemoteRejected)
                    ShowFailure(ProviderArtworkStatus.Blocked, generation);
                return;
            }
            if (!_generation.IsCurrent(generation) || !IsLoaded)
                return;
            if (decoded.Source is null)
            {
                ShowFailure(decoded.Status, generation);
                return;
            }
            ArtworkImage.Source = decoded.Source;
            ArtworkImage.Visibility = Visibility.Visible;
            FallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected on rebind, unload and page closure. A current control's
            // deadline still leaves a coarse, local explanation rather than a broken-image box.
            if (IsLoaded && !session.Token.IsCancellationRequested)
                ShowFailure(ProviderArtworkStatus.TimedOut, generation, allowCancelled: true);
        }
        catch (COMException) { ShowFailure(ProviderArtworkStatus.InvalidImage, generation); }
        catch (IOException) { ShowFailure(ProviderArtworkStatus.InvalidImage, generation); }
        catch (ArgumentException) { ShowFailure(ProviderArtworkStatus.InvalidImage, generation); }
    }

    private async Task<DecodedProviderArtwork> LoadBundledAsync(Uri uri, int generation, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<SvgImageSourceLoadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SvgImageSource { RasterizePixelWidth = 24, RasterizePixelHeight = 24 };
        void Opened(SvgImageSource sender, SvgImageSourceOpenedEventArgs args) =>
            completion.TrySetResult(SvgImageSourceLoadStatus.Success);
        void Failed(SvgImageSource sender, SvgImageSourceFailedEventArgs args) => completion.TrySetResult(args.Status);
        source.Opened += Opened;
        source.OpenFailed += Failed;
        try
        {
            ct.ThrowIfCancellationRequested();
            // URI-backed SVG decoding starts when a visible Image consumes the source.
            // Keep the fallback visible until Opened; awaiting an unattached source deadlocks.
            ArtworkImage.Source = source;
            ArtworkImage.Visibility = Visibility.Visible;
            source.UriSource = uri;
            var status = await completion.Task.WaitAsync(ct);
            return status == SvgImageSourceLoadStatus.Success
                ? new(ProviderArtworkStatus.Loaded, source) : new(ProviderArtworkStatus.InvalidImage);
        }
        finally
        {
            source.Opened -= Opened;
            source.OpenFailed -= Failed;
            if (ct.IsCancellationRequested)
            {
                source.UriSource = null;
                if (_generation.Matches(generation) && ReferenceEquals(ArtworkImage.Source, source))
                {
                    ArtworkImage.Source = null;
                    ArtworkImage.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void ShowFailure(ProviderArtworkStatus status, int generation, bool allowCancelled = false)
    {
        if (!IsLoaded || !(allowCancelled ? _generation.Matches(generation) : _generation.IsCurrent(generation)))
            return;
        ClearImage();
        ToolTipService.SetToolTip(this,
            SetupLocalization.Format("Onboarding_AiSetup_ArtworkUnavailable", status.ToString()));
    }
}
