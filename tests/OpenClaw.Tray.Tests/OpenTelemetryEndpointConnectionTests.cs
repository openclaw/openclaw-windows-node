using Microsoft.Extensions.Logging;
using OpenClawTray.Services;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Tray.Tests;

public sealed class OpenTelemetryEndpointConnectionTests
{
    [Fact]
    public void Apply_DoesNotCreateSink_WhenEndpointIsEmpty()
    {
        var created = 0;
        using var connection = new OpenTelemetryEndpointConnection(
            _ =>
            {
                created++;
                return new FakeProbeSink();
            },
            _ => { },
            _ => { });

        connection.Apply(OpenTelemetryEndpointOptions.Create(null, OpenTelemetryEndpointProtocol.HttpProtobuf));

        Assert.Equal(0, created);
        Assert.Equal(OpenTelemetryEndpointConnectionState.Disabled, connection.State);
        Assert.False(connection.CurrentOptions.IsEnabled);
    }

    [Fact]
    public void Probe_UsesGatewayAlignedTelemetryConstants()
    {
        Assert.Equal("openclaw", OpenClawActivitySourceName.OpenClaw.ToTelemetryName());
        Assert.Equal("openclaw", OpenClawMeterName.OpenClaw.ToTelemetryName());
        Assert.Equal("openclaw-windows-tray", OpenClawResourceName.WindowsTray.ToServiceName());
        Assert.Equal("OpenClaw.Telemetry.Exporter", OpenTelemetryLogPolicy.TelemetryExporterCategory);
        Assert.Equal("grpc", OpenTelemetryEndpointProtocol.ToTelemetryValue(OpenTelemetryEndpointProtocol.Grpc));
        Assert.Equal("http/protobuf", OpenTelemetryEndpointProtocol.ToTelemetryValue(OpenTelemetryEndpointProtocol.HttpProtobuf));
    }

    [Theory]
    [InlineData("OpenClaw.Telemetry.Exporter", LogLevel.Information, true)]
    [InlineData("OpenClaw.Telemetry.Connection", LogLevel.Warning, true)]
    [InlineData("OpenClaw.Telemetry.Exporter", LogLevel.Debug, false)]
    [InlineData("OpenClaw.Telemetry.Exporter", LogLevel.None, false)]
    [InlineData("OpenClawTray.Services.GatewayService", LogLevel.Warning, false)]
    [InlineData(null, LogLevel.Warning, false)]
    public void OpenTelemetryLogPolicy_AllowsOnlySafeTelemetryCategories(
        string? category,
        LogLevel level,
        bool expected)
    {
        Assert.Equal(expected, OpenTelemetryLogPolicy.ShouldExport(category, level));
    }

    [Fact]
    public void ProviderRuntime_IsAppLevel_NotDebugPageOwned()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var debugPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs"));

