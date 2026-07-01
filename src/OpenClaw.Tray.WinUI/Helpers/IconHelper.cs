using OpenClaw.Shared;
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenClawTray.Helpers;

/// <summary>
/// Provides icon resources for the tray application.
/// Creates fallback dynamic status icons when packaged assets are unavailable.
/// </summary>
public static class IconHelper
{
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string IconsPath = Path.Combine(AssetsPath, "Icons");

    // Icon cache
    private static Icon? _connectedIcon;
    private static Icon? _disconnectedIcon;
    private static Icon? _activityIcon;
    private static Icon? _errorIcon;
    private static Icon? _appIcon;

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

        var iconPath = Path.Combine(AssetsPath, "openclaw.ico");
        if (File.Exists(iconPath))
        {
            _appIcon = new Icon(iconPath);
        }
        else
        {
            _appIcon = CreateFallbackStatusIcon(Color.FromArgb(255, 99, 71));
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
            cached = CreateFallbackStatusIcon(color);
        }

        return cached;
    }

    /// <summary>
    /// Creates a simple colored fallback status icon.
    /// </summary>
    public static Icon CreateFallbackStatusIcon(Color color)
    {
        const int size = 16;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(color);
        
        // Body
        g.FillRectangle(brush, 6, 6, 4, 6);
        
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
