using OpenClaw.Shared;
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenClawTray.Helpers;

public enum VoiceTrayIconState
{
    Off,
    Armed,
    Listening,
    Speaking
}

/// <summary>
/// Provides icon resources for the tray application.
/// Creates dynamic status icons with lobster pixel art.
/// </summary>
public static class IconHelper
{
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string IconsPath = Path.Combine(AssetsPath, "Icons");
    private static readonly string GeneratedIconsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray",
        "GeneratedIcons");

    // Icon cache
    private static Icon? _connectedIcon;
    private static Icon? _disconnectedIcon;
    private static Icon? _activityIcon;
    private static Icon? _errorIcon;
    private static Icon? _appIcon;
    private static string? _voiceArmedIconPath;
    private static string? _voiceListeningIconPath;
    private static string? _voiceSpeakingIconPath;

    public static string GetStatusIconPath(ConnectionStatus status)
    {
        var iconName = status switch
        {
            ConnectionStatus.Connected => "StatusConnected.ico",
            ConnectionStatus.Connecting => "StatusConnecting.ico",
            ConnectionStatus.Error => "StatusError.ico",
            _ => "StatusDisconnected.ico"
        };

        var path = Path.Combine(IconsPath, iconName);
        
        // If specific icon doesn't exist, fall back to main icon
        if (!File.Exists(path))
        {
            path = Path.Combine(AssetsPath, "openclaw.ico");
        }

        return path;
    }

    public static string GetAppIconPath()
    {
        var path = Path.Combine(AssetsPath, "openclaw.ico");
        if (File.Exists(path))
        {
            return path;
        }

        return GetStatusIconPath(ConnectionStatus.Disconnected);
    }

    public static string GetVoiceTrayIconPath(VoiceTrayIconState state)
    {
        return state switch
        {
            VoiceTrayIconState.Armed => GetOrCreateVoiceIconPath(ref _voiceArmedIconPath, VoiceTrayIconState.Armed),
            VoiceTrayIconState.Listening => GetOrCreateVoiceIconPath(ref _voiceListeningIconPath, VoiceTrayIconState.Listening),
            VoiceTrayIconState.Speaking => GetOrCreateVoiceIconPath(ref _voiceSpeakingIconPath, VoiceTrayIconState.Speaking),
            _ => GetAppIconPath()
        };
    }

    public static Icon GetStatusIcon(ConnectionStatus status)
    {
        return status switch
        {
            ConnectionStatus.Connected => GetOrCreateIcon(ref _connectedIcon, ConnectionStatus.Connected),
            ConnectionStatus.Connecting => GetOrCreateIcon(ref _activityIcon, ConnectionStatus.Connecting),
            ConnectionStatus.Error => GetOrCreateIcon(ref _errorIcon, ConnectionStatus.Error),
            _ => GetOrCreateIcon(ref _disconnectedIcon, ConnectionStatus.Disconnected)
        };
    }

    public static Icon GetAppIcon()
    {
        if (_appIcon != null) return _appIcon;

        var iconPath = GetAppIconPath();
        if (File.Exists(iconPath))
        {
            _appIcon = new Icon(iconPath);
        }
        else
        {
            _appIcon = CreateLobsterIcon(Color.FromArgb(255, 99, 71)); // Lobster red
        }

        return _appIcon;
    }

    private static Icon GetOrCreateIcon(ref Icon? cached, ConnectionStatus status)
    {
        if (cached != null) return cached;

        var iconPath = GetStatusIconPath(status);
        if (File.Exists(iconPath))
        {
            cached = new Icon(iconPath);
        }
        else
        {
            // Generate dynamic icon
            var color = status switch
            {
                ConnectionStatus.Connected => Color.FromArgb(76, 175, 80),   // Green
                ConnectionStatus.Connecting => Color.FromArgb(255, 193, 7),  // Amber
                ConnectionStatus.Error => Color.FromArgb(244, 67, 54),       // Red
                _ => Color.FromArgb(158, 158, 158)                           // Gray
            };
            cached = CreateLobsterIcon(color);
        }

        return cached;
    }

    /// <summary>
    /// Creates a simple colored lobster icon programmatically.
    /// Uses pixel art style matching the original WinForms version.
    /// </summary>
    public static Icon CreateLobsterIcon(Color color)
    {
        const int size = 16;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        
        g.Clear(Color.Transparent);

        // Simple lobster silhouette (pixel art style)
        using var brush = new SolidBrush(color);
        
        // Body
        g.FillRectangle(brush, 6, 6, 4, 6);
        
        // Claws
        g.FillRectangle(brush, 3, 4, 2, 2);
        g.FillRectangle(brush, 11, 4, 2, 2);
        g.FillRectangle(brush, 4, 6, 2, 2);
        g.FillRectangle(brush, 10, 6, 2, 2);
        
        // Tail
        g.FillRectangle(brush, 7, 12, 2, 3);
        g.FillRectangle(brush, 5, 14, 6, 1);
        
        // Eyes
        using var eyeBrush = new SolidBrush(Color.White);
        g.FillRectangle(eyeBrush, 6, 5, 1, 1);
        g.FillRectangle(eyeBrush, 9, 5, 1, 1);

        // Convert bitmap to icon
        var hIcon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        
        // Clone to own the icon data
        var result = (Icon)icon.Clone();
        DestroyIcon(hIcon);
        
        return result;
    }

    private static string GetOrCreateVoiceIconPath(ref string? cachedPath, VoiceTrayIconState state)
    {
        if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        Directory.CreateDirectory(GeneratedIconsPath);
        var outputPath = Path.Combine(GeneratedIconsPath, $"voice-{state.ToString().ToLowerInvariant()}.ico");

        using var bitmap = CreateVoiceTrayBitmap(state);
        using var icon = CreateIcon(bitmap);
        using var stream = File.Create(outputPath);
        icon.Save(stream);

        cachedPath = outputPath;
        return outputPath;
    }

    private static Bitmap CreateVoiceTrayBitmap(VoiceTrayIconState state)
    {
        const int size = 32;
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        using (var baseIcon = new Icon(GetAppIconPath(), size, size))
        using (var baseBitmap = baseIcon.ToBitmap())
        {
            graphics.DrawImage(baseBitmap, 0, 0, size, size);
        }

        switch (state)
        {
            case VoiceTrayIconState.Armed:
                DrawHeadphones(graphics);
                break;
            case VoiceTrayIconState.Listening:
                DrawHeadphones(graphics);
                DrawMicrophone(graphics);
                break;
            case VoiceTrayIconState.Speaking:
                DrawHeadphones(graphics);
                DrawSpeaker(graphics);
                break;
        }

        return bitmap;
    }

    private static void DrawHeadphones(Graphics graphics)
    {
        using var shadowPen = new Pen(Color.FromArgb(96, 255, 255, 255), 4f);
        using var bandPen = new Pen(Color.FromArgb(42, 48, 58), 3f);
        using var earBrush = new SolidBrush(Color.FromArgb(42, 48, 58));

        graphics.DrawArc(shadowPen, 6, 3, 20, 16, 180, 180);
        graphics.DrawArc(bandPen, 6, 3, 20, 16, 180, 180);
        graphics.FillPath(earBrush, CreateRoundedRectanglePath(4, 12, 5, 10, 3));
        graphics.FillPath(earBrush, CreateRoundedRectanglePath(23, 12, 5, 10, 3));
    }

    private static void DrawMicrophone(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(33, 150, 243));
        using var pen = new Pen(Color.FromArgb(33, 150, 243), 2f);

        graphics.FillPath(brush, CreateRoundedRectanglePath(22, 17, 6, 9, 3));
        graphics.FillRectangle(brush, 24, 25, 2, 4);
        graphics.DrawArc(pen, 21, 27, 8, 5, 0, 180);
        graphics.DrawLine(pen, 20, 21, 15, 19);
    }

    private static void DrawSpeaker(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(76, 175, 80));
        using var pen = new Pen(Color.FromArgb(76, 175, 80), 2f);
        using var thinPen = new Pen(Color.FromArgb(76, 175, 80), 1.5f);

        var points = new[]
        {
            new Point(24, 17),
            new Point(19, 20),
            new Point(19, 24),
            new Point(24, 27)
        };

        graphics.FillPolygon(brush, points);
        graphics.DrawArc(pen, 22, 17, 6, 10, 300, 120);
        graphics.DrawArc(thinPen, 21, 14, 10, 16, 300, 120);
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        var result = (Icon)icon.Clone();
        DestroyIcon(handle);
        return result;
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(int x, int y, int width, int height, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, radius, radius, 180, 90);
        path.AddArc(x + width - radius, y, radius, radius, 270, 90);
        path.AddArc(x + width - radius, y + height - radius, radius, radius, 0, 90);
        path.AddArc(x, y + height - radius, radius, radius, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
