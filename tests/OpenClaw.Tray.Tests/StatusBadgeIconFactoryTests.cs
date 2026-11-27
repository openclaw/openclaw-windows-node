using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Verifies the lobster tray/desktop icon badge policy: only attention states are
/// badged (grey dot for disconnected, red dot with a "-" for error); healthy
/// states (connected/connecting) render the plain lobster with no badge.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatusBadgeIconFactoryTests
{
    [Theory]
    [InlineData((int)ConnectionStatusAccent.Success, 76, 175, 80)]
    [InlineData((int)ConnectionStatusAccent.Caution, 255, 193, 7)]
    [InlineData((int)ConnectionStatusAccent.Critical, 244, 67, 54)]
    [InlineData((int)ConnectionStatusAccent.Neutral, 158, 158, 158)]
    public void DotColor_MapsAccentToStatusColor(int accent, int r, int g, int b)
    {
        var color = StatusBadgeIconFactory.DotColor((ConnectionStatusAccent)accent);
        Assert.Equal(Color.FromArgb(r, g, b), color);
    }

    [Theory]
    [InlineData((int)ConnectionStatusAccent.Neutral, true)]
    [InlineData((int)ConnectionStatusAccent.Critical, true)]
    [InlineData((int)ConnectionStatusAccent.Success, false)]
    [InlineData((int)ConnectionStatusAccent.Caution, false)]
    public void ShouldBadge_OnlyNeutralAndCritical(int accent, bool expected)
    {
        Assert.Equal(expected, StatusBadgeIconFactory.ShouldBadge((ConnectionStatusAccent)accent));
    }

    [Theory]
    [InlineData((int)ConnectionStatusAccent.Critical, true)]
    [InlineData((int)ConnectionStatusAccent.Neutral, false)]
    [InlineData((int)ConnectionStatusAccent.Success, false)]
    [InlineData((int)ConnectionStatusAccent.Caution, false)]
    public void HasDash_OnlyCritical(int accent, bool expected)
    {
        Assert.Equal(expected, StatusBadgeIconFactory.HasDash((ConnectionStatusAccent)accent));
    }

    [Fact]
    public void Compose_DrawsDotInBottomRightCorner()
    {
        const int size = 64;
        using var baseImage = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        // Fully transparent base so the only opaque pixels come from the dot.

        using var composed = StatusBadgeIconFactory.Compose(
            baseImage, size, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Neutral));

        // Bottom-right region carries the dot.
        var dotPixel = composed.GetPixel((int)(size * 0.80), (int)(size * 0.80));
        Assert.True(dotPixel.A > 200, "Dot should be opaque in the bottom-right corner");

        // Top-left stays transparent (no badge, base was empty).
        var cornerPixel = composed.GetPixel(2, 2);
        Assert.True(cornerPixel.A < 40, "Top-left corner should remain transparent");
    }

    [Fact]
    public void Compose_NullColor_RendersPlainLobsterWithNoBadge()
    {
        const int size = 64;
        using var baseImage = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        // Transparent base + null dot colour => the whole icon stays transparent.

        using var composed = StatusBadgeIconFactory.Compose(baseImage, size, dotColor: null);

        var dotPixel = composed.GetPixel((int)(size * 0.80), (int)(size * 0.80));
        Assert.True(dotPixel.A < 40, "Healthy state should render no dot in the bottom-right corner");
    }

    [Fact]
    public void Compose_ErrorState_DrawsRedDotWithWhiteDash()
    {
        const int size = 64;
        using var baseImage = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var composed = StatusBadgeIconFactory.Compose(
            baseImage, size, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Critical), withDash: true);

        var (cx, cy, dot) = DotCenter(size);

        // Centre of the dot is covered by the white minus glyph.
        var centre = composed.GetPixel((int)cx, (int)cy);
        Assert.True(centre.R > 230 && centre.G > 230 && centre.B > 230, "Dash centre should be white");

        // Below the dash (still inside the dot) reads red.
        var belowDash = composed.GetPixel((int)cx, (int)(cy + dot * 0.30f));
        Assert.True(belowDash.A > 200 && belowDash.R > belowDash.G && belowDash.R > belowDash.B,
            "Dot around the dash should read red");
    }

    [Fact]
    public void Compose_UsesDistinctColorPerAccent()
    {
        const int size = 64;
        using var baseImage = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var px = (int)(size * 0.80);

        using var neutral = StatusBadgeIconFactory.Compose(baseImage, size, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Neutral));
        using var critical = StatusBadgeIconFactory.Compose(baseImage, size, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Critical));

        var gray = neutral.GetPixel(px, px);
        var red = critical.GetPixel(px, px);
        Assert.True(Math.Abs(gray.R - gray.G) < 20 && Math.Abs(gray.G - gray.B) < 20, "Neutral dot is grey");
        Assert.True(red.R > red.G && red.R > red.B, "Critical dot is red-dominant");
    }

    [Fact]
    public void DotFraction_ScalesLargerOnTinyIconsAndSubtlerOnLargeIcons()
    {
        // Tiny tray icons get the largest dot for legibility.
        Assert.Equal(0.44, StatusBadgeIconFactory.DotFraction(16), 3);
        Assert.Equal(0.44, StatusBadgeIconFactory.DotFraction(32), 3);

        // Large taskbar / alt-tab icons get the subtlest dot.
        Assert.Equal(0.26, StatusBadgeIconFactory.DotFraction(256), 3);

        // Monotonically shrinks as the icon grows between the two extremes.
        var f48 = StatusBadgeIconFactory.DotFraction(48);
        var f128 = StatusBadgeIconFactory.DotFraction(128);
        Assert.True(f48 < 0.44 && f48 > f128, "48px dot fraction sits between the extremes");
        Assert.True(f128 > 0.26 && f128 < f48, "128px dot fraction is subtler than 48px but above the floor");
    }

    [Fact]
    public void CreateIcoBytes_ProducesValidMultiSizeIcon()
    {
        var sizes = new[] { 16, 32, 48 };
        using var baseImage = new Bitmap(256, 256, PixelFormat.Format32bppArgb);

        var bytes = StatusBadgeIconFactory.CreateIcoBytes(
            baseImage, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Critical), withDash: true, sizes);

        // ICONDIR header: reserved=0, type=1 (icon), count=frames.
        Assert.Equal(0, bytes[0] | bytes[1]);
        Assert.Equal(1, bytes[2] | (bytes[3] << 8));
        var count = bytes[4] | (bytes[5] << 8);
        Assert.Equal(sizes.Length, count);

        // The container round-trips back into a real icon.
        using var stream = new MemoryStream(bytes);
        using var icon = new Icon(stream);
        Assert.NotNull(icon);
    }

    [Fact]
    public void CreateIcoBytes_NullColor_ProducesValidPlainIcon()
    {
        var sizes = new[] { 16, 32 };
        using var baseImage = new Bitmap(256, 256, PixelFormat.Format32bppArgb);

        var bytes = StatusBadgeIconFactory.CreateIcoBytes(baseImage, dotColor: null, withDash: false, sizes);

        using var stream = new MemoryStream(bytes);
        using var icon = new Icon(stream);
        Assert.NotNull(icon);
    }

    [Fact]
    public void IconSizes_CoverTrayAndTaskbarResolutions()
    {
        Assert.Contains(16, StatusBadgeIconFactory.IconSizes);   // tray at 100% DPI
        Assert.Contains(32, StatusBadgeIconFactory.IconSizes);   // tray at 200% DPI / taskbar
        Assert.Contains(256, StatusBadgeIconFactory.IconSizes);  // high-DPI taskbar
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishIcon_ConcurrentPublishersNeverExposeAnIncompleteIconToReaders(bool damagedTarget)
    {
        // Several tray processes start together and publish the same icon while each one,
        // like WinUIEx TrayIcon, loads a returned path with LoadImage. Large frames widen
        // the window in which a rewrite in place would expose a truncated file. With a
        // damaged target, every publisher tries to repair it concurrently.
        var bytes = CreateNoisyIcoBytes();
        using var root = new TempDirectory();
        var targetName = Path.GetFileName(
            StatusBadgeIconFactory.PublishIcon(Path.Combine(root.Path, "name"), "openclaw-neutral", bytes));
        const int Rounds = 30;
        const int Publishers = 4;
        const int PublishesPerPublisher = 5;
        var publisherFailures = new ConcurrentQueue<string>();
        var loadFailures = new ConcurrentQueue<string>();
        var loads = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var directory = Path.Combine(root.Path, round.ToString());
            if (damagedTarget)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, targetName), bytes.AsSpan(0, bytes.Length / 2).ToArray());
            }
            using var start = new Barrier(Publishers);
            var published = new ConcurrentQueue<string>();
            var publishers = Enumerable.Range(0, Publishers).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                for (var publish = 0; publish < PublishesPerPublisher; publish++)
                {
                    try { published.Enqueue(StatusBadgeIconFactory.PublishIcon(directory, "openclaw-neutral", bytes)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        publisherFailures.Enqueue(ex.Message);
                    }
                }
            })).ToArray();
            var publishing = Task.WhenAll(publishers);

            while (!publishing.IsCompleted || !published.IsEmpty)
            {
                if (!published.TryDequeue(out var path))
                {
                    Thread.Yield();
                    continue;
                }
                var icon = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LoadFromFile);
                if (icon == IntPtr.Zero)
                    loadFailures.Enqueue($"{Path.GetFileName(path)}: Win32 error {Marshal.GetLastWin32Error()}");
                else
                    DestroyIcon(icon);
                loads++;
            }
            await publishing;
        }

        Assert.Equal(Rounds * Publishers * PublishesPerPublisher - publisherFailures.Count, loads);
        Assert.True(loadFailures.IsEmpty && publisherFailures.IsEmpty,
            $"{loadFailures.Count} of {loads} loads failed: {string.Join(" | ", loadFailures.Take(3))}; " +
            $"{publisherFailures.Count} publishes failed: {string.Join(" | ", publisherFailures.Take(3))}");
    }

    [Fact]
    public void PublishIcon_ReplacesADamagedPublishedIconWithCompleteContent()
    {
        var bytes = CreateNoisyIcoBytes();
        using var root = new TempDirectory();
        var published = StatusBadgeIconFactory.PublishIcon(root.Path, "openclaw-neutral", bytes);
        File.WriteAllBytes(published, bytes.AsSpan(0, bytes.Length / 2).ToArray());

        var republished = StatusBadgeIconFactory.PublishIcon(root.Path, "openclaw-neutral", bytes);

        Assert.Equal(bytes, File.ReadAllBytes(republished));
        var icon = LoadImage(IntPtr.Zero, republished, ImageIcon, 0, 0, LoadFromFile);
        Assert.NotEqual(IntPtr.Zero, icon);
        DestroyIcon(icon);
    }

    [Fact]
    public void PublishIcon_DifferentContentNeverReplacesAnotherBuildsIcon()
    {
        using var root = new TempDirectory();
        var first = CreateNoisyIcoBytes(seed: 1);
        var second = CreateNoisyIcoBytes(seed: 2);

        var firstPath = StatusBadgeIconFactory.PublishIcon(root.Path, "openclaw-neutral", first);
        var secondPath = StatusBadgeIconFactory.PublishIcon(root.Path, "openclaw-neutral", second);

        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(first, File.ReadAllBytes(firstPath));
        Assert.Equal(second, File.ReadAllBytes(secondPath));
    }

    [Fact]
    public void PublishIcon_RemovesOnlyThisHostsStagingCopiesForExitedWriters()
    {
        var bytes = CreateNoisyIcoBytes();
        using var root = new TempDirectory();
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        exited.WaitForExit();
        var host = StatusBadgeIconFactory.HostTag;
        var abandoned = Path.Combine(root.Path, $"openclaw-neutral-0123456789abcdef.{host}-{exited.Id}-{Guid.NewGuid():N}.ico");
        var ownCopy = Path.Combine(root.Path, $"openclaw-neutral-0123456789abcdef.{host}-{Environment.ProcessId}-{Guid.NewGuid():N}.ico");
        var otherHost = Path.Combine(root.Path, $"openclaw-neutral-0123456789abcdef.{(host == "00000000" ? "11111111" : "00000000")}-{exited.Id}-{Guid.NewGuid():N}.ico");
        var otherStem = Path.Combine(root.Path, $"openclaw-critical-0123456789abcdef.{host}-{exited.Id}-{Guid.NewGuid():N}.ico");
        foreach (var path in new[] { abandoned, ownCopy, otherHost, otherStem })
            File.WriteAllBytes(path, bytes);

        StatusBadgeIconFactory.PublishIcon(root.Path, "openclaw-neutral", bytes);

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(ownCopy));
        Assert.True(File.Exists(otherHost));
        Assert.True(File.Exists(otherStem));
    }

    private static byte[] CreateNoisyIcoBytes(int seed = 7)
    {
        using var baseImage = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        var random = new Random(seed);
        for (var y = 0; y < baseImage.Height; y++)
            for (var x = 0; x < baseImage.Width; x++)
                baseImage.SetPixel(x, y, Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
        return StatusBadgeIconFactory.CreateIcoBytes(
            baseImage, StatusBadgeIconFactory.DotColor(ConnectionStatusAccent.Neutral), withDash: false,
            StatusBadgeIconFactory.IconSizes);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "openclaw-status-icon-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    // Mirrors the dot geometry in StatusBadgeIconFactory.Compose so tests can sample
    // the dot centre precisely.
    private static (float Cx, float Cy, float Dot) DotCenter(int size)
    {
        float dot = size * (float)StatusBadgeIconFactory.DotFraction(size);
        float ring = Math.Max(1f, dot * 0.14f);
        float pad = size * 0.02f;
        float outer = dot + (ring * 2f);
        float outerX = size - outer - pad;
        float outerY = size - outer - pad;
        return (outerX + ring + (dot / 2f), outerY + ring + (dot / 2f), dot);
    }
}
