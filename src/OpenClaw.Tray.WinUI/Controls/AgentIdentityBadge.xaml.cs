using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

public sealed partial class AgentIdentityBadge : UserControl
{
    private readonly UIElement[] _faceEyes;
    private readonly UIElement[] _faceMouths;
    private WorkspaceAgent? _agent;
    private IChatAgentAvatarImageSource? _imageSource;
    private long _imageSourceRevision;
    private CancellationTokenSource? _loadCancellation;

    public AgentIdentityBadge()
    {
        InitializeComponent();
        _faceEyes = [FaceEye0, FaceEye1, FaceEye2, FaceEye3];
        _faceMouths = [FaceMouth0, FaceMouth1, FaceMouth2, FaceMouth3];
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WorkspaceAgent agent) Initialize(agent);
        };
        Loaded += (_, _) => _ = LoadPictureAsync();
        Unloaded += (_, _) => CancelLoad();
    }

    internal void Initialize(
        WorkspaceAgent agent,
        bool avatarOnly = false,
        double pictureSize = 32,
        IChatAgentAvatarImageSource? imageSource = null)
    {
        Picture.Width = Picture.Height = Face.Width = Face.Height = pictureSize;
        Layout.MinHeight = avatarOnly ? pictureSize : 40;
        Details.Visibility = avatarOnly ? Visibility.Collapsed : Visibility.Visible;
        Layout.ColumnSpacing = avatarOnly ? 0 : 12;
        var imageSourceRevision = imageSource?.Revision ?? 0;
        if (_agent is { } previous && previous.Id == agent.Id && previous.Name == agent.Name &&
            previous.Emoji == agent.Emoji && previous.AvatarUrl == agent.AvatarUrl &&
            ReferenceEquals(_imageSource, imageSource) && _imageSourceRevision == imageSourceRevision)
            return;
        CancelLoad();
        _agent = agent;
        _imageSource = imageSource;
        _imageSourceRevision = imageSourceRevision;
        AgentName.Text = Picture.DisplayName = agent.Name;
        AgentId.Text = agent.Id;
        Picture.Initials = agent.Emoji ?? string.Empty;
        Picture.ProfilePicture = null;
        ApplyFace(agent.Id);
        UpdateFallback();
        AutomationProperties.SetName(this, $"{agent.Name}, {agent.Id}");
        ToolTipService.SetToolTip(this, $"{agent.Name} ({agent.Id})");
        if (IsLoaded) _ = LoadPictureAsync();
    }

    private async Task LoadPictureAsync()
    {
        if (_loadCancellation is not null || Picture.ProfilePicture is not null || _agent?.AvatarUrl is not { } source)
            return;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        try
        {
            // Reuse the bounded image decoder and public-network policy. Gateway-local
            // avatar files arrive as data URLs, never as paths on this Windows machine.
            var imageSource = _imageSource;
            Microsoft.UI.Xaml.Media.ImageSource? image;
            if (imageSource is not null)
            {
                image = await imageSource.LoadImageAsync(source, cancellation.Token);
            }
            else
            {
                using var media = new MediaResolver(new AppLogger());
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    media.AllowHost(uri.Host);
                image = await media.LoadImageAsync(source, cancellation.Token);
            }
            if (!cancellation.IsCancellationRequested && IsLoaded && _agent?.AvatarUrl == source)
            {
                Picture.ProfilePicture = image;
                UpdateFallback();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The item was removed, replaced, or moved between the dropdown and selection box.
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Workspace] Agent picture failed to load: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation = null;
    }

    private void ApplyFace(string agentId)
    {
        var face = AgentAvatarPresentation.Face(agentId);
        Recolor("AgentFaceBackgroundBrush", face.Background);
        Recolor("AgentFaceInkBrush", face.Ink);
        Recolor("AgentFacePaleBrush", face.Pale);
        for (var index = 0; index < _faceEyes.Length; index++)
            _faceEyes[index].Visibility = index == face.Eye ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < _faceMouths.Length; index++)
            _faceMouths[index].Visibility = index == face.Mouth ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recolor(string key, AgentAvatarColor color) =>
        ((SolidColorBrush)Resources[key]).Color = ColorHelper.FromArgb(255, color.R, color.G, color.B);

    // Image, then text avatar, then the generated face. The face also covers a loading or failed image.
    // The hidden PersonPicture keeps its size so callers measure the same avatar either way.
    private void UpdateFallback()
    {
        var showFace = Picture.ProfilePicture is null && _agent?.Emoji is null;
        Face.Visibility = showFace ? Visibility.Visible : Visibility.Collapsed;
        Picture.Opacity = showFace ? 0 : 1;
    }
}
