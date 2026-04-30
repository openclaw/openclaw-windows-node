using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

/// <summary>
/// Tiny IDisposable shim so renderers can stash arbitrary cleanup actions in
/// the surface's subscription dictionary. The host disposes registered
/// subscriptions on rebuild, which is exactly when we want to cancel async
/// work the renderer kicked off (e.g. timer-bound CancellationTokenSources).
/// </summary>
internal sealed class RendererCleanup : IDisposable
{
    private Action? _onDispose;
    public RendererCleanup(Action onDispose) => _onDispose = onDispose;
    public void Dispose()
    {
        var action = System.Threading.Interlocked.Exchange(ref _onDispose, null);
        try { action?.Invoke(); } catch { /* cleanup must never throw */ }
    }
}

public sealed class TextRenderer : IComponentRenderer
{
    public string ComponentName => "Text";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        ApplyUsageHint(tb, c.Properties["usageHint"]?.GetValue<string>());

        var textVal = ctx.GetValue(c, "text");
        void Update() => tb.Text = ctx.ResolveString(textVal) ?? string.Empty;
        Update();
        ctx.WatchValue(c.Id, "text", textVal, Update);
        return tb;
    }

    private static void ApplyUsageHint(TextBlock tb, string? hint)
    {
        var resourceKey = hint switch
        {
            "h1" => "TitleLargeTextBlockStyle",
            "h2" => "TitleTextBlockStyle",
            "h3" => "SubtitleTextBlockStyle",
            "h4" => "BodyStrongTextBlockStyle",
            "h5" => "BodyStrongTextBlockStyle",
            "caption" => "CaptionTextBlockStyle",
            "body" => "BodyTextBlockStyle",
            null => "BodyTextBlockStyle",
            _ => "BodyTextBlockStyle",
        };
        if (Application.Current.Resources.TryGetValue(resourceKey, out var styleObj) && styleObj is Style s)
            tb.Style = s;
        // Some style keys don't exist on every WinUI version; fall back gracefully.
        if (tb.Style == null && hint != null
            && Application.Current.Resources.TryGetValue("BodyTextBlockStyle", out var fallback) && fallback is Style fb)
        {
            tb.Style = fb;
        }
    }
}

public sealed class ImageRenderer : IComponentRenderer
{
    private readonly MediaResolver _media;
    public ImageRenderer(MediaResolver media) { _media = media; }
    public string ComponentName => "Image";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var image = new Image();
        var usageHint = c.Properties["usageHint"]?.GetValue<string>();
        ApplyFit(image, c.Properties["fit"]?.GetValue<string>());
        ApplyUsageHint(image, usageHint);

        // Single counter per image; each load takes a snapshot and only assigns
        // the bitmap if no later load has started. Stops a slow first response
        // from clobbering a faster second one when the URL flips quickly.
        var generation = new int[] { 0 };
        System.Threading.CancellationTokenSource? loadCts = null;
        var urlVal = ctx.GetValue(c, "url");
        void Update()
        {
            var url = ctx.ResolveString(urlVal);
            if (string.IsNullOrEmpty(url))
            {
                System.Threading.Interlocked.Increment(ref generation[0]);
                var prev = loadCts;
                loadCts = null;
                if (prev != null)
                {
                    try { prev.Cancel(); } catch { }
                    prev.Dispose();
                }
                image.Source = null;
                return;
            }
            if (ctx.MediaBudget?.TryReserveImageLoad() == false)
            {
                ctx.Logger?.Warn("[A2UI] Image load rejected: surface media budget exhausted");
                return;
            }
            int token = System.Threading.Interlocked.Increment(ref generation[0]);
            // Cancel + dispose the previous timer-bound CTS. CancellationTokenSource
            // wraps a kernel timer when constructed with a TimeSpan, so leaking
            // these on a chatty url path holds handles until GC.
            var prevCts = loadCts;
            loadCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (prevCts != null)
            {
                try { prevCts.Cancel(); } catch { }
                prevCts.Dispose();
            }
            _ = LoadAsync(image, url, generation, token, loadCts.Token, ctx.Logger);
        }
        Update();
        ctx.WatchValue(c.Id, "url", urlVal, Update);
        // Cancel + dispose the live CTS on surface rebuild so an in-flight load
        // doesn't continue running after the visual tree has moved on.
        ctx.RegisterSubscription(c.Id, "imageLoadCts", new RendererCleanup(() =>
        {
            var cts = loadCts;
            loadCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }));
        // A2UI carries alt text in `description` (preferred) or `label` for images.
        AutomationHelpers.Apply(image, c, ctx);

