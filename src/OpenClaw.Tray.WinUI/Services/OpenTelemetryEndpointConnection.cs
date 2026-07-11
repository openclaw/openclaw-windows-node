using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenClaw.Shared.Telemetry;

namespace OpenClawTray.Services;

internal enum OpenTelemetryEndpointConnectionState
{
    Disabled,
    ProbeFlushed,
    Failed
}

internal interface IOpenTelemetryProbeSink : IDisposable
{
    void SendProbe(OpenTelemetryEndpointOptions options);
    bool ForceFlush(int timeoutMilliseconds);
}

internal sealed class OpenTelemetryEndpointConnection : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<OpenTelemetryEndpointOptions, IOpenTelemetryProbeSink> _sinkFactory;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private IOpenTelemetryProbeSink? _sink;
    private OpenTelemetryEndpointOptions _currentOptions = OpenTelemetryEndpointOptions.Disabled;
    private long _applyGeneration;
    private volatile bool _disposed;

    public OpenTelemetryEndpointConnection()
        : this(OpenTelemetryOtlpProbeSink.Create, Logger.Info, Logger.Warn)
    {
    }

    internal OpenTelemetryEndpointConnection(
        Func<OpenTelemetryEndpointOptions, IOpenTelemetryProbeSink> sinkFactory,
        Action<string> logInfo,
        Action<string> logWarn)
    {
        _sinkFactory = sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
        _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        _logWarn = logWarn ?? throw new ArgumentNullException(nameof(logWarn));
    }

    public OpenTelemetryEndpointConnectionState State { get; private set; } =
        OpenTelemetryEndpointConnectionState.Disabled;

    public string? LastError { get; private set; }

    public OpenTelemetryEndpointOptions CurrentOptions => _currentOptions;

    public void Apply(SettingsManager? settings) =>
        Apply(OpenTelemetryEndpointOptions.FromSettings(settings));

    public Task ApplyAsync(OpenTelemetryEndpointOptions options)
        => ApplyAsync(options, forceProbe: false);

    public Task ProbeAsync(OpenTelemetryEndpointOptions options)
        => ApplyAsync(options, forceProbe: true);

    private Task ApplyAsync(OpenTelemetryEndpointOptions options, bool forceProbe)
    {
        if (_disposed)
            return Task.CompletedTask;

        var generation = Interlocked.Increment(ref _applyGeneration);
        return Task.Run(() => Apply(options, generation, forceProbe));
    }

    internal void Apply(OpenTelemetryEndpointOptions options)
    {
        Apply(options, generation: null, forceProbe: false);
    }

    private void Apply(OpenTelemetryEndpointOptions options, long? generation, bool forceProbe)
    {
        lock (_gate)
        {
            if (IsStale(generation))
                return;

            if (!options.IsEnabled)
            {
                Disable();
                return;
            }

            if (!options.TryGetEndpointUri(out _))
            {
                Disable();
                State = OpenTelemetryEndpointConnectionState.Failed;
                LastError = "OpenTelemetry endpoint must be an absolute HTTP or HTTPS URL.";
                _logWarn($"OpenTelemetry endpoint configuration rejected: {LastError}");
                return;
            }

            if (!forceProbe &&
                _sink != null &&
                State == OpenTelemetryEndpointConnectionState.ProbeFlushed &&
                options == _currentOptions)
                return;

            DisposeSink();
            IOpenTelemetryProbeSink? newSink = null;

            try
            {
                newSink = _sinkFactory(options);
                newSink.SendProbe(options);
                if (!newSink.ForceFlush(OpenTelemetryOtlpProbeSink.ProbeFlushTimeoutMilliseconds))
                {
                    DisposeProbeSink(newSink, "discarding an unflushed OpenTelemetry probe sink");
                    State = OpenTelemetryEndpointConnectionState.Failed;
                    LastError = $"OpenTelemetry probe did not flush within {OpenTelemetryOtlpProbeSink.ProbeFlushTimeoutMilliseconds} ms.";
                    _currentOptions = options;
                    _logWarn($"OpenTelemetry endpoint probe failed: {LastError}");
                    return;
                }

                if (IsStale(generation))
                {
                    DisposeProbeSink(newSink, "discarding a stale OpenTelemetry probe sink");
                    return;
                }

                // ForceFlush confirms the SDK processed queued batches; OTLP does not provide
                // a collector acknowledgment round-trip for this probe.
                _sink = newSink;
                newSink = null;
                _currentOptions = options;
                State = OpenTelemetryEndpointConnectionState.ProbeFlushed;
                LastError = null;
                _logInfo($"OpenTelemetry endpoint probe sent using {OpenTelemetryEndpointProtocol.ToDisplayName(options.Protocol)}.");
            }
            catch (Exception ex)
            {
                DisposeProbeSink(newSink, "discarding a failed OpenTelemetry probe sink");
                State = OpenTelemetryEndpointConnectionState.Failed;
                LastError = ex.Message;
                _currentOptions = options;
                _logWarn($"OpenTelemetry endpoint probe failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _applyGeneration);
        lock (_gate)
        {
            DisposeSink();
            State = OpenTelemetryEndpointConnectionState.Disabled;
            LastError = null;
            _currentOptions = OpenTelemetryEndpointOptions.Disabled;
        }
    }

    private void Disable()
    {
        DisposeSink();
        State = OpenTelemetryEndpointConnectionState.Disabled;
        LastError = null;
        _currentOptions = OpenTelemetryEndpointOptions.Disabled;
    }

    private void DisposeSink()
    {
        var sink = _sink;
        _sink = null;
        DisposeProbeSink(sink, "disposing the current OpenTelemetry probe sink");
    }

    private void DisposeProbeSink(IOpenTelemetryProbeSink? sink, string context)
    {
        if (sink == null)
            return;

        try
        {
            sink.Dispose();
        }
        catch (Exception ex)
        {
            _logWarn($"OpenTelemetry endpoint sink disposal failed while {context}: {ex.Message}");
        }
    }

    private bool IsStale(long? generation) =>
        _disposed ||
        (generation.HasValue && generation.Value != Volatile.Read(ref _applyGeneration));
}

internal sealed class OpenTelemetryOtlpProbeSink : IOpenTelemetryProbeSink
{
    internal const int ProbeFlushTimeoutMilliseconds = 3_000;
    private const int DisposeFlushTimeoutMilliseconds = 500;
    private const string HttpVersionPath = "v1";
    private const string TraceHttpPath = "v1/traces";
    private const string MetricHttpPath = "v1/metrics";
    private const string LogHttpPath = "v1/logs";
    private const string ExporterTagKey = "openclaw.exporter";
    private const string ExporterProtocolTagKey = "openclaw.exporter.protocol";
    private const string SignalTagKey = "openclaw.signal";
    private static readonly EventId ExporterProbeLogEvent = new(1000, "OpenTelemetryExporterProbeSent");
    private static readonly Counter<long> ExporterProbeCounter = OpenClawTelemetry.CreateCounter(
        "openclaw.telemetry.exporter.probes",
        unit: "{probe}",
        description: "Number of OpenClaw telemetry exporter probe metrics sent.");

    internal enum OpenTelemetryOtlpSignal
    {
        Traces,
        Metrics,
        Logs
    }

    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly OpenTelemetryLoggerPipeline _loggerPipeline;
    private readonly ILogger _probeLogger;

    private OpenTelemetryOtlpProbeSink(
        TracerProvider tracerProvider,
        MeterProvider meterProvider,
        OpenTelemetryLoggerPipeline loggerPipeline)
    {
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
        _loggerPipeline = loggerPipeline;
        _probeLogger = loggerPipeline.CreateLogger(OpenTelemetryLogPolicy.TelemetryExporterCategory);
    }

    public static IOpenTelemetryProbeSink Create(OpenTelemetryEndpointOptions options)
    {
        if (!options.TryGetEndpointUri(out var endpoint) || endpoint == null)
            throw new InvalidOperationException("OpenTelemetry endpoint must be configured before creating the exporter.");

        TracerProvider? tracerProvider = null;
        MeterProvider? meterProvider = null;
        OpenTelemetryLoggerPipeline? loggerPipeline = null;

        try
        {
            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(CreateResourceBuilder())
                .AddSource(OpenClawActivitySourceName.OpenClaw.ToTelemetryName())
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, options.Protocol, OpenTelemetryOtlpSignal.Traces))
                .Build();

            meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(CreateResourceBuilder())
                .AddMeter(OpenClawMeterName.OpenClaw.ToTelemetryName())
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, options.Protocol, OpenTelemetryOtlpSignal.Metrics))
                .Build();

            loggerPipeline = CreateLoggerPipeline(endpoint, options.Protocol);

            var sink = new OpenTelemetryOtlpProbeSink(tracerProvider, meterProvider, loggerPipeline);
            tracerProvider = null;
            meterProvider = null;
            loggerPipeline = null;
            return sink;
        }
        finally
        {
            tracerProvider?.Dispose();
            meterProvider?.Dispose();
            loggerPipeline?.Dispose();
        }
    }

    public void SendProbe(OpenTelemetryEndpointOptions options)
    {
        OpenClawTelemetry.Trace(
            "openclaw.telemetry.exporter.probe",
            static () => { },
            CreateProbeTags(options, "traces"));

        OpenClawTelemetry.Add(
            ExporterProbeCounter,
            tags: CreateProbeTags(options, "metrics"));

        _probeLogger.Log(
            LogLevel.Information,
            ExporterProbeLogEvent,
            CreateProbeLogAttributes(options),
            null,
            static (_, _) => "OpenClaw telemetry exporter probe log sent.");
    }

    public bool ForceFlush(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
        {
            var tracesOk = _tracerProvider.ForceFlush(timeoutMilliseconds);
            var metricsOk = _meterProvider.ForceFlush(timeoutMilliseconds);
            var logsOk = _loggerPipeline.ForceFlush(timeoutMilliseconds);
            return tracesOk && metricsOk && logsOk;
        }

        var stopwatch = Stopwatch.StartNew();
        var tracesFlushed = _tracerProvider.ForceFlush(timeoutMilliseconds);
        var remainingMilliseconds = Math.Max(0, timeoutMilliseconds - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
        var metricsFlushed = _meterProvider.ForceFlush(remainingMilliseconds);
        remainingMilliseconds = Math.Max(0, timeoutMilliseconds - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
        var logsFlushed = _loggerPipeline.ForceFlush(remainingMilliseconds);
        return tracesFlushed && metricsFlushed && logsFlushed;
    }

    public void Dispose()
    {
        List<Exception>? errors = null;
        TryDisposeStep(() => _tracerProvider.ForceFlush(DisposeFlushTimeoutMilliseconds), ref errors);
        TryDisposeStep(() => _meterProvider.ForceFlush(DisposeFlushTimeoutMilliseconds), ref errors);
        TryDisposeStep(() => _loggerPipeline.ForceFlush(DisposeFlushTimeoutMilliseconds), ref errors);
        TryDisposeStep(_tracerProvider.Dispose, ref errors);
        TryDisposeStep(_meterProvider.Dispose, ref errors);
        TryDisposeStep(_loggerPipeline.Dispose, ref errors);

        if (errors != null)
            throw new AggregateException("OpenTelemetry endpoint sink disposal failed.", errors);
    }

    private static ResourceBuilder CreateResourceBuilder() =>
        ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: OpenClawResourceName.WindowsTray.ToServiceName(),
                serviceVersion: typeof(OpenTelemetryOtlpProbeSink).Assembly.GetName().Version?.ToString())
            .AddAttributes(new Dictionary<string, object>
            {
                ["process.pid"] = Environment.ProcessId
            });

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        Uri endpoint,
        string? protocol,
        OpenTelemetryOtlpSignal signal)
    {
        var normalizedProtocol = OpenTelemetryEndpointProtocol.Normalize(protocol);
        exporter.Endpoint = ResolveExporterEndpoint(endpoint, normalizedProtocol, signal);
        exporter.Protocol = normalizedProtocol == OpenTelemetryEndpointProtocol.HttpProtobuf
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    // Explicitly resolve full signal endpoints for HTTP/protobuf so traces,
    // metrics, and logs do not compete for one collector URL.
    internal static Uri ResolveExporterEndpoint(
        Uri endpoint,
        string? protocol,
        OpenTelemetryOtlpSignal signal) =>
        OpenTelemetryEndpointProtocol.Normalize(protocol) == OpenTelemetryEndpointProtocol.HttpProtobuf
            ? AppendHttpSignalPath(endpoint, signal)
            : endpoint;

    private static Uri AppendHttpSignalPath(Uri endpoint, OpenTelemetryOtlpSignal signal)
    {
        var prefix = TrimKnownHttpSignalSuffix(endpoint.AbsolutePath.Trim('/'));
        var signalPath = signal switch
        {
            OpenTelemetryOtlpSignal.Traces => TraceHttpPath,
            OpenTelemetryOtlpSignal.Metrics => MetricHttpPath,
            OpenTelemetryOtlpSignal.Logs => LogHttpPath,
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null)
        };
        var path = string.IsNullOrEmpty(prefix)
            ? signalPath
            : $"{prefix}/{signalPath}";

        return new UriBuilder(endpoint)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }

    private static string TrimKnownHttpSignalSuffix(string path)
    {
        foreach (var suffix in new[] { TraceHttpPath, MetricHttpPath, LogHttpPath })
        {
            if (string.Equals(path, suffix, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (path.EndsWith($"/{suffix}", StringComparison.OrdinalIgnoreCase))
                return path[..^(suffix.Length + 1)];
        }

        if (string.Equals(path, HttpVersionPath, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (path.EndsWith($"/{HttpVersionPath}", StringComparison.OrdinalIgnoreCase))
            return path[..^(HttpVersionPath.Length + 1)];

        return path;
    }

    private static OpenTelemetryLoggerPipeline CreateLoggerPipeline(Uri endpoint, string? protocol)
    {
        OpenTelemetrySdk? sdk = null;
        try
        {
            sdk = OpenTelemetrySdk.Create(builder => builder.WithLogging(
                logging => logging
                    .SetResourceBuilder(CreateResourceBuilder())
                    .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, protocol, OpenTelemetryOtlpSignal.Logs)),
                options =>
                {
                    options.IncludeScopes = false;
                    options.IncludeFormattedMessage = false;
                    options.ParseStateValues = true;
                }));
            var pipeline = new OpenTelemetryLoggerPipeline(sdk, sdk.GetLoggerFactory());
            sdk = null;
            return pipeline;
        }
        finally
        {
            sdk?.Dispose();
        }
    }

    private static OpenClawTelemetryTag[] CreateProbeTags(OpenTelemetryEndpointOptions options, string signal) =>
    [
        OpenClawTelemetryTag.String(
            ExporterTagKey,
            "tray-otel"),
        OpenClawTelemetryTag.String(
            SignalTagKey,
            signal),
        OpenClawTelemetryTag.String(
            ExporterProtocolTagKey,
            OpenTelemetryEndpointProtocol.ToTelemetryValue(options.Protocol))
    ];

    private static KeyValuePair<string, object?>[] CreateProbeLogAttributes(OpenTelemetryEndpointOptions options) =>
    [
        new(ExporterTagKey, "tray-otel"),
        new(SignalTagKey, "logs"),
        new(ExporterProtocolTagKey, OpenTelemetryEndpointProtocol.ToTelemetryValue(options.Protocol))
    ];

    private static void TryDisposeStep(Action step, ref List<Exception>? errors)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            errors ??= [];
            errors.Add(ex);
        }
    }

    private sealed class OpenTelemetryLoggerPipeline : IDisposable
    {
        private readonly OpenTelemetrySdk _sdk;
        private readonly ILoggerFactory _loggerFactory;

        public OpenTelemetryLoggerPipeline(
            OpenTelemetrySdk sdk,
            ILoggerFactory loggerFactory)
        {
            _sdk = sdk;
            _loggerFactory = loggerFactory;
        }

        public ILogger CreateLogger(string categoryName) =>
            new PolicyLogger(categoryName, _loggerFactory.CreateLogger(categoryName));

        public bool ForceFlush(int timeoutMilliseconds) =>
            _sdk.LoggerProvider.ForceFlush(timeoutMilliseconds);

        public void Dispose() =>
            _sdk.Dispose();
    }

    private sealed class PolicyLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ILogger _inner;

        public PolicyLogger(string categoryName, ILogger inner)
        {
            _categoryName = categoryName;
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            OpenTelemetryLogPolicy.ShouldExport(_categoryName, logLevel) && _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!OpenTelemetryLogPolicy.ShouldExport(_categoryName, logLevel))
                return;

            if (!_inner.IsEnabled(logLevel))
                return;

            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
