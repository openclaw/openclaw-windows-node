using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiPrefixCacheProxyTests : IDisposable
{
    private const string Profile = "profile";
    private const string Model = "managed-model";
    private const string SystemText = "You are OpenClaw. Follow the rules.";

    private readonly TempDirectory _store = new("prefix-proxy-");
    private readonly FakeUpstream _upstream = new();
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private LocalAiPrefixCacheProxy? _proxy;

    public void Dispose()
    {
        _proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
        _upstream.Dispose();
        _store.Dispose();
    }

    private LocalAiPrefixCacheProxy StartProxy(
        int slots = 1,
        int minimumPrefixCharacters = 10,
        TimeSpan? minimumSaveInterval = null,
        bool restoreNewestOnStart = false,
        Func<string?>? apiKey = null)
    {
        _proxy = new LocalAiPrefixCacheProxy(
            new LocalAiPrefixCacheProxyOptions
            {
                Upstream = _upstream.Base,
                StoreDirectory = _store.Path,
                ProfileFingerprint = Profile,
                Slots = slots,
                MinimumPrefixCharacters = minimumPrefixCharacters,
                MinimumSaveInterval = minimumSaveInterval ?? TimeSpan.Zero,
                ModelName = Model,
                RestoreNewestOnStart = restoreNewestOnStart,
                GetApiKey = apiKey,
            },
            new NullLogger());
        _proxy.Start();
        _upstream.OnSave = seen =>
        {
            string name = JsonNode.Parse(seen.Body)!["filename"]!.GetValue<string>();
            File.WriteAllText(Path.Combine(_store.Path, name), "kv");
        };
        return _proxy;
    }

    private static JsonArray SystemPrefix(string text = SystemText) =>
        new(new JsonObject { ["role"] = "system", ["content"] = text });

    private static string ChatBody(string user, string systemText = SystemText, bool stream = false, bool includeSystem = true)
    {
        var messages = new JsonArray();
        if (includeSystem)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
        return new JsonObject
        {
            ["model"] = Model,
            ["stream"] = stream,
            ["max_tokens"] = 256,
            ["messages"] = messages,
        }.ToJsonString();
    }

    private string KeyFor(string systemText = SystemText) =>
        LocalAiPrefixCacheKey.Compute(Profile, Model, SystemPrefix(systemText), null);

    private Task<HttpResponseMessage> PostChat(string body, string? bearer = null, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_proxy!.Endpoint.AbsoluteUri + "/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
            request.Headers.Authorization = new("Bearer", bearer);
        return _client.SendAsync(request, option);
    }

    private static string[] Paths(IEnumerable<Seen> seen) => seen.Select(s => s.Path).ToArray();

    [Fact]
    public async Task NonChatRequests_PassThroughUnchanged()
    {
        StartProxy();
        _upstream.Override = async (seen, context) =>
        {
            if (seen.Path != "/models?autoload=false")
                return false;
            context.Response.StatusCode = 207;
            context.Response.Headers["X-Test"] = "upstream";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[]}"));
            return true;
        };
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_proxy!.Endpoint, "/models?autoload=false"));
        request.Headers.Authorization = new("Bearer", "client-key");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(207, (int)response.StatusCode);
        Assert.Equal("upstream", response.Headers.GetValues("X-Test").Single());
        Assert.Equal("{\"data\":[]}", await response.Content.ReadAsStringAsync());
        Seen forwarded = Assert.Single(_upstream.Seen);
        Assert.Equal("Bearer client-key", forwarded.Auth);
    }

    [Fact]
    public async Task StreamingResponses_ReachTheClientBeforeTheUpstreamFinishes()
    {
        StartProxy();
        var release = new TaskCompletionSource();
        _upstream.Override = async (seen, context) =>
        {
            if (!seen.Path.StartsWith("/v1/chat/completions", StringComparison.Ordinal))
                return false;
            context.Response.ContentType = "text/event-stream";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: first\n\n"));
            await context.Response.OutputStream.FlushAsync();
            await release.Task;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            return true;
        };

        using HttpResponseMessage response = await PostChat(
            ChatBody("hi", stream: true, includeSystem: false), option: HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("data: first", first);
        Assert.False(release.Task.IsCompleted);
        release.SetResult();
        Assert.Contains("[DONE]", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ChatWithoutASystemPrefix_IsForwardedWithoutAnySlotCalls()
    {
        StartProxy();
        string body = ChatBody("hi", includeSystem: false);

        using HttpResponseMessage response = await PostChat(body);

        Assert.True(response.IsSuccessStatusCode);
        Seen only = Assert.Single(_upstream.Seen);
        Assert.Equal("/v1/chat/completions", only.Path);
        Assert.Equal(body, only.Body);
    }

    [Fact]
    public async Task ChatWithAPrefixBelowTheMinimum_IsForwardedWithoutAnySlotCalls()
    {
        StartProxy(minimumPrefixCharacters: 1000);
        // (minimum is measured on the serialized prefix, so the default 35-character prompt is below it)
        string body = ChatBody("hi");

        using HttpResponseMessage response = await PostChat(body);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task UnknownPrefix_IsWarmedAloneAndSavedBeforeTheRealRequestIsForwardedUnchanged()
    {
        StartProxy();
        string body = ChatBody("what is the product?");

        using HttpResponseMessage response = await PostChat(body);

        Assert.True(response.IsSuccessStatusCode);
        IReadOnlyList<Seen> seen = _upstream.Seen;
        Assert.Equal(["/v1/chat/completions", "/slots/0?action=save", "/v1/chat/completions"], Paths(seen));
        JsonObject warm = JsonNode.Parse(seen[0].Body)!.AsObject();
        Assert.Equal(Model, warm["model"]!.GetValue<string>());
        Assert.False(warm["stream"]!.GetValue<bool>());
        Assert.Equal(1, warm["max_tokens"]!.GetValue<int>());
        Assert.Equal(0, warm["id_slot"]!.GetValue<int>());
        Assert.True(warm["cache_prompt"]!.GetValue<bool>());
        Assert.Equal(SystemPrefix().ToJsonString(), warm["messages"]!.ToJsonString());
        JsonObject save = JsonNode.Parse(seen[1].Body)!.AsObject();
        Assert.Equal(Model, save["model"]!.GetValue<string>());
        Assert.Equal(LocalAiPrefixCacheStore.FileNameFor(KeyFor()), save["filename"]!.GetValue<string>());
        Assert.Equal(body, seen[2].Body);
    }

    [Fact]
    public async Task SavingPrunesToTheFourMostRecentPrefixes()
    {
        StartProxy();
        for (int i = 1; i <= 4; i++)
        {
            string old = Path.Combine(_store.Path, $"openclaw-prefix-{i:x40}.bin");
            File.WriteAllText(old, "kv");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        using HttpResponseMessage response = await PostChat(ChatBody("hi"));

        Assert.True(response.IsSuccessStatusCode);
        string[] files = Directory.GetFiles(_store.Path).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(4, files.Length);
        Assert.Contains(LocalAiPrefixCacheStore.FileNameFor(KeyFor()), files);
        Assert.DoesNotContain($"openclaw-prefix-{1:x40}.bin", files);
    }

    [Fact]
    public async Task SavedPrefix_IsRestoredIntoEverySlotBeforeTheRealRequest()
    {
        StartProxy(slots: 2);
        string file = LocalAiPrefixCacheStore.FileNameFor(KeyFor());
        string path = Path.Combine(_store.Path, file);
        File.WriteAllText(path, "kv");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string body = ChatBody("hi");

        using HttpResponseMessage response = await PostChat(body);

        Assert.True(response.IsSuccessStatusCode);
        IReadOnlyList<Seen> seen = _upstream.Seen;
        Assert.Equal(["/slots/0?action=restore", "/slots/1?action=restore", "/v1/chat/completions"], Paths(seen));
        JsonObject restore = JsonNode.Parse(seen[0].Body)!.AsObject();
        Assert.Equal(Model, restore["model"]!.GetValue<string>());
        Assert.Equal(file, restore["filename"]!.GetValue<string>());
        Assert.Equal(body, seen[2].Body);
        Assert.True(File.GetLastWriteTimeUtc(path) > new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ResidentPrefix_IsNotPreparedAgain()
    {
        StartProxy();
        using (await PostChat(ChatBody("first")))
        {
        }
        int afterFirst = _upstream.Seen.Count;

        using HttpResponseMessage second = await PostChat(ChatBody("second"));

        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(afterFirst + 1, _upstream.Seen.Count);
        Assert.Equal("/v1/chat/completions", _upstream.Seen[^1].Path);
    }

    [Fact]
    public async Task PrefixCacheFailure_NeverBlocksTheRealRequest()
    {
        StartProxy();
        _upstream.Override = async (seen, context) =>
        {
            if (!seen.Path.StartsWith("/slots/", StringComparison.Ordinal))
                return false;
            context.Response.StatusCode = 500;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"boom\"}"));
            return true;
        };
        string body = ChatBody("hi");

        using HttpResponseMessage response = await PostChat(body);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(body, _upstream.Seen[^1].Body);
    }

    [Fact]
    public async Task RequestInFlight_SkipsPreparationSoALiveConversationIsNeverOverwritten()
    {
        StartProxy();
        var release = new TaskCompletionSource();
        _upstream.Override = async (seen, context) =>
        {
            if (seen.Path != "/v1/chat/completions" || !seen.Body.Contains("\"stream\":true", StringComparison.Ordinal))
                return false;
            context.Response.ContentType = "text/event-stream";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: first\n\n"));
            await context.Response.OutputStream.FlushAsync();
            await release.Task;
            return true;
        };
        using HttpResponseMessage first = await PostChat(
            ChatBody("slow", stream: true), option: HttpCompletionOption.ResponseHeadersRead);
        int beforeSecond = _upstream.Seen.Count;

        using HttpResponseMessage second = await PostChat(ChatBody("other", systemText: "A different system prompt."));

        Assert.True(second.IsSuccessStatusCode);
        IReadOnlyList<Seen> added = _upstream.Seen.Skip(beforeSecond).ToArray();
        Assert.Equal(["/v1/chat/completions"], Paths(added));
        release.SetResult();
        await first.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task SlotCalls_CarryTheClientCredentialAndNeverASubstitutedOne()
    {
        StartProxy();

        using (await PostChat(ChatBody("hi"), bearer: "client-key"))
        {
        }
        Assert.All(_upstream.Seen, seen => Assert.Equal("Bearer client-key", seen.Auth));

        _upstream.Clear();
        using (await PostChat(ChatBody("hi", systemText: "Another system prompt entirely.")))
        {
        }
        Assert.All(_upstream.Seen, seen => Assert.Null(seen.Auth));
    }

    [Fact]
    public async Task NonChatPost_InvalidatesResidencySoTheNextChatRestoresAgain()
    {
        StartProxy();
        using (await PostChat(ChatBody("first")))
        {
        }
        using (await _client.PostAsync(
            new Uri(_proxy!.Endpoint.AbsoluteUri + "/embeddings"),
            new StringContent("{}", Encoding.UTF8, "application/json")))
        {
        }
        _upstream.Clear();

        using (await PostChat(ChatBody("second")))
        {
        }

        Assert.Equal(["/slots/0?action=restore", "/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task NonChatPost_KeepsResidencyWhenTheModelHasMoreThanOneSlot()
    {
        StartProxy(slots: 2);
        using (await PostChat(ChatBody("first")))
        {
        }
        using (await _client.PostAsync(
            new Uri(_proxy!.Endpoint.AbsoluteUri + "/embeddings"),
            new StringContent("{}", Encoding.UTF8, "application/json")))
        {
        }
        _upstream.Clear();

        using (await PostChat(ChatBody("second")))
        {
        }

        // llama-server places a request by prefix similarity, so another slot may have served the POST.
        Assert.Equal(["/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task FailedWarm_IsNotRetriedOnEveryTurn()
    {
        StartProxy();
        _upstream.Override = async (seen, context) =>
        {
            if (seen.Path != "/v1/chat/completions" || !seen.Body.Contains("\"id_slot\":0", StringComparison.Ordinal))
                return false;
            context.Response.StatusCode = 400;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"no user query\"}"));
            return true;
        };
        using (await PostChat(ChatBody("first")))
        {
        }
        _upstream.Clear();

        using HttpResponseMessage second = await PostChat(ChatBody("second"));

        Assert.True(second.IsSuccessStatusCode);
        Assert.Equal(["/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task NewPrefixWithinTheSaveInterval_IsForwardedWithoutWarmingOrSaving()
    {
        StartProxy(minimumSaveInterval: TimeSpan.FromHours(1));
        using (await PostChat(ChatBody("first")))
        {
        }
        _upstream.Clear();

        using HttpResponseMessage response = await PostChat(ChatBody("second", systemText: "A prompt that changes every turn."));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task WarmRequest_DropsGenerationLimitsAndKeepsHowToolsAreRendered()
    {
        StartProxy();
        JsonObject request = JsonNode.Parse(ChatBody("hi"))!.AsObject();
        request["n_predict"] = 500;
        request["n"] = 2;
        request["logprobs"] = true;
        request["tool_choice"] = "none";
        request["tools"] = new JsonArray(new JsonObject { ["type"] = "function" });

        using (await PostChat(request.ToJsonString()))
        {
        }

        JsonObject warm = JsonNode.Parse(_upstream.Seen[0].Body)!.AsObject();
        Assert.Null(warm["n_predict"]);
        Assert.Null(warm["n"]);
        Assert.Null(warm["logprobs"]);
        Assert.Equal("none", warm["tool_choice"]!.GetValue<string>());
        Assert.NotNull(warm["tools"]);
        // A forced tool call would constrain the one warm token with a grammar, so it is dropped.
        request["tool_choice"] = "required";
        request["messages"]![0]!["content"] = "A different system prompt, so the prefix is new again.";
        _upstream.Clear();
        using (await PostChat(request.ToJsonString()))
        {
        }
        Assert.Null(JsonNode.Parse(_upstream.Seen[0].Body)!["tool_choice"]);
    }

    private string WriteSavedPrefix(string key, int day)
    {
        string name = LocalAiPrefixCacheStore.FileNameFor(key);
        string path = Path.Combine(_store.Path, name);
        File.WriteAllText(path, "kv");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));
        return name;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "condition was not met within 5 s");
    }

    private static string KeyHex(int index) => $"{index:x40}";

    [Fact]
    public async Task StartupRestore_RestoresTheNewestSavedPrefixIntoEverySlotBeforeAnyRequest()
    {
        WriteSavedPrefix(KeyHex(1), 1);
        string newest = WriteSavedPrefix(KeyHex(2), 2);

        StartProxy(slots: 2, restoreNewestOnStart: true, apiKey: () => "startup-key");

        await WaitUntilAsync(() => _upstream.Seen.Count >= 2);
        IReadOnlyList<Seen> seen = _upstream.Seen;
        Assert.Equal(["/slots/0?action=restore", "/slots/1?action=restore"], Paths(seen));
        JsonObject body = JsonNode.Parse(seen[0].Body)!.AsObject();
        Assert.Equal(Model, body["model"]!.GetValue<string>());
        Assert.Equal(newest, body["filename"]!.GetValue<string>());
        Assert.All(seen, s => Assert.Equal("Bearer startup-key", s.Auth));
    }

    [Fact]
    public async Task StartupRestore_LetsAMatchingFirstChatSkipPreparation()
    {
        WriteSavedPrefix(KeyFor(), 1);
        StartProxy(restoreNewestOnStart: true);
        await WaitUntilAsync(() => _upstream.Seen.Count >= 1);
        _upstream.Clear();

        using HttpResponseMessage response = await PostChat(ChatBody("hi"));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task StartupRestore_FallsBackToNormalPreparationWhenTheFirstChatHasAnotherPrefix()
    {
        WriteSavedPrefix(KeyHex(7), 1);
        StartProxy(restoreNewestOnStart: true);
        await WaitUntilAsync(() => _upstream.Seen.Count >= 1);
        _upstream.Clear();

        using HttpResponseMessage response = await PostChat(ChatBody("hi"));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["/v1/chat/completions", "/slots/0?action=save", "/v1/chat/completions"], Paths(_upstream.Seen));
    }

    [Fact]
    public async Task StartupRestore_DoesNothingWhenNothingWasSaved()
    {
        StartProxy(restoreNewestOnStart: true);

        await Task.Delay(400);

        Assert.Empty(_upstream.Seen);
    }

    [Fact]
    public async Task StartupRestore_FailureNeverBlocksChat()
    {
        WriteSavedPrefix(KeyFor(), 1);
        _upstream.Override = async (seen, context) =>
        {
            if (!seen.Path.StartsWith("/slots/", StringComparison.Ordinal) || !seen.Path.EndsWith("action=restore", StringComparison.Ordinal))
                return false;
            context.Response.StatusCode = 500;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"boom\"}"));
            return true;
        };
        StartProxy(restoreNewestOnStart: true);
        await WaitUntilAsync(() => _upstream.Seen.Count >= 1);

        using HttpResponseMessage response = await PostChat(ChatBody("hi"));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("/v1/chat/completions", _upstream.Seen[^1].Path);
    }

    [Fact]
    public async Task StartupRestore_RequestsWaitForItInsteadOfRacingTheSlot()
    {
        WriteSavedPrefix(KeyFor(), 1);
        var release = new TaskCompletionSource();
        _upstream.Override = async (seen, context) =>
        {
            if (!seen.Path.EndsWith("action=restore", StringComparison.Ordinal))
                return false;
            await release.Task;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"n_restored\":1}"));
            return true;
        };
        StartProxy(restoreNewestOnStart: true);
        await WaitUntilAsync(() => _upstream.Seen.Count >= 1);

        Task<HttpResponseMessage> chat = PostChat(ChatBody("hi"));
        await Task.Delay(300);
        Assert.Single(_upstream.Seen);
        release.SetResult();
        using HttpResponseMessage response = await chat;

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(["/slots/0?action=restore", "/v1/chat/completions"], Paths(_upstream.Seen));
    }

    private sealed record Seen(string Method, string Path, string? Auth, string Body);

    private sealed class FakeUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<Seen> _seen = [];

        public FakeUpstream()
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = Random.Shared.Next(20000, 40000);
                try
                {
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Base = new Uri($"http://127.0.0.1:{port}/");
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    _listener.Prefixes.Clear();
                }
            }
            _ = Task.Run(LoopAsync);
        }

        public Uri Base { get; }
        public Func<Seen, HttpListenerContext, Task<bool>>? Override { get; set; }
        public Action<Seen>? OnSave { get; set; }

        public IReadOnlyList<Seen> Seen
        {
            get
            {
                lock (_seen)
                    return [.. _seen];
            }
        }

        public void Clear()
        {
            lock (_seen)
                _seen.Clear();
        }

        public void Dispose() => _listener.Close();

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var seen = new Seen(
                    context.Request.HttpMethod,
                    context.Request.Url!.PathAndQuery,
                    context.Request.Headers["Authorization"],
                    body);
                lock (_seen)
                    _seen.Add(seen);
                bool handled = Override is not null && await Override(seen, context);
                if (!handled)
                {
                    string json = "{\"choices\":[]}";
                    if (seen.Path.StartsWith("/slots/", StringComparison.Ordinal))
                    {
                        json = "{\"id_slot\":0,\"n_saved\":10,\"n_written\":99,\"n_restored\":10}";
                        if (seen.Path.EndsWith("action=save", StringComparison.Ordinal))
                            OnSave?.Invoke(seen);
                    }
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
                }
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // The client may already be gone.
                }
            }
        }
    }
}
