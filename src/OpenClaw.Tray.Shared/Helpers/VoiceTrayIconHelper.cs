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

public static class VoiceTrayIconHelper
{
    private static readonly string GeneratedIconsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray",
        "GeneratedIcons");

    private static string? _voiceArmedIconPath;
    private static string? _voiceListeningIconPath;
    private static string? _voiceSpeakingIconPath;

    public static string GetBaseAppIconPath()
    {
        return Path.Combine(ResolveAssetsPath(), "openclaw.ico");
    }

    public static string GetVoiceTrayIconPath(VoiceTrayIconState state)
    {
        return state switch
        {
            VoiceTrayIconState.Armed => GetOrCreateVoiceIconPath(ref _voiceArmedIconPath, VoiceTrayIconState.Armed),
            VoiceTrayIconState.Listening => GetOrCreateVoiceIconPath(ref _voiceListeningIconPath, VoiceTrayIconState.Listening),
            VoiceTrayIconState.Speaking => GetOrCreateVoiceIconPath(ref _voiceSpeakingIconPath, VoiceTrayIconState.Speaking),
            _ => GetBaseAppIconPath()
        };
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

        using (var baseIcon = new Icon(GetBaseAppIconPath(), size, size))
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
                DrawHeadphoneWaves(graphics);
                break;
            case VoiceTrayIconState.Speaking:
                DrawMicrophone(graphics);
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

    private static void DrawHeadphoneWaves(Graphics graphics)
    {
        using var wavePen = new Pen(Color.FromArgb(76, 175, 80), 2f);
        using var accentPen = new Pen(Color.FromArgb(76, 175, 80), 1.5f);

        graphics.DrawArc(wavePen, 0, 12, 8, 8, 270, 180);
        graphics.DrawArc(accentPen, 2, 14, 4, 4, 270, 180);
        graphics.DrawArc(wavePen, 24, 12, 8, 8, 90, 180);
        graphics.DrawArc(accentPen, 26, 14, 4, 4, 90, 180);
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

    private static string ResolveAssetsPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (File.Exists(Path.Combine(bundledPath, "openclaw.ico")))
        {
            return bundledPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var sourcePath = Path.Combine(current.FullName, "src", "OpenClaw.Tray.WinUI", "Assets");
            if (Directory.Exists(sourcePath))
            {
                return sourcePath;
            }

            current = current.Parent;
        }

        return bundledPath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
