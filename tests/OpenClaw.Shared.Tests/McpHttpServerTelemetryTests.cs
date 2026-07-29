using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using OpenClaw.Shared.Mcp;
using OpenClaw.Shared.Telemetry;
using OpenClaw.Shared.Tests.Telemetry;

namespace OpenClaw.Shared.Tests;

[Collection(McpServerTelemetryCollection.Name)]
public sealed class McpHttpServerTelemetryTests
{
    [Fact]
    public async Task ConcurrentStartAndRepeatedStop_EmitLifecycleExactlyOnce()
    {
        using var metrics = new MetricCollector();
        var listener = new TestMcpHttpListener(blockStart: true);
        await using var server = CreateServer(listener);

        var firstStart = Task.Run(server.Start);
        await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStart = Task.Run(server.Start);
        listener.ReleaseStart();
        await Task.WhenAll(firstStart, secondStart);

        await server.StopAsync(TimeSpan.FromSeconds(1));
        await server.StopAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, listener.StartCount);
        Assert.Equal(1, listener.StopCount);
        await EventuallyAsync(() =>
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName).Count == 2);
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "start"),
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success")));
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "stop"),
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success")));
    }

    [Fact]
    public async Task StopWhileStartIsInProgress_WaitsForStartupAndStopsServer()
    {
        using var metrics = new MetricCollector();
        var listener = new TestMcpHttpListener(blockStart: true);
        await using var server = CreateServer(listener);
        var stopInvoked = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var start = Task.Run(server.Start);
        await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = Task.Run(async () =>
        {
            stopInvoked.TrySetResult(true);
            await server.StopAsync(TimeSpan.FromSeconds(1));
        });
        await stopInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(100)));

        listener.ReleaseStart();
        await Task.WhenAll(start, stop);

        Assert.Equal(1, listener.StartCount);
        Assert.Equal(1, listener.StopCount);
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "start"),
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success")));
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "stop"),
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success")));
    }

    [Fact]
    public async Task ListenerFailures_AreFiniteAndCloseDoesNotOverwriteStop()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var listener = new TestMcpHttpListener
        {
            AcceptErrorOnce = new HttpListenerException(995),
            StopError = new HttpListenerException(87),
            CloseError = new InvalidOperationException("close failed")
        };
        var server = CreateServer(listener);

        server.Start();
        await EventuallyAsync(() => HasListenerError(metrics, "listener_accept"));
        await server.DisposeAsync();

        await EventuallyAsync(() =>
            HasListenerError(metrics, "listener_stop") &&
            HasListenerError(metrics, "listener_close"));
        var stop = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.StopSpanName);
        Assert.Equal(
            "listener_stop",
            stop.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.DoesNotContain(
            activities.Stopped,
            activity =>
                activity.OperationName == McpServerTelemetry.StopSpanName &&
                Equals(
                    activity.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()),
                    "listener_close"));
    }

    [Fact]
    public async Task ListenerStartFailure_IsRecordedAndRethrown()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var expected = new HttpListenerException(5);
        var listener = new TestMcpHttpListener { StartError = expected };
        var server = CreateServer(listener);

        var thrown = Assert.Throws<HttpListenerException>(server.Start);

        Assert.Same(expected, thrown);
        Assert.True(HasListenerError(metrics, "listener_start"));
        var start = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.StartSpanName);
        Assert.Equal(ActivityStatusCode.Error, start.Status);
        Assert.Equal(
            "listener_start",
            start.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.Equal(
            typeof(HttpListenerException).FullName,
            start.GetTagItem(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()));
        await server.DisposeAsync();
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.StopSpanName);
        Assert.DoesNotContain(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "stop")));
    }

    [Fact]
    public async Task DisposeWithoutStart_DoesNotEmitStopLifecycle()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var server = CreateServer(new TestMcpHttpListener());

        await server.DisposeAsync();

        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.StopSpanName);
        Assert.DoesNotContain(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "stop")));
    }

    [Fact]
    public async Task ListenerStopFailure_TakesPrecedenceOverDrainTimeout()
    {
        using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = new GatedCapability(release);
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        var options = CreateOptions();
        var listener = new ThrowAfterStopListener(new SystemMcpHttpListener(port));
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options,
            listener);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var request = http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gate.wait"}}"""));
        await capability.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        await server.StopAsync(TimeSpan.Zero);
        release.TrySetResult(true);
        await IgnoreRequestFailureAsync(request);

        var stop = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.StopSpanName);
        Assert.Equal(
            "listener_stop",
            stop.GetTagItem(OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName()));
        Assert.True(HasListenerError(metrics, "listener_stop"));
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.LifecycleOperationsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (McpServerTelemetry.OperationTag, "stop"),
                (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "listener_stop")));
    }

    [Fact]
    public async Task AuthFailureAndMalformedJson_UseTransportOutcomes()
    {
        using var metrics = new MetricCollector();
        var port = FreePort();
        var authToken = Guid.Empty.ToString("N");
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        using var unauthorized = await http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        using var malformed = await http.PostAsync("", Json("{"));
        Assert.Equal(HttpStatusCode.OK, malformed.StatusCode);

        await EventuallyAsync(() =>
            FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName).Count >= 2);
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "failure"),
                (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "authentication_failed")));
        Assert.Single(
            FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName),
            measurement => HasTags(
                measurement.Tags,
                (OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), "success"),
                (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), "none")));
        await AssertRequestDurationsAsync(metrics, expectedCount: 2);
    }

    [Fact]
    public async Task ToolsCall_LinksNodeInvocationToIndependentMcpRequest()
    {
        using var activities = new ActivityCollector();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult(true);
        var capability = new GatedCapability(release);
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        using var response = await http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gate.wait"}}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await EventuallyAsync(() =>
            activities.Stopped.Any(activity =>
                activity.OperationName == McpServerTelemetry.RequestSpanName) &&
            activities.Stopped.Any(activity =>
                activity.OperationName == NodeToolInvocation.InvokeSpanName &&
                Equals(activity.GetTagItem(NodeToolInvocation.CommandTag), "gate.wait")));

        var request = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == McpServerTelemetry.RequestSpanName);
        var invocation = Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName == NodeToolInvocation.InvokeSpanName &&
                Equals(activity.GetTagItem(NodeToolInvocation.CommandTag), "gate.wait"));
        Assert.Equal(default, request.ParentSpanId);
        Assert.Equal(default, invocation.ParentSpanId);
        Assert.NotEqual(request.TraceId, invocation.TraceId);
        var link = Assert.Single(invocation.Links);
        Assert.Equal(request.Context, link.Context);
    }

    [Fact]
    public async Task HandlerSaturation_RecordsBusy()
    {
        using var metrics = new MetricCollector();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = new GatedCapability(release);
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        var options = CreateOptions(
            maxConcurrentHandlers: 1,
            handlerAdmissionTimeout: TimeSpan.FromMilliseconds(20));
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        var first = http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gate.wait"}}"""));
        await capability.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        using var busy = await http.GetAsync("");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        release.TrySetResult(true);
        using var firstResponse = await first;
        await EventuallyAsync(() => HasRequestCategory(metrics, "busy"));
        await AssertRequestDurationsAsync(metrics, expectedCount: 2);
    }

    [Fact]
    public async Task ShutdownWhileWaitingForHandler_RecordsShutdownNotBusyOrTimeout()
    {
        using var metrics = new MetricCollector();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = new GatedCapability(release);
        var secondAdmission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = 0;
        var port = FreePort();
        var bridge = new McpToolBridge(() => new INodeCapability[] { capability });
        var options = CreateOptions(
            maxConcurrentHandlers: 1,
            handlerAdmissionTimeout: TimeSpan.FromSeconds(10),
            handlerAdmissionStarted: () =>
            {
                if (Interlocked.Increment(ref admissions) == 2)
                    secondAdmission.TrySetResult(true);
            });
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        var first = http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gate.wait"}}"""));
        await capability.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        var waiting = http.GetAsync("");
        await secondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.StopAsync(TimeSpan.FromMilliseconds(20));
        release.TrySetResult(true);
        await IgnoreRequestFailureAsync(first);
        await IgnoreRequestFailureAsync(waiting);

        await EventuallyAsync(() => HasRequestCategory(metrics, "shutdown"));
        Assert.False(HasRequestCategory(metrics, "busy"));
        Assert.False(HasRequestCategory(metrics, "timeout"));
    }

    [Fact]
    public async Task AdmissionDisposedDuringShutdown_RecordsShutdownNotInternalFailure()
    {
        using var metrics = new MetricCollector();
        var admissionEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAdmission = new ManualResetEventSlim();
        var port = FreePort();
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        var options = CreateOptions(
            handlerAdmissionStarted: () =>
            {
                admissionEntered.TrySetResult(true);
                releaseAdmission.Wait();
            });
        var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        var request = http.GetAsync("");
        await admissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = server.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(3));
        releaseAdmission.Set();
        await IgnoreRequestFailureAsync(request);

        await EventuallyAsync(() => HasRequestCategory(metrics, "shutdown"));
        Assert.False(HasRequestCategory(metrics, "internal_failure"));
    }

    [Fact]
    public void CancellationCause_FallsBackOnlyWhenOneSourceCanceled()
    {
        using var timeout = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        var timeoutState = new McpHttpServer.McpRequestCancellationState();

        timeout.Cancel();

        Assert.Equal(
            McpHttpServer.McpRequestCancellationCause.Timeout,
            McpHttpServer.ResolveCancellationCause(
                timeoutState,
                timeout.Token,
                shutdown.Token));

        shutdown.Cancel();

        Assert.Equal(
            McpHttpServer.McpRequestCancellationCause.Timeout,
            McpHttpServer.ResolveCancellationCause(
                timeoutState,
                timeout.Token,
                shutdown.Token));

        using var shutdownOnly = new CancellationTokenSource();
        var shutdownState = new McpHttpServer.McpRequestCancellationState();
        shutdownOnly.Cancel();

        Assert.Equal(
            McpHttpServer.McpRequestCancellationCause.Shutdown,
            McpHttpServer.ResolveCancellationCause(
                shutdownState,
                CancellationToken.None,
                shutdownOnly.Token));

        var ambiguousState = new McpHttpServer.McpRequestCancellationState();

        Assert.Equal(
            McpHttpServer.McpRequestCancellationCause.None,
            McpHttpServer.ResolveCancellationCause(
                ambiguousState,
                timeout.Token,
                shutdown.Token));
    }

    [Fact]
    public void CancellationPropagation_RecordsFirstCauseBeforeCancelingRequest()
    {
        using var requestCancellation = new CancellationTokenSource();
        var state = new McpHttpServer.McpRequestCancellationState();
        var observedCause = McpHttpServer.McpRequestCancellationCause.None;
        using var registration = requestCancellation.Token.Register(
            () => observedCause = state.Cause);

        McpHttpServer.PropagateCancellation(
            state,
            requestCancellation,
            McpHttpServer.McpRequestCancellationCause.Timeout);
        state.TrySet(McpHttpServer.McpRequestCancellationCause.Shutdown);

        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.Equal(McpHttpServer.McpRequestCancellationCause.Timeout, observedCause);
        Assert.Equal(McpHttpServer.McpRequestCancellationCause.Timeout, state.Cause);
    }

    [Fact]
    public async Task DisposeAfterDrainTimeout_BeforeHandlerSetup_CompletesShutdownAndReleasesSlot()
    {
        using var metrics = new MetricCollector();
        using var executionRelease = new ManualResetEventSlim(false);
        var executionEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var port = FreePort();
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        var options = new McpHttpServerOptions(
            maxConcurrentHandlers: 1,
            handlerAdmissionTimeout: TimeSpan.FromMilliseconds(50),
            requestTimeout: TimeSpan.FromSeconds(5),
            drainTimeout: TimeSpan.Zero,
            handlerExecutionStarting: () =>
            {
                executionEntered.TrySetResult(true);
                executionRelease.Wait();
            });
        var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var request = http.GetAsync("");
        await executionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await server.DisposeAsync();
        executionRelease.Set();
        await IgnoreRequestFailureAsync(request);

        await EventuallyAsync(() => HasRequestCategory(metrics, "shutdown"));
        Assert.False(HasRequestCategory(metrics, "internal_failure"));
    }

    [Fact]
    public async Task SlowBodyDeadline_RecordsTimeout()
    {
        using var metrics = new MetricCollector();
        var port = FreePort();
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        var options = CreateOptions(
            requestTimeout: TimeSpan.FromMilliseconds(50),
            requestBodyReader: static async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "";
            });
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            options);
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        using var response = await http.PostAsync(
            "",
            Json("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        await EventuallyAsync(() => HasRequestCategory(metrics, "timeout"));
        await AssertRequestDurationsAsync(metrics, expectedCount: 1);
    }

    [Fact]
    public async Task ResponseWriteFailure_RecordsTransportFailure()
    {
        using var metrics = new MetricCollector();
        var port = FreePort();
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        await using var server = new McpHttpServer(
            bridge,
            port,
            NullLogger.Instance,
            authToken: null,
            static (response, _, _, _) =>
            {
                response.Close();
                throw new IOException("simulated response write failure");
            });
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        await IgnoreRequestFailureAsync(http.GetAsync(""));

        await EventuallyAsync(() => HasRequestCategory(metrics, "transport_failure"));
        await AssertRequestDurationsAsync(metrics, expectedCount: 1);
    }

    private static McpHttpServer CreateServer(TestMcpHttpListener listener)
    {
        var bridge = new McpToolBridge(() => Array.Empty<INodeCapability>());
        return new McpHttpServer(
            bridge,
            8765,
            NullLogger.Instance,
            authToken: null,
            static (_, _, _, _) => { },
            McpHttpServerOptions.Default,
            listener);
    }

    private static McpHttpServerOptions CreateOptions(
        int maxConcurrentHandlers = 16,
        TimeSpan? handlerAdmissionTimeout = null,
        TimeSpan? requestTimeout = null,
        Action? handlerAdmissionStarted = null,
        Func<HttpListenerRequest, long, CancellationToken, Task<string>>? requestBodyReader = null,
        Action? handlerExecutionStarting = null) =>
        new(
            maxConcurrentHandlers,
            handlerAdmissionTimeout ?? TimeSpan.FromMilliseconds(50),
            requestTimeout ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            handlerAdmissionStarted,
            requestBodyReader,
            handlerExecutionStarting);

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task IgnoreRequestFailureAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request;
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(
                deadline.Elapsed < TimeSpan.FromSeconds(2),
                "Timed out waiting for telemetry.");
            await Task.Delay(10);
        }
    }

    private static bool HasListenerError(MetricCollector metrics, string category) =>
        FindMeasurements(metrics, McpServerTelemetry.ListenerErrorsMetricName)
            .Any(measurement => HasTags(
                measurement.Tags,
                (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), category)));

    private static bool HasRequestCategory(MetricCollector metrics, string category) =>
        FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName)
            .Any(measurement => HasTags(
                measurement.Tags,
                (OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(), category)));

    private static List<MetricMeasurement<long>> FindMeasurements(
        MetricCollector metrics,
        string name) =>
        metrics.LongMeasurements.Where(item => item.Name == name).ToList();

    private static async Task AssertRequestDurationsAsync(
        MetricCollector metrics,
        int expectedCount)
    {
        await EventuallyAsync(() =>
            FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName).Count == expectedCount &&
            FindDurationMeasurements(metrics).Count == expectedCount);

        var requests = FindMeasurements(metrics, McpServerTelemetry.RequestsMetricName);
        var durations = FindDurationMeasurements(metrics);
        Assert.All(durations, measurement => Assert.True(measurement.Value >= 0));
        Assert.Equal(
            requests.Select(measurement => TagSignature(measurement.Tags)).Order(),
            durations.Select(measurement => TagSignature(measurement.Tags)).Order());
    }

    private static List<MetricMeasurement<double>> FindDurationMeasurements(
        MetricCollector metrics) =>
        metrics.DoubleMeasurements
            .Where(item => item.Name == McpServerTelemetry.RequestDurationMetricName)
            .ToList();

    private static string TagSignature(
        IReadOnlyCollection<KeyValuePair<string, object?>> tags) =>
        string.Join(
            "|",
            tags.OrderBy(tag => tag.Key)
                .Select(tag => $"{tag.Key}={tag.Value}"));

    private static bool HasTags(
        IReadOnlyCollection<KeyValuePair<string, object?>> actual,
        params (string Key, object? Value)[] expected) =>
        expected.All(pair =>
            actual.Any(tag => tag.Key == pair.Key && Equals(tag.Value, pair.Value)));

    private sealed class GatedCapability : INodeCapability
    {
        private readonly TaskCompletionSource<bool> _release;
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedCapability(TaskCompletionSource<bool> release)
        {
            _release = release;
        }

        public string Category => "gate";
        public IReadOnlyList<string> Commands => ["gate.wait"];
        public Task Entered => _entered.Task;
        public bool CanHandle(string command) => command == "gate.wait";

        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        {
            _entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            return new NodeInvokeResponse { Ok = true };
        }
    }

    private sealed class TestMcpHttpListener : IMcpHttpListener
    {
        private readonly TaskCompletionSource<HttpListenerContext> _pendingAccept =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _startRelease;
        private int _acceptCount;

        public TestMcpHttpListener(bool blockStart = false)
        {
            _startRelease = new ManualResetEventSlim(!blockStart);
        }

        public bool IsListening { get; private set; }
        public Exception? StartError { get; init; }
        public Exception? AcceptErrorOnce { get; init; }
        public Exception? StopError { get; init; }
        public Exception? CloseError { get; init; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public TaskCompletionSource<bool> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            StartCount++;
            StartEntered.TrySetResult(true);
            _startRelease.Wait();
            if (StartError != null)
                throw StartError;
            IsListening = true;
        }

        public Task<HttpListenerContext> GetContextAsync()
        {
            if (Interlocked.Increment(ref _acceptCount) == 1 && AcceptErrorOnce != null)
                return Task.FromException<HttpListenerContext>(AcceptErrorOnce);
            return _pendingAccept.Task;
        }

        public void Stop()
        {
            StopCount++;
            IsListening = false;
            _pendingAccept.TrySetException(new HttpListenerException(995));
            if (StopError != null)
                throw StopError;
        }

        public void Close()
        {
            IsListening = false;
            _pendingAccept.TrySetException(new ObjectDisposedException(nameof(TestMcpHttpListener)));
            _startRelease.Dispose();
            if (CloseError != null)
                throw CloseError;
        }

        public void ReleaseStart() => _startRelease.Set();
    }

    private sealed class ThrowAfterStopListener : IMcpHttpListener
    {
        private readonly IMcpHttpListener _inner;

        public ThrowAfterStopListener(IMcpHttpListener inner)
        {
            _inner = inner;
        }

        public bool IsListening => _inner.IsListening;
        public void Start() => _inner.Start();
        public Task<HttpListenerContext> GetContextAsync() => _inner.GetContextAsync();

        public void Stop()
        {
            _inner.Stop();
            throw new HttpListenerException(87);
        }

        public void Close() => _inner.Close();
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source =>
                    source.Name == OpenClawActivitySourceName.OpenClaw.ToTelemetryName(),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Name.StartsWith("openclaw.mcp.server.", StringComparison.Ordinal) ||
                    options.Name == NodeToolInvocation.InvokeSpanName
                        ? ActivitySamplingResult.AllDataAndRecorded
                        : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName.StartsWith(
                        "openclaw.mcp.server.",
                        StringComparison.Ordinal) ||
                        activity.OperationName == NodeToolInvocation.InvokeSpanName)
                    {
                        Stopped.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenClawMeterName.OpenClaw.ToTelemetryName() &&
                    instrument.Name.StartsWith("openclaw.mcp.server.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                LongMeasurements.Add(new MetricMeasurement<long>(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                DoubleMeasurements.Add(new MetricMeasurement<double>(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
            _listener.Start();
        }

        public ConcurrentBag<MetricMeasurement<long>> LongMeasurements { get; } = new();
        public ConcurrentBag<MetricMeasurement<double>> DoubleMeasurements { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record MetricMeasurement<T>(
        string Name,
        T Value,
        KeyValuePair<string, object?>[] Tags);
}
