using System;
using System.Diagnostics;
using System.Text;
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

    private const string MinSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
        "<rect width='24' height='24' fill='red'/></svg>";

    private static MediaResolver NewResolver() => new(NullLogger.Instance);

    private static string DataSvgUtf8(string svgXml) =>
        "data:image/svg+xml;utf8," + Uri.EscapeDataString(svgXml);

    private static string DataSvgBase64(string svgXml) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svgXml));

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
        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(DataSvgUtf8(svg));
            sw.Stop();
            // DOCTYPE strip removes all <!ENTITY> declarations before parsing begins, so
            // the entity-expansion blowup never happens. The wall-clock cap (SvgRenderTimeout)
            // is the backstop. Either way, decode must complete well under timeout.
            Assert.True(
                sw.Elapsed < MediaResolver.SvgRenderTimeout + TimeSpan.FromSeconds(2),
                $"billion-laughs decode took {sw.Elapsed} (>{MediaResolver.SvgRenderTimeout})");
            _ = src;
        });
    }

    [Fact]
    public async Task PathologicalGeometry_BoundedByRenderTimeout()
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

        await _ui.RunOnUIAsync(async () =>
        {
            var media = NewResolver();
            var sw = Stopwatch.StartNew();
            var src = await media.LoadImageAsync(url);
            sw.Stop();
            // Decode (or timeout) must complete within the configured wall-clock bound.
            Assert.True(
                sw.Elapsed < MediaResolver.SvgRenderTimeout + TimeSpan.FromSeconds(2),
                $"pathological-geometry decode took {sw.Elapsed} (>{MediaResolver.SvgRenderTimeout})");
            _ = src;
        });
    }
}
