using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenClawTray.Services;

internal enum OpenTelemetryEndpointConnectionState
{
    Disabled,
    Connected,
    Failed
}

internal interface IOpenTelemetryProbeSink : IDisposable
{
    void SendProbe(OpenTelemetryEndpointOptions options);
    bool ForceFlush(int timeoutMilliseconds);
}

internal sealed class OpenTelemetryEndpointConnection : IDisposable
{
    internal const string ProbeActivityName = "openclaw.telemetry.probe";

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
    {
        if (_disposed)
            return Task.CompletedTask;

        var generation = Interlocked.Increment(ref _applyGeneration);
        return Task.Run(() => Apply(options, generation));
    }

    internal void Apply(OpenTelemetryEndpointOptions options)
    {
        Apply(options, generation: null);
    }

    private void Apply(OpenTelemetryEndpointOptions options, long? generation)
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

            if (_sink != null && State == OpenTelemetryEndpointConnectionState.Connected && options == _currentOptions)
                return;

            DisposeSink();
            IOpenTelemetryProbeSink? newSink = null;

            try
            {
                newSink = _sinkFactory(options);
                newSink.SendProbe(options);
                if (!newSink.ForceFlush(3_000))
                {
                    newSink.Dispose();
                    State = OpenTelemetryEndpointConnectionState.Failed;
                    LastError = "OpenTelemetry probe did not flush within 3000 ms.";
                    _currentOptions = options;
                    _logWarn($"OpenTelemetry endpoint probe failed: {LastError}");
                    return;
                }

                if (IsStale(generation))
                {
                    newSink.Dispose();
                    return;
                }

                _sink = newSink;
                newSink = null;
                _currentOptions = options;
                State = OpenTelemetryEndpointConnectionState.Connected;
                LastError = null;
                _logInfo($"OpenTelemetry endpoint probe sent using {OpenTelemetryEndpointProtocol.ToDisplayName(options.Protocol)}.");
            }
            catch (Exception ex)
            {
                newSink?.Dispose();
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
        sink?.Dispose();
    }

    private bool IsStale(long? generation) =>
        _disposed ||
        (generation.HasValue && generation.Value != Volatile.Read(ref _applyGeneration));
}

internal sealed class OpenTelemetryOtlpProbeSink : IOpenTelemetryProbeSink
{
    private const string ActivitySourceName = "OpenClaw.Companion.TelemetryProbe";
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);
    private readonly TracerProvider _provider;

    private OpenTelemetryOtlpProbeSink(TracerProvider provider)
    {
        _provider = provider;
    }

    public static IOpenTelemetryProbeSink Create(OpenTelemetryEndpointOptions options)
    {
        if (!options.TryGetEndpointUri(out var endpoint) || endpoint == null)
            throw new InvalidOperationException("OpenTelemetry endpoint must be configured before creating the exporter.");

        var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(
                    serviceName: "OpenClaw.Companion",
                    serviceVersion: typeof(OpenTelemetryOtlpProbeSink).Assembly.GetName().Version?.ToString())
                .AddAttributes(new Dictionary<string, object>
                {
                    ["process.pid"] = Environment.ProcessId
                }))
            .AddSource(ActivitySourceName)
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = endpoint;
                exporter.Protocol = options.Protocol == OpenTelemetryEndpointProtocol.HttpProtobuf
                    ? OtlpExportProtocol.HttpProtobuf
                    : OtlpExportProtocol.Grpc;
            })
            .Build();

        return new OpenTelemetryOtlpProbeSink(provider);
    }

    public void SendProbe(OpenTelemetryEndpointOptions options)
    {
        using var activity = s_activitySource.StartActivity(
            OpenTelemetryEndpointConnection.ProbeActivityName,
            ActivityKind.Internal);
        activity?.SetTag("openclaw.telemetry.protocol", OpenTelemetryEndpointProtocol.ToDisplayName(options.Protocol));
        activity?.SetTag("openclaw.telemetry.probe", true);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public bool ForceFlush(int timeoutMilliseconds) => _provider.ForceFlush(timeoutMilliseconds);

    public void Dispose()
    {
        _provider.ForceFlush(2_000);
        _provider.Dispose();
    }
}
