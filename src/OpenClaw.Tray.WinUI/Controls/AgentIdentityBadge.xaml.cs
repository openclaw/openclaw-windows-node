using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

public sealed partial class AgentIdentityBadge : UserControl
{
    private WorkspaceAgent? _agent;
    private CancellationTokenSource? _loadCancellation;

    public AgentIdentityBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = LoadPictureAsync();
        Unloaded += (_, _) => CancelLoad();
    }

    internal void Initialize(WorkspaceAgent agent)
    {
        if (_agent is { } previous && previous.Id == agent.Id && previous.Name == agent.Name &&
            previous.Emoji == agent.Emoji && previous.AvatarUrl == agent.AvatarUrl)
            return;
        CancelLoad();
        _agent = agent;
        AgentName.Text = Picture.DisplayName = agent.Name;
        AgentId.Text = agent.Id;
        Picture.Initials = agent.Emoji ?? string.Empty;
        Picture.ProfilePicture = null;
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
            using var media = new MediaResolver(new AppLogger());
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                media.AllowHost(uri.Host);
            var image = await media.LoadImageAsync(source, cancellation.Token);
            if (!cancellation.IsCancellationRequested && IsLoaded && _agent?.AvatarUrl == source)
                Picture.ProfilePicture = image;
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
}