        // Avatar usageHint: clip to a circle. Wrapping in a Border with rounded
        // CornerRadius is the simplest WinUI3-friendly approach (PersonPicture
        // gives us full Fluent parity but doesn't accept arbitrary BitmapImage
        // sources without templating).
        if (string.Equals(usageHint, "avatar", StringComparison.OrdinalIgnoreCase))
        {
            var diameter = image.Width > 0 ? image.Width : 40;
            return new Border
            {
                Width = diameter,
                Height = diameter,
                CornerRadius = new CornerRadius(diameter / 2),
                Child = image,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        return image;
    }

    private async Task LoadAsync(
        Image target,
        string url,
        int[] generation,
        int token,
        System.Threading.CancellationToken cancellationToken,
        OpenClaw.Shared.IOpenClawLogger? logger)
    {
        try
        {
            // ImageSource is the common base of BitmapImage (raster) and SvgImageSource — Image.Source accepts either.
            var src = await _media.LoadImageAsync(url, cancellationToken).ConfigureAwait(true);
            // Only commit if our token is still current. Volatile read is sufficient
            // — UI thread is the only writer and we're awaited back onto it.
            if (src != null && System.Threading.Volatile.Read(ref generation[0]) == token)
                target.Source = src;
        }
        catch (OperationCanceledException)
        {
            logger?.Debug("[A2UI] Image load cancelled");
        }
    }

    private static void ApplyFit(Image image, string? fit)
    {
        image.Stretch = fit switch
        {
            "contain" => Stretch.Uniform,
            "cover" => Stretch.UniformToFill,
            "fill" => Stretch.Fill,
            "none" => Stretch.None,
            "scale-down" => Stretch.Uniform,
            _ => Stretch.Uniform,
        };
    }

    private static void ApplyUsageHint(Image image, string? hint)
    {
        switch (hint)
        {
            case "icon":
                image.Width = image.Height = 24;
                break;
            case "avatar":
                image.Width = image.Height = 40;
                // Approximate circle with corner radius via clipping is non-trivial here;
                // leave as a square avatar for v1.
                break;
            case "smallFeature":
                image.Height = 80;
                break;
            case "mediumFeature":
                image.Height = 160;
                break;
            case "largeFeature":
                image.Height = 240;
                break;
            case "header":
                image.Stretch = Stretch.UniformToFill;
                image.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
        }
    }
}

public sealed class IconRenderer : IComponentRenderer
{
    public string ComponentName => "Icon";
    // Emit at most one info log per name-with-known-fidelity-issue, per process.
    // Cheap signal that lets us judge whether to invest in a better glyph.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_loggedFidelity = new();

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var fontIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
        };
        var nameVal = ctx.GetValue(c, "name");
        void Update()
        {
            var name = ctx.ResolveString(nameVal);
            fontIcon.Glyph = MapName(name);
            // moreHoriz currently reuses moreVert's glyph (no canonical horizontal
            // ellipsis in MDL2). Log once so we can tell whether real surfaces
            // ask for it before swapping in a custom font.
            if (name == "moreHoriz" && s_loggedFidelity.TryAdd("moreHoriz", 1))
                ctx.Logger?.Info("[A2UI] icon 'moreHoriz' is rendered with the moreVert glyph (no canonical horizontal in Segoe Fluent Icons).");
        }
        Update();
        ctx.WatchValue(c.Id, "name", nameVal, Update);
        // Prefer the A2UI label/description fields (they are human-readable
        // and localized by the agent). Fall back to NOT announcing the icon
        // at all — the previous behavior would speak raw enum names like
        // "moreHoriz" or "accountCircle" to Narrator users, which is worse
        // than the icon being skipped. Decorative icons inside Buttons / Cards
        // already have parent labels for assistive tech.
        AutomationHelpers.Apply(fontIcon, c, ctx);
        if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fontIcon)))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                fontIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
        return fontIcon;
    }

    /// <summary>
    /// Map A2UI v0.8 icon-name enum to Segoe Fluent Icons glyph codepoints.
    /// The icon-name enum is the v0.8 Material-derived set; each name maps to
    /// the closest-meaning Segoe MDL2 / Fluent glyph. Unknown names fall back
    /// to the Help glyph rather than rendering an empty box.
    /// </summary>
    private static string MapName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ""; // Help (?)
        return name switch
        {
            "accountCircle"    => "", // Contact
            "add"              => "", // Add
            "arrowBack"        => "", // Back
            "arrowForward"     => "", // Forward
            "attachFile"       => "", // Attach
            "calendarToday"    => "", // CalendarDay
            "call"             => "", // Phone
            "camera"           => "", // Camera
            "check"            => "", // CheckMark
            "close"            => "", // Cancel
            "delete"           => "", // Delete
            "download"         => "", // Download
            "edit"             => "", // Edit
            "event"            => "", // Calendar
            "error"            => "", // Error
            "favorite"         => "", // HeartFill
            "favoriteOff"      => "", // Heart (outline)
            "folder"           => "", // Folder
            "help"             => "", // Help / Unknown
            "home"             => "", // Home
            "info"             => "", // Info
            "locationOn"       => "", // MapPin
            "lock"             => "", // Lock
            "lockOpen"         => "", // Unlock
            "mail"             => "", // Mail
            "menu"             => "", // GlobalNavButton (hamburger)
            "moreVert"         => "", // More (vertical dots)
            "moreHoriz"        => "", // More — no canonical horizontal in MDL2; reuse vertical
            "notificationsOff" => "", // RingerOff
            "notifications"    => "", // Ringer
            "payment"          => "", // Payment
            "person"           => "", // Contact
            "phone"            => "", // Phone
            "photo"            => "", // Picture
            "print"            => "", // Print
            "refresh"          => "", // Refresh
            "search"           => "", // Search
            "send"             => "", // Send
            "settings"         => "", // Settings (gear)
            "share"            => "", // Share
            "shoppingCart"     => "", // ShoppingCart
            "star"             => "", // FavoriteStarFill
            "starHalf"         => "", // HalfStarLeft
            "starOff"          => "", // FavoriteStar (outline)
            "upload"           => "", // Upload
            "visibility"       => "", // RedEye / View
            "visibilityOff"    => "", // Hide
            "warning"          => "", // Warning
            _                  => "", // Help (unknown name)
        };
    }
}

