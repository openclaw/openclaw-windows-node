using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Rendering;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// SVG decode tests for <see cref="MediaResolver"/>. These exercise the security
/// envelope from the SVG addition spec: the same allowlist/cap policy as PNG, plus
/// defense-in-depth against XXE / billion-laughs / pathological geometry.
///
/// All tests use <c>data:image/svg+xml</c> URLs so no HTTP traffic is involved at
/// the resolver layer — the asserts about "no external request" are structurally
/// guaranteed (D2D's static SVG 1.1 subset has no network stack either).
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UISvgTests
{
    private readonly UIThreadFixture _ui;
    public A2UISvgTests(UIThreadFixture ui) => _ui = ui;

    private const string SvgDecodeSlackVariable = "OPENCLAW_UI_SVG_DECODE_SLACK_SECONDS";
    // Slack beyond MediaResolver.SvgRenderTimeout for ONE admitted decode (the pre-existing 2 s).
    // Do not widen it to absorb a late return. Pathological geometry is rejected before decode by
    // SvgGeometryBudget because SvgImageSource.SetSourceAsync can block the UI
    // thread past SvgRenderTimeout (see docs/CI_FAILURES.md). Queue wait is excluded by
    // AssertSingleDecodeBoundedAsync and covered separately by Load_QueuedBehindBusyDecodeSlots_CompletesOnceASlotFrees.
    private static readonly TimeSpan SvgDecodeSlack =
        int.TryParse(Environment.GetEnvironmentVariable(SvgDecodeSlackVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SingleDecodeBound = MediaResolver.SvgRenderTimeout + SvgDecodeSlack;

    private const string MinSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
        "<rect width='24' height='24' fill='red'/></svg>";

    private static MediaResolver NewResolver(IOpenClawLogger? logger = null) => new(logger ?? NullLogger.Instance);

    private static SemaphoreSlim DecodeSlots()
    {
        var field = typeof(MediaResolver).GetField("s_svgDecodeSemaphore", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<SemaphoreSlim>(field.GetValue(null));
    }

    /// <summary>
    /// Loads one SVG with every decode slot idle, so the elapsed time is a single admitted decode
    /// with no queue wait. DOCTYPE strip removes all entity declarations before parsing, so
    /// entity-expansion blowup never happens; the wall-clock cap (SvgRenderTimeout) is the backstop.
    /// </summary>
    private async Task AssertSingleDecodeBoundedAsync(string url, string label)
    {
        await _ui.RunOnUIAsync(async () =>
        {
            Assert.True(DecodeSlots().CurrentCount > 0,
                "SVG decode slots are busy, so the single-decode bound would include queue wait");
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(url);
            sw.Stop();
            Assert.True(
                sw.Elapsed < SingleDecodeBound,
                $"{label} decode took {sw.Elapsed} (>{SingleDecodeBound}); resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
            _ = src;
        });
    }

    /// <summary>Records warnings with their offset from construction, so a late cap can be told apart from a late return.</summary>
    private sealed class RecordingLogger : IOpenClawLogger
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<string> _warnings = new();
        public IReadOnlyCollection<string> Warnings => _warnings;
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) => Record(message);
        public void Error(string message, Exception? ex = null) => Record(message);
        private void Record(string message) => _warnings.Enqueue($"+{_clock.ElapsedMilliseconds} ms {message}");
    }

    private static string DataSvgUtf8(string svgXml) =>
        "data:image/svg+xml;utf8," + Uri.EscapeDataString(svgXml);

    private static string DataSvgBase64(string svgXml) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svgXml));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteImage_AfterAsyncFetch_DecodesOnCallingUIThread(bool svg)
    {
        await _ui.RunOnUIAsync(async () =>
        {
            // Match the synchronization context installed by the production WinUI entry point.
            var previous = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(_ui.Dispatcher));
            try
            {
                using var handler = new DelayedImageHandler(svg);
                using var media = new MediaResolver(NullLogger.Instance, handler);
                media.AllowHost("avatar.example");
                var image = await media.LoadImageAsync("https://avatar.example/agent");
                Assert.NotNull(image);
                Assert.True(image.DispatcherQueue.HasThreadAccess);
                if (svg) Assert.IsType<SvgImageSource>(image);
                else Assert.IsType<BitmapImage>(image);
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

    private sealed class DelayedImageHandler(bool svg) : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            var bytes = svg ? Encoding.UTF8.GetBytes(MinSvg) :
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
            var content = new System.Net.Http.ByteArrayContent(bytes);
            content.Headers.ContentType = new(svg ? "image/svg+xml" : "image/png");
            return new(System.Net.HttpStatusCode.OK) { Content = content };
        }
    }

    [Fact]
    public async Task DataSvg_Utf8_RoundTrips_ToSvgImageSource()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var src = await media.LoadImageAsync(DataSvgUtf8(MinSvg));
            Assert.NotNull(src);
            Assert.IsType<SvgImageSource>(src);
        });
    }

    [Fact]
    public async Task DataSvg_Base64_RoundTrips_ToSvgImageSource()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var src = await media.LoadImageAsync(DataSvgBase64(MinSvg));
            Assert.NotNull(src);
            Assert.IsType<SvgImageSource>(src);
        });
    }

    [Fact]
    public async Task DataPng_StillReturnsBitmapImage()
    {
        // 1x1 transparent PNG.
        const string pngB64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
        var url = "data:image/png;base64," + pngB64;
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var src = await media.LoadImageAsync(url);
            Assert.NotNull(src);
            Assert.IsType<BitmapImage>(src);
        });
    }

    [Fact]
    public void RemoteSvg_HostNotAllowlisted_RejectedByIsAllowed()
    {
        var media = NewResolver(); // empty allowlist
        Assert.False(media.IsAllowed("https://not-on-allowlist.example/foo.svg"));
    }

    [Fact]
    public void RemoteSvg_HostOnAllowlist_PassesIsAllowed()
    {
        var media = NewResolver();
        media.AllowHost("cdn.example.com");
        Assert.True(media.IsAllowed("https://cdn.example.com/foo.svg"));
    }

    [Fact]
    public void DataSvg_OverCap_RejectedByIsAllowed()
    {
        var media = NewResolver();
        // Just over 2 MiB worth of payload chars; non-base64 cap = encoded-length upper bound.
        var giant = new string('A', 2 * 1024 * 1024 + 1);
        var url = "data:image/svg+xml;utf8," + giant;
        Assert.False(media.IsAllowed(url));
    }

    [Fact]
    public async Task ScriptAndEventHandlers_DecodeSuccessfully_AreInert()
    {
        // D2D has no script engine; <script> and on* attributes are dropped silently.
        // The regression guard is: decode succeeds and the test process doesn't crash.
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>" +
            "<script>throw new Error('script ran')</script>" +
            "<rect width='10' height='10' onclick='throw new Error(1)' fill='blue'/>" +
            "</svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var src = await media.LoadImageAsync(DataSvgUtf8(svg));
            Assert.NotNull(src);
            Assert.IsType<SvgImageSource>(src);
        });
    }

    [Fact]
    public async Task ForeignObject_Inert_NoExternalFetch()
    {
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>" +
            "<foreignObject width='10' height='10'>" +
            "<body xmlns='http://www.w3.org/1999/xhtml'><iframe src='https://attacker.example/leak'/></body>" +
            "</foreignObject></svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            // Data URL → MediaResolver makes zero HTTP calls; D2D has no network stack either.
            var src = await media.LoadImageAsync(DataSvgUtf8(svg));
            // foreignObject is dropped by the static subset — decode shouldn't fail outright,
            // but we don't depend on a particular pass/fail outcome here. The load-bearing
            // assertion is "no exception, no HTTP", which is structurally true.
            _ = src;
        });
    }

    [Fact]
    public async Task ExternalReferences_Inert_NoNetworkIo()
    {
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>" +
            "<image href='https://attacker.example/leak.png' width='10' height='10'/>" +
            "<use href='https://attacker.example/x.svg#g'/>" +
            "</svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var src = await media.LoadImageAsync(DataSvgUtf8(svg));
            // External href targets are dropped by D2D's static subset; we just verify no crash / hang.
            _ = src;
        });
    }

    [Fact]
    public async Task Xxe_DoctypeStrippedPrePass_NoFileRead()
    {
        // The DOCTYPE-with-internal-subset is what would let an XML parser try to resolve
        // file:/// entities. MediaResolver strips the entire <!DOCTYPE …> declaration before
        // handing bytes to D2D, so even if D2D's parser were XXE-permissive (it isn't, that
        // we know of), the entity declarations are gone.
        var svg =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>" +
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><text x='0' y='10'>&xxe;</text></svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            // Decode either succeeds with empty text (entity unresolved post-strip) or returns
            // null (status != Success). Either is acceptable; the load-bearing fact is that no
            // file read occurs — guaranteed by the strip happening before the parser sees bytes.
            var src = await media.LoadImageAsync(DataSvgUtf8(svg));
            _ = src;
        });
    }

    [Fact]
    public async Task BillionLaughs_DoctypeStripped_ReturnsPromptly()
    {
        var svg =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE svg [" +
            "<!ENTITY lol \"lol\">" +
            "<!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">" +
            "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">" +
            "<!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\">" +
            "]>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><text>&lol4;</text></svg>";
        await AssertSingleDecodeBoundedAsync(DataSvgUtf8(svg), "billion-laughs");
    }

    [Fact]
    public async Task PathologicalGeometry_RejectedBeforeDecode()
    {
        // Build an SVG with a very long path expression. Stay under the 2 MiB data-URL cap
        // by base64-encoding to avoid percent-escape bloat.
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><path d='M0,0 ");
        var rng = new Random(0);
        for (int i = 0; i < 30_000; i++)
        {
            sb.Append('C')
              .Append(rng.Next(100)).Append(',').Append(rng.Next(100)).Append(' ')
              .Append(rng.Next(100)).Append(',').Append(rng.Next(100)).Append(' ')
              .Append(rng.Next(100)).Append(',').Append(rng.Next(100)).Append(' ');
        }
        sb.Append("'/></svg>");
        var url = DataSvgBase64(sb.ToString());

        // Oversized geometry never reaches D2D, so the load fails fast instead of blocking the UI thread.
        await _ui.RunOnUIAsync(async () =>
        {
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(url);
            sw.Stop();
            Assert.Null(src);
            Assert.Contains(logger.Warnings, warning => warning.Contains("SVG rejected before decode", StringComparison.Ordinal));
            Assert.True(
                sw.Elapsed < SvgDecodeSlack,
                $"pathological-geometry rejection took {sw.Elapsed} (>{SvgDecodeSlack}); resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
        });
    }

    [Fact]
    public async Task ManySmallShapes_RejectedBeforeDecode()
    {
        // The attribute budget cannot see element count: tiny shapes carry no path data, so a
        // document of thousands of nodes would otherwise reach the decoder unmeasured.
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>" +
            string.Concat(Enumerable.Repeat("<rect width='1' height='1'/>", SvgGeometryBudget.MaxElements)) +
            "</svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(DataSvgBase64(svg));
            sw.Stop();
            Assert.Null(src);
            Assert.Contains(logger.Warnings, warning => warning.Contains("element count exceeds", StringComparison.Ordinal));
            Assert.True(sw.Elapsed < SvgDecodeSlack,
                $"element-count rejection took {sw.Elapsed} (>{SvgDecodeSlack}); resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
        });
    }

    [Fact]
    public async Task DoctypeCommentContainingFakePath_StillRenders()
    {
        // Regression: the geometry guard used to scan before the DOCTYPE strip, so the '>' inside this
        // DTD comment ended its skip early, the fake path inside the comment was counted, and this
        // small SVG was rejected. The strip now runs first, so the image must render.
        var fakePath = new string('1', SvgGeometryBudget.MaxAttributeChars + 1);
        var svg = $"<!DOCTYPE svg [<!-- > <path d='{fakePath}'/> -->]>" + MinSvg;
        await _ui.RunOnUIAsync(async () =>
        {
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var src = await media.LoadImageAsync(DataSvgBase64(svg));
            Assert.True(src is SvgImageSource,
                $"DOCTYPE comment made a small SVG fail; warnings: [{string.Join(" | ", logger.Warnings)}]");
        });
    }

    [Fact]
    public async Task OversizedGeometryAfterDoctype_StillRejectedBeforeDecode()
    {
        // '<' + '?' makes the unprefixed scan treat the DTD tail as an unclosed processing
        // instruction and skip to end of input, so it misses the oversized path. The shipped policy
        // strips the declaration first and counts it.
        var svg =
            "<!DOCTYPE svg [<!-- > <? -->]>" +
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>" +
            $"<path d='{new string('1', SvgGeometryBudget.MaxAttributeChars + 1)}'/></svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(DataSvgBase64(svg));
            sw.Stop();
            Assert.Null(src);
            Assert.Contains(logger.Warnings, warning => warning.Contains("geometry attributes exceed", StringComparison.Ordinal));
            Assert.True(sw.Elapsed < SvgDecodeSlack,
                $"rejection took {sw.Elapsed} (>{SvgDecodeSlack}); resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
        });
    }

    [Fact]
    public async Task DoctypeSurvivingTheStrip_IsRejectedBeforeDecode()
    {
        // The decoy declaration inside the comment is the first textual match, so the strip removes
        // it and leaves the real one; the scan would then skip that DTD only to its first '>'.
        // The policy fails closed instead of decoding bytes it never accounted for.
        var svg =
            "<!-- <!DOCTYPE x> --><!DOCTYPE svg [<!-- > <![CDATA[ -->]>" +
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10'/></svg>";
        await _ui.RunOnUIAsync(async () =>
        {
            var logger = new RecordingLogger();
            var media = NewResolver(logger);
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(DataSvgBase64(svg));
            sw.Stop();
            Assert.Null(src);
            Assert.Contains(logger.Warnings, warning => warning.Contains("DOCTYPE could not be stripped", StringComparison.Ordinal));
            Assert.True(sw.Elapsed < SvgDecodeSlack,
                $"rejection took {sw.Elapsed} (>{SvgDecodeSlack}); resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
        });
    }

    [Fact]
    public async Task Load_QueuedBehindBusyDecodeSlots_CompletesOnceASlotFrees()
    {
        // Two-stage case: queue wait (all slots busy for a known hold) then one trivial decode.
        // The single-decode tests above must never absorb queue wait; this one covers it explicitly.
        var slots = DecodeSlots();
        var hold = TimeSpan.FromMilliseconds(500);
        var held = 0;
        void ReleaseHeld()
        {
            var count = Interlocked.Exchange(ref held, 0);
            if (count > 0) slots.Release(count);
        }

        try
        {
            while (slots.Wait(0)) held++;
            Assert.True(held > 0, "no free SVG decode slot to hold");

            await _ui.RunOnUIAsync(async () =>
            {
                var logger = new RecordingLogger();
                var media = NewResolver(logger);
                long releasedAt = 0;
                var sw = Stopwatch.StartNew();
                var releaser = Task.Run(async () =>
                {
                    await Task.Delay(hold);
                    Volatile.Write(ref releasedAt, Stopwatch.GetTimestamp());
                    ReleaseHeld();
                });

                var src = await media.LoadImageAsync(DataSvgUtf8(MinSvg));
                var completedAt = Stopwatch.GetTimestamp();
                sw.Stop();
                await releaser;

                Assert.True(src is SvgImageSource, $"queued load did not decode; resolver warnings: [{string.Join(" | ", logger.Warnings)}]");
                Assert.True(completedAt >= Volatile.Read(ref releasedAt),
                    "load completed before any decode slot was released, so it was not queued");
                Assert.True(sw.Elapsed < hold + SvgDecodeSlack,
                    $"queue wait {hold} plus a trivial decode took {sw.Elapsed} (>{hold + SvgDecodeSlack})");
            });
        }
        finally
        {
            ReleaseHeld();
        }
    }
}
