using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine.Tests;

public sealed class ProviderArtworkTests
{
    public static TheoryData<string, string> Aliases => new()
    {
        { "claude-cli", "claude" }, { "claude-code", "claude" }, { "claude", "claude" }, { "anthropic", "claude" },
        { "codex-cli", "codex" }, { "codex", "codex" }, { "openai", "codex" }, { "chatgpt", "codex" },
        { "gemini-cli", "gemini" }, { "gemini", "gemini" }, { "googlegemini", "gemini" }, { "google", "gemini" },
        { "ollama", "ollama" }, { "lmstudio", "lmstudio" }, { "lm-studio", "lmstudio" },
        { "pi", "pi" }, { "opencode", "opencode" }, { "kimi-code", "kimi" }, { "kimi", "kimi" },
        { "moonshot", "kimi" }, { "grok-build", "xai" }, { "grok", "xai" }, { "xai", "xai" },
        { " xAI-OAuth \n", "xai" }, { "anthropic-vertex", "claude" }, { "google-gemini-cli", "gemini" },
        { "-xai-oauth", "xai" }
    };

    [Theory, MemberData(nameof(Aliases))]
    public void PinnedMacAliases_MapOnlyToNineBundledNames(string value, string resource) =>
        Assert.Equal($"ProviderIcon-{resource}.svg", GatewayAiSetupPresentation.GetBundledProviderIconFileName(value));

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("../claude")]
    [InlineData("https://host/claude")]
    [InlineData("lm-studio-oauth")]
    public void UnrecognizedNames_NeverBecomeResourcePaths(string? value) =>
        Assert.Null(GatewayAiSetupPresentation.GetBundledProviderIconFileName(value));

    [Fact]
    public void Artwork_PrefersBrandThenIdThenRemoteThenFallback()
    {
        var brand = GatewayAiSetupPresentation.GetProviderArtwork("google", "xai", "https://icons.example/logo", ProviderArtworkFallback.Key);
        Assert.Equal("ProviderIcon-gemini.svg", brand.BundledFileName);
        Assert.Null(brand.RemoteUri);
        var id = GatewayAiSetupPresentation.GetProviderArtwork("unknown", "xai-oauth", "http://localhost/logo", ProviderArtworkFallback.Pair);
        Assert.Equal("ProviderIcon-xai.svg", id.BundledFileName);
        Assert.False(id.RemoteRejected);
        var remote = GatewayAiSetupPresentation.GetProviderArtwork(null, "unknown", "https://icons.example/logo", ProviderArtworkFallback.Account);
        Assert.NotNull(remote.RemoteUri);
        var fallback = GatewayAiSetupPresentation.GetProviderArtwork(null, "unknown", "http://localhost/logo", ProviderArtworkFallback.Account);
        Assert.Null(fallback.RemoteUri);
        Assert.True(fallback.RemoteRejected);
        Assert.Equal(ProviderArtworkFallback.Account, fallback.Fallback);
    }

    [Theory]
    [InlineData("device-code", "Pair")]
    [InlineData("install", "Set up…")]
    [InlineData("custom", "Configure…")]
    [InlineData("oauth", "Sign in")]
    [InlineData(null, "Sign in")]
    public void MetadataActionLabel_WinsOverKindDefault(string? kind, string expected)
    {
        Assert.Equal(expected, GatewayAiSetupPresentation.GetProviderActionLabel(null, kind));
        Assert.Equal(expected, GatewayAiSetupPresentation.GetProviderActionLabel(" ", kind));
        Assert.Equal("Use provider", GatewayAiSetupPresentation.GetProviderActionLabel("Use provider", kind));
        Assert.Equal("Connect / Set up",
            GatewayAiSetupPresentation.GetProviderActionLabel(null, kind, GatewayAiSetupChoiceKind.Prepare));
        Assert.Equal("Prepare exact model",
            GatewayAiSetupPresentation.GetProviderActionLabel("Prepare exact model", kind, GatewayAiSetupChoiceKind.Prepare));
    }

    [Theory]
    [InlineData("https://icons.example/logo", true)]
    [InlineData("https://8.8.8.8/logo", true)]
    [InlineData("https://[2606:4700:4700::1111]/logo", true)]
    [InlineData("https://user:password@icons.example/logo", false)]
    [InlineData("https://user@icons.example/logo", false)]
    [InlineData("https://icons.example:8443/logo", false)]
    [InlineData("http://icons.example/logo", false)]
    [InlineData("file:///C:/logo", false)]
    [InlineData("data:image/svg+xml,<svg/>", false)]
    [InlineData("https://localhost/logo", false)]
    [InlineData("https://machine/logo", false)]
    [InlineData("https://machine.local/logo", false)]
    [InlineData("https://machine.internal./logo", false)]
    [InlineData("https://127.1/logo", false)]
    [InlineData("https://2130706433/logo", false)]
    [InlineData("https://[::1]/logo", false)]
    [InlineData("https://[::ffff:127.0.0.1]/logo", false)]
    [InlineData("https://icons.example/logo#fragment", false)]
    public void UriPolicy_IsHttpsPublicOnly(string value, bool expected) =>
        Assert.Equal(expected, ProviderArtworkNetworkPolicy.TryGetUri(value, out _));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("100::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:20::1")]
    [InlineData("2002:0808:0808::1")]
    [InlineData("3fff::1")]
    [InlineData("4000::1")]
    public void AddressPolicy_BlocksNonPublicAndSpecialUse(string value)
    {
        var ip = IPAddress.Parse(value);
        Assert.False(ProviderArtworkNetworkPolicy.IsPublicAddress(ip));
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            Assert.False(ProviderArtworkNetworkPolicy.IsPublicAddress(ip.MapToIPv6()));
    }

    [Fact]
    public async Task SocketConnection_UsesTheValidatedAddressWithoutSecondDnsLookup()
    {
        var resolves = 0;
        var connects = 0;
        using var stream = await ProviderArtworkNetworkPolicy.ConnectPublicAsync("icons.example", 443,
            (_, _) => { ++resolves; return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }); },
            (ip, port, _) =>
            {
                ++connects;
                Assert.Equal(IPAddress.Parse("8.8.8.8"), ip);
                Assert.Equal(443, port);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            }, default);
        Assert.Equal(1, resolves);
        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task SocketConnection_RejectsMixedDnsAnswersBeforeAnyConnect()
    {
        await Assert.ThrowsAsync<ProviderArtworkAddressException>(async () =>
            await ProviderArtworkNetworkPolicy.ConnectPublicAsync("icons.example", 443,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, _, _) => throw new InvalidOperationException("Must not connect."), default));
    }

    [Fact]
    public async Task SocketConnection_LiteralIpv6AndCancelledResolution()
    {
        using var stream = await ProviderArtworkNetworkPolicy.ConnectPublicAsync("2606:4700:4700::1111", 443,
            (_, _) => throw new InvalidOperationException("Must not resolve a literal."),
            (ip, _, _) =>
            {
                Assert.Equal(IPAddress.Parse("2606:4700:4700::1111"), ip);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            }, default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProviderArtworkNetworkPolicy.ConnectPublicAsync("icons.example", 443,
                (_, ct) => Task.FromCanceled<IPAddress[]>(ct),
                (_, _, _) => throw new InvalidOperationException("Must not connect."), cancelled.Token));
    }

    [Fact]
    public void Handler_DisablesCredentialRedirectProxyAndTelemetryPropagation()
    {
        using var handler = ProviderArtworkNetworkPolicy.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    public async Task Responses_NeverFollowOrAuthenticate(HttpStatusCode status)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://127.0.0.1/secret");
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
            return Task.FromResult(response);
        });
        using var loader = new ProviderArtworkLoader(handler);
        Assert.Equal(ProviderArtworkStatus.Http, (await loader.LoadAsync(IconUri(), default)).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidUri_NeverReachesHandler()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException());
        using var loader = new ProviderArtworkLoader(handler);
        Assert.Equal(ProviderArtworkStatus.Blocked, (await loader.LoadAsync(new Uri("http://127.0.0.1/logo"), default)).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_EnforcesAdvertisedAndStreamedByteLimits(bool advertise)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = ImageResponse(new byte[ProviderArtworkLoader.MaxBytes + 1]);
            if (!advertise)
                response.Content.Headers.ContentLength = 1;
            response.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(response);
        });
        using var loader = new ProviderArtworkLoader(handler);
        Assert.Equal(ProviderArtworkStatus.TooLarge, (await loader.LoadAsync(IconUri(), default)).Status);
        Assert.Equal((0, 0), loader.CacheSize);
    }

    [Fact]
    public async Task Download_CancellationAndDeadlineAreDistinct()
    {
        using var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ImageResponse(Png());
        });
        using var loader = new ProviderArtworkLoader(handler, TimeSpan.FromMilliseconds(30));
        Assert.Equal(ProviderArtworkStatus.TimedOut, (await loader.LoadAsync(IconUri(), default)).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(IconUri(), cancelled.Token));
    }

    [Fact]
    public async Task Cache_IsFiniteAndAvoidsRepeatDownload()
    {
        using var handler = new Handler((_, _) => Task.FromResult(ImageResponse(Png())));
        using var loader = new ProviderArtworkLoader(handler);
        await loader.LoadAsync(IconUri(), default);
        await loader.LoadAsync(IconUri(), default);
        Assert.Equal(1, handler.Calls);
        for (var i = 0; i < 30; ++i)
            await loader.LoadAsync(IconUri(i + 1), default);
        Assert.Equal(ProviderArtworkLoader.MaxCacheEntries, loader.CacheSize.Entries);
        Assert.InRange(loader.CacheSize.Bytes, 1, ProviderArtworkLoader.MaxCacheBytes);
        loader.Dispose();
        Assert.Equal((0, 0), loader.CacheSize);
    }

    [Fact]
    public async Task Cache_ByteBudgetEvictsBeforeCountBudget()
    {
        var bytes = new byte[ProviderArtworkLoader.MaxBytes];
        Png().CopyTo(bytes, 0);
        using var handler = new Handler((_, _) => Task.FromResult(ImageResponse(bytes)));
        using var loader = new ProviderArtworkLoader(handler);
        for (var i = 0; i < 12; ++i)
            await loader.LoadAsync(IconUri(i), default);
        Assert.Equal(8, loader.CacheSize.Entries);
        Assert.Equal(ProviderArtworkLoader.MaxCacheBytes, loader.CacheSize.Bytes);
    }

    [Theory]
    [InlineData("text/html", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/gif", false)]
    [InlineData("image/png", true)]
    public async Task UntrustedMimeAndCompression_AreRejected(string mime, bool compressed)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = ImageResponse(Png());
            response.Content.Headers.ContentType = new(mime);
            if (compressed)
                response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        using var loader = new ProviderArtworkLoader(handler);
        Assert.Equal(ProviderArtworkStatus.Unsupported, (await loader.LoadAsync(IconUri(), default)).Status);
    }

    [Fact]
    public async Task PageDisposal_CancelsActiveAndQueuedRequestsAndDropsCache()
    {
        using var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ImageResponse(Png());
        });
        using var loader = new ProviderArtworkLoader(handler);
        var pending = Enumerable.Range(0, 8).Select(i => loader.LoadAsync(IconUri(i), default)).ToArray();
        loader.Dispose();
        foreach (var task in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal((0, 0), loader.CacheSize);
    }

    [Fact]
    public async Task PageDisposal_DrainsAdmittedRequestsBeforeDisposingTransport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var cancellations = 0;
        using var handler = new Handler(async (_, ct) =>
        {
            using var registration = ct.Register(() => Interlocked.Increment(ref cancellations));
            if (Interlocked.Increment(ref active) == ProviderArtworkLoader.MaxConcurrentRequests)
                entered.SetResult();
            await release.Task;
            // Simulate a response already arriving while cancellation is draining.
            return ImageResponse(Png());
        });
        using var loader = new ProviderArtworkLoader(handler);
        var pending = Enumerable.Range(0, 8).Select(i => loader.LoadAsync(IconUri(i), default)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            loader.Dispose();
            loader.Dispose();
            Assert.Equal(ProviderArtworkLoader.MaxConcurrentRequests, cancellations);
            Assert.Equal(0, handler.Disposals);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => loader.LoadAsync(IconUri(9), default));
        }
        finally { release.TrySetResult(); }
        foreach (var task in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(ProviderArtworkLoader.MaxConcurrentRequests, handler.Calls);
        Assert.Equal(1, handler.Disposals);
        Assert.Equal((0, 0), loader.CacheSize);
    }

    [Fact]
    public async Task UnexpectedTransportDisposal_IsNotReportedAsCancellation()
    {
        using var handler = new Handler((_, _) => throw new ObjectDisposedException("external transport"));
        using var loader = new ProviderArtworkLoader(handler);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => loader.LoadAsync(IconUri(), default));
    }

    [Fact]
    public async Task NetworkFailures_OnlyReturnCoarseCategories()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("https://sensitive.example/logo?secret=123"));
        using var loader = new ProviderArtworkLoader(handler);
        var result = await loader.LoadAsync(IconUri(), default);
        Assert.Equal(ProviderArtworkStatus.Network, result.Status);
        Assert.DoesNotContain("sensitive", result.ToString());
        Assert.DoesNotContain("123", result.ToString());
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task RequestConcurrencyAndAdmission_AreBounded()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        using var handler = new Handler(async (_, ct) =>
        {
            var count = Interlocked.Increment(ref running);
            peak = Math.Max(peak, count);
            try { await release.Task.WaitAsync(ct); return ImageResponse(Png()); }
            finally { Interlocked.Decrement(ref running); }
        });
        using var loader = new ProviderArtworkLoader(handler);
        var requests = Enumerable.Range(0, 40).Select(i => loader.LoadAsync(IconUri(i), default)).ToArray();
        Assert.Equal(ProviderArtworkLoader.MaxConcurrentRequests, running);
        release.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Equal(40 - ProviderArtworkLoader.MaxPendingRequests, results.Count(r => r.Status == ProviderArtworkStatus.Busy));
        Assert.InRange(peak, 1, ProviderArtworkLoader.MaxConcurrentRequests);
    }

    [Fact]
    public void Generation_RejectsCompletionAfterRebindUnloadAndPageClose()
    {
        var lifetime = new ProviderArtworkGeneration();
        using var page = new CancellationTokenSource();
        var first = lifetime.Begin(page.Token);
        var second = lifetime.Begin(page.Token);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(lifetime.IsCurrent(first.Generation));
        Assert.False(lifetime.Matches(first.Generation));
        Assert.True(lifetime.IsCurrent(second.Generation));
        page.Cancel();
        Assert.False(lifetime.IsCurrent(second.Generation));
        lifetime.Stop();
        Assert.False(lifetime.Matches(second.Generation));
    }

    [Fact]
    public void Content_RequiresMatchingSignatureAndSupportedMime()
    {
        Assert.Equal(ProviderArtworkStatus.Loaded, ProviderArtworkContent.Validate(Png(), "image/png").Status);
        Assert.Equal(ProviderArtworkStatus.InvalidImage, ProviderArtworkContent.Validate(Png(), "image/jpeg").Status);
        Assert.Equal(ProviderArtworkStatus.InvalidImage, ProviderArtworkContent.Validate(Encoding.UTF8.GetBytes("<html/>"), "image/png").Status);
        Assert.False(ProviderArtworkContent.AreDimensionsAllowed(0, 1));
        Assert.False(ProviderArtworkContent.AreDimensionsAllowed(65535, 65535));
        Assert.False(ProviderArtworkContent.AreDimensionsAllowed(1025, 1));
        Assert.True(ProviderArtworkContent.AreDimensionsAllowed(1024, 1024));
    }

    [Fact]
    public void Svg_StaticSubsetIsReconstructedWithBoundedRenderExtent()
    {
        var result = Svg("<?xml version='1.0'?><!-- logo --><svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><g fill='#fff'><path d='M0 0 L12 12 Z'/></g></svg>");
        Assert.Equal(ProviderArtworkStatus.Loaded, result.Status);
        var root = XElement.Parse(Encoding.UTF8.GetString(result.Data!.Bytes));
        Assert.Equal("24", root.Attribute("width")!.Value);
        Assert.Equal("24", root.Attribute("height")!.Value);
        Assert.Equal("#FFFFFF", root.Attribute("fill")!.Value);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/secret'>]><svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>&x;</svg>")]
    [InlineData("<?xml-stylesheet href='https://icons.example/style.css'?><svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><foreignObject/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><image href='https://icons.example/private'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><use href='#a'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' style='fill:red'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='url(https://icons.example/private)'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' onload='run()'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 99999 24'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 NaN 24'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M 1e99 2'/></svg>")]
    public void Svg_ActiveExternalOrUnboundedContentNeverReachesNativeDecode(string svg) =>
        Assert.Null(Svg(svg).Data);

    [Fact]
    public void Svg_NodeDepthAndPathBudgetsAreEnforced()
    {
        const string root = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>";
        Assert.Null(Svg(root + string.Concat(Enumerable.Repeat("<g/>", 257)) + "</svg>").Data);
        Assert.Null(Svg(root + string.Concat(Enumerable.Repeat("<g>", 18)) +
            string.Concat(Enumerable.Repeat("</g>", 18)) + "</svg>").Data);
        Assert.Null(Svg(root + "<path d='M0 0 " + new string(' ', ProviderArtworkContent.MaxPathCharacters) + "'/></svg>").Data);
    }

    private static ProviderArtworkResult Svg(string text) => ProviderArtworkContent.Validate(Encoding.UTF8.GetBytes(text), "image/svg+xml");
    private static Uri IconUri(int id = 0) => new($"https://icons.example/logo-{id}");
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII=");
    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        internal int Disposals;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref Disposals);
            base.Dispose(disposing);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return send(request, ct);
        }
    }
}