public sealed class VideoRenderer : IComponentRenderer
{
    private readonly MediaResolver _media;
    public VideoRenderer(MediaResolver media) { _media = media; }
    public string ComponentName => "Video";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            MinHeight = 180,
        };
        var urlVal = ctx.GetValue(c, "url");
        void Update()
        {
            var url = ctx.ResolveString(urlVal);
            // Gate is allowlist + IP-literal-public only — DNS-rebinding pin
            // would not hold here because MediaSource.CreateFromUri does its
            // own resolution at playback. See MediaResolver.TryResolveMediaUri.
            var uri = string.IsNullOrEmpty(url) ? null : _media.TryResolveMediaUri(url);
            player.Source = uri == null ? null : global::Windows.Media.Core.MediaSource.CreateFromUri(uri);
        }
        Update();
        ctx.WatchValue(c.Id, "url", urlVal, Update);
        AutomationHelpers.Apply(player, c, ctx);
        return player;
    }
}

public sealed class AudioPlayerRenderer : IComponentRenderer
{
    private readonly MediaResolver _media;
    public AudioPlayerRenderer(MediaResolver media) { _media = media; }
    public string ComponentName => "AudioPlayer";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var stack = new StackPanel { Spacing = 4 };
        var description = new TextBlock();
        var descVal = ctx.GetValue(c, "description");
        void DescUpdate() => description.Text = ctx.ResolveString(descVal) ?? string.Empty;
        DescUpdate();
        ctx.WatchValue(c.Id, "description", descVal, DescUpdate);
        if (Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var capStyle) && capStyle is Style cs)
            description.Style = cs;

        var player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            MinHeight = 50,
        };
        var urlVal = ctx.GetValue(c, "url");
        void UrlUpdate()
        {
            var url = ctx.ResolveString(urlVal);
            var uri = string.IsNullOrEmpty(url) ? null : _media.TryResolveMediaUri(url);
            player.Source = uri == null ? null : global::Windows.Media.Core.MediaSource.CreateFromUri(uri);
        }
        UrlUpdate();
        ctx.WatchValue(c.Id, "url", urlVal, UrlUpdate);

        if (!string.IsNullOrEmpty(description.Text)) stack.Children.Add(description);
        stack.Children.Add(player);
        AutomationHelpers.Apply(stack, c, ctx);
        return stack;
    }
}
