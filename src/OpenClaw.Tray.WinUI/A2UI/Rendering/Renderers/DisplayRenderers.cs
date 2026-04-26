using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

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
        try { tb.Style = (Style)Application.Current.Resources[resourceKey]; } catch { }
        // Some style keys don't exist on every WinUI version; fall back gracefully.
        if (tb.Style == null && hint != null)
        {
            try { tb.Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]; } catch { }
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
        ApplyFit(image, c.Properties["fit"]?.GetValue<string>());
        ApplyUsageHint(image, c.Properties["usageHint"]?.GetValue<string>());

        var urlVal = ctx.GetValue(c, "url");
        void Update()
        {
            var url = ctx.ResolveString(urlVal);
            if (!string.IsNullOrEmpty(url)) _ = LoadAsync(image, url);
        }
        Update();
        ctx.WatchValue(c.Id, "url", urlVal, Update);
        return image;
    }

    private async Task LoadAsync(Image target, string url)
    {
        var bmp = await _media.LoadImageAsync(url);
        if (bmp != null) target.Source = bmp;
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

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var fontIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
        };
        var nameVal = ctx.GetValue(c, "name");
        void Update() => fontIcon.Glyph = MapName(ctx.ResolveString(nameVal));
        Update();
        ctx.WatchValue(c.Id, "name", nameVal, Update);
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
            if (string.IsNullOrEmpty(url)) return;
            if (!_media.IsAllowed(url)) return;
            var uri = _media.AsUri(url);
            if (uri != null) player.Source = global::Windows.Media.Core.MediaSource.CreateFromUri(uri);
        }
        Update();
        ctx.WatchValue(c.Id, "url", urlVal, Update);
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
        try { description.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]; } catch { }

        var player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            MinHeight = 50,
        };
        var urlVal = ctx.GetValue(c, "url");
        void UrlUpdate()
        {
            var url = ctx.ResolveString(urlVal);
            if (string.IsNullOrEmpty(url)) return;
            if (!_media.IsAllowed(url)) return;
            var uri = _media.AsUri(url);
            if (uri != null) player.Source = global::Windows.Media.Core.MediaSource.CreateFromUri(uri);
        }
        UrlUpdate();
        ctx.WatchValue(c.Id, "url", urlVal, UrlUpdate);

        if (!string.IsNullOrEmpty(description.Text)) stack.Children.Add(description);
        stack.Children.Add(player);
        return stack;
    }
}