        Assert.Contains("_openTelemetryConnection = new OpenTelemetryEndpointConnection();", app);
        Assert.Contains("ApplyOpenTelemetryEndpointSettings();", app);
        Assert.Contains("OnSettingsSaved", app);
        Assert.DoesNotContain("new OpenTelemetryEndpointConnection", debugPage);
    }

    [Fact]
    public void FromSettings_DoesNotUsePlaceholderAsDefaultEndpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            var settings = new SettingsManager(dir);

            var options = OpenTelemetryEndpointOptions.FromSettings(settings);

            Assert.False(options.IsEnabled);
            Assert.Null(options.Endpoint);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Apply_SendsOneProbeAndFlushes_ForConfiguredEndpoint()
    {
        var sink = new FakeProbeSink();
        using var connection = new OpenTelemetryEndpointConnection(
            _ => sink,
            _ => { },
            _ => { });

        var options = OpenTelemetryEndpointOptions.Create(
            " http://localhost:4318 ",
            OpenTelemetryEndpointProtocol.HttpProtobuf);

        connection.Apply(options);

        Assert.Equal(OpenTelemetryEndpointConnectionState.Connected, connection.State);
        Assert.Equal("http://localhost:4318", connection.CurrentOptions.Endpoint);
        Assert.Equal(OpenTelemetryEndpointProtocol.HttpProtobuf, connection.CurrentOptions.Protocol);
        Assert.Equal(1, sink.SendProbeCount);
        Assert.Equal(1, sink.ForceFlushCount);
        Assert.Equal(options, sink.LastProbeOptions);
    }

    [Fact]
    public void Apply_FailedFlush_DoesNotReportConnected()
    {
        var sink = new FakeProbeSink { ForceFlushResult = false };
        using var connection = new OpenTelemetryEndpointConnection(
            _ => sink,
            _ => { },
            _ => { });

        var options = OpenTelemetryEndpointOptions.Create(
            "http://localhost:4318",
            OpenTelemetryEndpointProtocol.HttpProtobuf);

        connection.Apply(options);

        Assert.Equal(OpenTelemetryEndpointConnectionState.Failed, connection.State);
        Assert.Contains("did not flush", connection.LastError);
        Assert.True(sink.Disposed);
        Assert.Equal(options, connection.CurrentOptions);
    }

    [Fact]
    public void Apply_SameOptions_DoNotSendDuplicateProbe()
    {
        var sink = new FakeProbeSink();
        using var connection = new OpenTelemetryEndpointConnection(
            _ => sink,
            _ => { },
            _ => { });
        var options = OpenTelemetryEndpointOptions.Create(
            "http://localhost:4317",
            OpenTelemetryEndpointProtocol.Grpc);

        connection.Apply(options);
        connection.Apply(options);

        Assert.Equal(1, sink.SendProbeCount);
        Assert.False(sink.Disposed);
    }

    [Fact]
    public void Apply_NewOptions_DisposesOldSinkAndSendsNewProbe()
    {
        var sinks = new List<FakeProbeSink>();
        using var connection = new OpenTelemetryEndpointConnection(
            _ =>
            {
                var sink = new FakeProbeSink();
                sinks.Add(sink);
                return sink;
            },
            _ => { },
            _ => { });

        connection.Apply(OpenTelemetryEndpointOptions.Create("http://localhost:4317", OpenTelemetryEndpointProtocol.Grpc));
        connection.Apply(OpenTelemetryEndpointOptions.Create("http://localhost:4318", OpenTelemetryEndpointProtocol.HttpProtobuf));

        Assert.Equal(2, sinks.Count);
        Assert.True(sinks[0].Disposed);
        Assert.False(sinks[1].Disposed);
        Assert.Equal(1, sinks[0].SendProbeCount);
        Assert.Equal(1, sinks[1].SendProbeCount);
        Assert.Equal(OpenTelemetryEndpointProtocol.HttpProtobuf, connection.CurrentOptions.Protocol);
    }

    [Fact]
    public void Apply_AfterDispose_DoesNotCreateSink()
    {
        var created = 0;
        var connection = new OpenTelemetryEndpointConnection(
            _ =>
            {
                created++;
                return new FakeProbeSink();
            },
            _ => { },
            _ => { });

        connection.Dispose();
        connection.Apply(OpenTelemetryEndpointOptions.Create("http://localhost:4317", OpenTelemetryEndpointProtocol.Grpc));

        Assert.Equal(0, created);
        Assert.Equal(OpenTelemetryEndpointConnectionState.Disabled, connection.State);
    }

    [Fact]
    public async Task ApplyAsync_StaleApply_DoesNotWinOverLatestSettings()
    {
        var firstFlushStarted = new ManualResetEventSlim();
        var releaseFirstFlush = new ManualResetEventSlim();
        var sinks = new List<FakeProbeSink>();
        using var connection = new OpenTelemetryEndpointConnection(
            _ =>
            {
                var sink = new FakeProbeSink();
                if (sinks.Count == 0)
                {
                    sink.OnForceFlush = () =>
                    {
                        firstFlushStarted.Set();
                        Assert.True(releaseFirstFlush.Wait(TimeSpan.FromSeconds(5)));
                    };
                }

                sinks.Add(sink);
                return sink;
            },
            _ => { },
            _ => { });
        var stale = OpenTelemetryEndpointOptions.Create("http://localhost:4317", OpenTelemetryEndpointProtocol.Grpc);
        var latest = OpenTelemetryEndpointOptions.Create("http://localhost:4318", OpenTelemetryEndpointProtocol.HttpProtobuf);

        var staleTask = connection.ApplyAsync(stale);
        Assert.True(firstFlushStarted.Wait(TimeSpan.FromSeconds(5)));
        var latestTask = connection.ApplyAsync(latest);
        releaseFirstFlush.Set();
        await Task.WhenAll(staleTask, latestTask);

        Assert.Equal(2, sinks.Count);
        Assert.True(sinks[0].Disposed);
        Assert.False(sinks[1].Disposed);
        Assert.Equal(OpenTelemetryEndpointConnectionState.Connected, connection.State);
        Assert.Equal(latest, connection.CurrentOptions);
    }

    [Fact]
    public void FromSettings_CarriesOnlyEndpointAndProtocol()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            var settings = new SettingsManager(dir)
            {
                GatewayUrl = "wss://gateway.example.test",
                OpenTelemetryEndpoint = "http://collector.example.test:4317",
                OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Grpc,
                TtsElevenLabsApiKey = "secret-key"
            };

            var options = OpenTelemetryEndpointOptions.FromSettings(settings);

            Assert.Equal("http://collector.example.test:4317", options.Endpoint);
            Assert.Equal(OpenTelemetryEndpointProtocol.Grpc, options.Protocol);
            var serialized = options.ToString().ToLowerInvariant();
            Assert.DoesNotContain("gateway", serialized);
            Assert.DoesNotContain("secret", serialized);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeProbeSink : IOpenTelemetryProbeSink
    {
        public int SendProbeCount { get; private set; }
        public int ForceFlushCount { get; private set; }
        public bool ForceFlushResult { get; init; } = true;
        public Action? OnForceFlush { get; set; }
        public bool Disposed { get; private set; }
        public OpenTelemetryEndpointOptions? LastProbeOptions { get; private set; }

        public void SendProbe(OpenTelemetryEndpointOptions options)
        {
            SendProbeCount++;
            LastProbeOptions = options;
        }

        public bool ForceFlush(int timeoutMilliseconds)
        {
            ForceFlushCount++;
            OnForceFlush?.Invoke();
            return ForceFlushResult;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
