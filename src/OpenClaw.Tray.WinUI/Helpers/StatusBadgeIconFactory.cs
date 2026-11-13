using OpenClawTray.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace OpenClawTray.Helpers;

/// <summary>
/// Builds application icons that surface the connection status on the lobster
/// mascot as a small badge in the bottom-right corner. To avoid a distracting
/// always-on indicator, only attention states are badged:
/// <list type="bullet">
///   <item>disconnected / neutral → a grey dot,</item>
///   <item>error → a red dot with a white "-" (minus) glyph.</item>
/// </list>
/// Healthy states (connected, connecting) render the plain lobster with no badge.
/// Composed icons are cached per <see cref="ConnectionStatusAccent"/> and published
/// to the app's local data folder as multi-resolution .ico files so both the tray icon
/// (<see cref="WinUIEx.TrayIcon.SetIcon(string)"/>) and the desktop/taskbar window
/// icon (<c>Window.SetIcon(string)</c>) can consume them. Other tray processes may
/// share that folder, so a published file is never rewritten in place; see
/// <see cref="PublishIcon"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StatusBadgeIconFactory
{
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string OutputDir = Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "StatusIcons");
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ConnectionStatusAccent, string> Cache = new();

    // Cover the sizes the shell requests for the tray (16-32 by DPI) and the
    // taskbar/window (up to 256), so LoadImage always finds a crisp match.
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    /// <summary>
    /// Returns a filesystem path to a lobster icon badged with the status dot for
    /// <paramref name="accent"/>. Falls back to the plain app icon on failure.
    /// </summary>
    public static string GetBadgedIconPath(ConnectionStatusAccent accent)
    {
        if (Cache.TryGetValue(accent, out var cached) && File.Exists(cached))
            return cached;

        lock (Gate)
        {
            if (Cache.TryGetValue(accent, out cached) && File.Exists(cached))
                return cached;

            var (path, isFallback) = Build(accent);

            // Only cache successfully composed icons. Caching the fallback would
            // permanently pin the un-badged icon after a transient GDI+ failure.
            if (!isFallback)
                Cache[accent] = path;

            return path;
        }
    }

    /// <summary>Maps a status accent to its dot colour (mirrors the companion-app dot).</summary>
    public static Color DotColor(ConnectionStatusAccent accent) => accent switch
    {
        ConnectionStatusAccent.Success => Color.FromArgb(76, 175, 80),   // Green  – connected
        ConnectionStatusAccent.Caution => Color.FromArgb(255, 193, 7),   // Amber  – connecting / attention
        ConnectionStatusAccent.Critical => Color.FromArgb(244, 67, 54),  // Red    – error
        _ => Color.FromArgb(158, 158, 158),                              // Gray   – disconnected / neutral
    };

    /// <summary>
    /// Whether <paramref name="accent"/> gets a status badge. Only attention states
    /// are badged so the icon is quiet when everything is healthy: disconnected
    /// (neutral) and error (critical). Connected/connecting render the plain lobster.
    /// </summary>
    public static bool ShouldBadge(ConnectionStatusAccent accent) =>
        accent is ConnectionStatusAccent.Neutral or ConnectionStatusAccent.Critical;

    /// <summary>Whether the badge carries the "-" (minus) glyph. Only the error state does.</summary>
    public static bool HasDash(ConnectionStatusAccent accent) =>
        accent is ConnectionStatusAccent.Critical;

    private static (string Path, bool IsFallback) Build(ConnectionStatusAccent accent)
    {
        var fallback = Path.Combine(AssetsPath, "openclaw.ico");
        try
        {
            // Only attention states (neutral/critical) get a dot; healthy states
            // render the plain lobster (null dot colour).
            Color? dotColor = ShouldBadge(accent) ? DotColor(accent) : null;

            using var baseImage = LoadBaseImage();
            var bytes = CreateIcoBytes(baseImage, dotColor, HasDash(accent), Sizes);
            return (PublishIcon(OutputDir, $"openclaw-{accent}".ToLowerInvariant(), bytes), false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to build status-badged icon for {accent}: {ex.Message}");
            return (fallback, true);
        }
    }

    /// <summary>
    /// Publishes icon bytes where concurrent tray processes and the shell can read them.
    /// Readers load an icon by path at any time, so a published path must only ever hold
    /// complete content:
    /// <list type="bullet">
    ///   <item>the name carries a content hash, so different builds never share a file;</item>
    ///   <item>this process writes and flushes a private staging file, then renames it into
    ///   place without overwriting; an existing target is reused only when it holds the
    ///   same bytes;</item>
    ///   <item>a target proven to hold other bytes (damaged) is replaced atomically by one
    ///   process at a time, after re-checking it under an exclusive lock file;</item>
    ///   <item>when the target cannot be read or repaired, or another process is repairing
    ///   it, this process loads its own complete staging copy. This host's staging files left
    ///   by exited processes are removed.</item>
    /// </list>
    /// Exposed for testing.
    /// </summary>
    internal static string PublishIcon(string directory, string stem, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
        var target = Path.Combine(directory, $"{stem}-{hash}.ico");
        RemoveAbandonedStagingFiles(directory, stem);
        if (ReadContent(target, bytes) == PublishedContent.Same)
            return target;

        var staging = Path.Combine(directory, $"{stem}-{hash}.{HostTag}-{Environment.ProcessId}-{Guid.NewGuid():N}.ico");
        try
        {
            using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // A partial staging file must never be loaded or published.
            TryDelete(staging);
            throw;
        }

        try
        {
            File.Move(staging, target, overwrite: false);
            return target;
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another process published first, or a damaged file holds the content name.
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        return ReuseOrRepair(target, staging, bytes);
    }

    private static string ReuseOrRepair(string target, string staging, byte[] bytes)
    {
        switch (ReadContent(target, bytes))
        {
            case PublishedContent.Same:
                TryDelete(staging);
                return target;
            case PublishedContent.Different:
                break;
            default:
                // The target could not be read (for example while it is briefly locked).
                // This process's complete private copy is safe to load.
                return staging;
        }

        // Replacing opens the target for deletion, which concurrent readers see as a sharing
        // violation. One repairer at a time holds an exclusive lock file beside the target and
        // re-checks it, so a repaired target is never replaced again. The lock is file-based so
        // it also covers other Windows sessions and hosts that share the folder, and it is never
        // waited for: a busy lock means this process loads its own complete copy.
        FileStream repairLock;
        try
        {
            repairLock = new FileStream(target + ".repair-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return staging;
        }
        using (repairLock)
        {
            switch (ReadContent(target, bytes))
            {
                case PublishedContent.Same:
                    TryDelete(staging);
                    return target;
                case PublishedContent.Different:
                    try
                    {
                        File.Move(staging, target, overwrite: true);
                        return target;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Logger.Warn($"Status icon {Path.GetFileName(target)} is damaged and in use; using a private copy: {ex.Message}");
                        return staging;
                    }
                default:
                    return staging;
            }
        }
    }

    // Staging names carry the writing host, so a folder shared across machines never has a
    // live copy removed on the basis of a process ID from another host.
    internal static readonly string HostTag = Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName.ToUpperInvariant())), 0, 4).ToLowerInvariant();

    /// <summary>
    /// Removes this host's staging copies whose writer has exited. A live, inaccessible or
    /// reused process ID keeps its files, so a copy that a running process may still load is
    /// never deleted.
    /// </summary>
    private static void RemoveAbandonedStagingFiles(string directory, string stem)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(stem) +
            @"-[0-9a-f]{16}\.(?<host>[0-9a-f]{8})-(?<pid>\d+)-[0-9a-f]{32}\.ico$");
        string[] candidates;
        try { candidates = Directory.GetFiles(directory, stem + "-*.*-*-*.ico"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var candidate in candidates)
        {
            var match = pattern.Match(Path.GetFileName(candidate));
            if (!match.Success || match.Groups["host"].Value != HostTag ||
                !int.TryParse(match.Groups["pid"].Value, out var pid) ||
                pid == Environment.ProcessId || IsProcessRunning(pid))
                continue;
            TryDelete(candidate);
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Inaccessible processes are still running.
            return true;
        }
    }

    private enum PublishedContent { Missing, Same, Different, Unreadable }

    private static PublishedContent ReadContent(string path, byte[] expected)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)
                ? PublishedContent.Same
                : PublishedContent.Different;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PublishedContent.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PublishedContent.Unreadable;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Composes the (optionally badged) lobster at every requested size and packs
    /// the frames into a single multi-resolution .ico byte array. A null
    /// <paramref name="dotColor"/> renders the plain lobster. Exposed for testing.
    /// </summary>
    internal static byte[] CreateIcoBytes(Bitmap baseImage, Color? dotColor, bool withDash, IReadOnlyList<int> sizes)
    {
        var frames = new List<byte[]>(sizes.Count);
        foreach (var size in sizes)
        {
            using var composed = Compose(baseImage, size, dotColor, withDash);
            using var ms = new MemoryStream();
            composed.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
        }

        return BuildIco(frames, sizes);
    }

    /// <summary>The icon sizes baked into every composed .ico container.</summary>
    internal static IReadOnlyList<int> IconSizes => Sizes;

    private static Bitmap LoadBaseImage()
    {
        // Prefer the high-resolution unplated mascot PNG (transparent background)
        // for crisp scaling; fall back to the multi-size app .ico.
        var png = Path.Combine(AssetsPath, "Square44x44Logo.targetsize-256_altform-unplated.png");
        if (File.Exists(png))
            return new Bitmap(png);

        var ico = Path.Combine(AssetsPath, "openclaw.ico");
        using var icon = new Icon(ico, 256, 256);
        return icon.ToBitmap();
    }

    /// <summary>
    /// The dot diameter as a fraction of the icon size. Tiny tray icons need a
    /// proportionally larger dot to stay legible at 16-32px, while large taskbar
    /// / alt-tab icons look cleaner with a subtler corner dot. Interpolates
    /// between the two extremes. Exposed for testing.
    /// </summary>
    internal static double DotFraction(int size)
    {
        const double maxFraction = 0.44; // <= 32px  (tray legibility)
        const double minFraction = 0.26; // >= 256px (subtle corner dot)
        const int minSize = 32;
        const int maxSize = 256;

        if (size <= minSize)
            return maxFraction;
        if (size >= maxSize)
            return minFraction;

        var t = (double)(size - minSize) / (maxSize - minSize);
        return maxFraction + (t * (minFraction - maxFraction));
    }

    internal static Bitmap Compose(Bitmap baseImage, int size, Color? dotColor, bool withDash = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        g.DrawImage(baseImage, new Rectangle(0, 0, size, size));

        // No badge for healthy states: render the plain lobster.
        if (dotColor is not Color color)
            return bmp;

        // Dot geometry: bottom-right corner with a white ring for contrast against
        // both the red mascot and arbitrary taskbar backgrounds. The dot scales by
        // size so it is legible on tiny tray icons yet subtle on large icons; the
        // ring is tied to the dot so it shrinks proportionally too.
        float dot = size * (float)DotFraction(size);
        float ring = Math.Max(1f, dot * 0.14f);
        float pad = size * 0.02f;
        float outer = dot + (ring * 2f);
        float outerX = size - outer - pad;
        float outerY = size - outer - pad;

        using (var ringBrush = new SolidBrush(Color.White))
            g.FillEllipse(ringBrush, outerX, outerY, outer, outer);

        using (var dotBrush = new SolidBrush(color))
            g.FillEllipse(dotBrush, outerX + ring, outerY + ring, dot, dot);

        // Error badge carries a white "-" (minus) glyph centred on the dot.
        if (withDash)
        {
            float cx = outerX + ring + (dot / 2f);
            float cy = outerY + ring + (dot / 2f);
            float dashW = dot * 0.52f;
            float dashH = Math.Max(2f, dot * 0.18f);
            using var dashBrush = new SolidBrush(Color.White);
            g.FillRectangle(dashBrush, cx - (dashW / 2f), cy - (dashH / 2f), dashW, dashH);
        }

        return bmp;
    }

    /// <summary>
    /// Packs PNG frames into a Vista-style .ico container (PNG-compressed entries,
    /// supported by LoadImage on Windows 10+).
    /// </summary>
    private static byte[] BuildIco(IReadOnlyList<byte[]> pngFrames, IReadOnlyList<int> sizes)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var count = (ushort)pngFrames.Count;
        writer.Write((ushort)0); // idReserved
        writer.Write((ushort)1); // idType = icon
        writer.Write(count);     // idCount

        var offset = 6 + (16 * count);
        for (var i = 0; i < pngFrames.Count; i++)
        {
            var dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]); // 0 encodes 256
            writer.Write(dim);              // bWidth
            writer.Write(dim);              // bHeight
            writer.Write((byte)0);          // bColorCount
            writer.Write((byte)0);          // bReserved
            writer.Write((ushort)1);        // wPlanes
            writer.Write((ushort)32);       // wBitCount
            writer.Write((uint)pngFrames[i].Length); // dwBytesInRes
            writer.Write((uint)offset);     // dwImageOffset
            offset += pngFrames[i].Length;
        }

        foreach (var frame in pngFrames)
            writer.Write(frame);

        writer.Flush();
        return ms.ToArray();
    }
}
