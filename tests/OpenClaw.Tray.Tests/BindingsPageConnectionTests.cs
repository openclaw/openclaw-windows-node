using OpenClawTray.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using System.Reflection;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class BindingsPageConnectionTests
{
    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Error)]
    public void RecoveryThenSameClientDrop_ClearsLoadingAndShowsDisconnected(ConnectionStatus droppedStatus)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);

            gateway.SetStatus(ConnectionStatus.Connected);
            Assert.Equal(0, gateway.ConfigRequests);
            page.DispatcherQueue.Drain();
            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);

            // No config response is delivered before the same client loses its socket.
            gateway.SetStatus(droppedStatus);
            page.DispatcherQueue.Drain();

            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_GatewayDisconnected.Title", page.ConnectionInfoBar.Title);
            Assert.False(page.RefreshButton.IsEnabled);
            Assert.Equal(1, gateway.ConfigRequests);
        }
        finally
        {
            page.Unload();
            Assert.Null(gateway.StatusChanged);
            Application.Current = null;
        }
    }

    [Fact]
    public void QueuedReconnectEvents_RequestConfigOnce()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();

            // Both events are queued before the UI thread runs, so each callback sees the reconnected client.
            gateway.SetStatus(ConnectionStatus.Connecting);
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();

            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);
        }
        finally
        {
            page.Unload();
            Application.Current = null;
        }
    }

    [Fact]
    public async Task ConfigGetRejected_ClearsTheSpinnerAndEnablesRefresh()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        gateway.FailConfigGet = true;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();
            await WaitForUi(page, () => page.LoadingState.Visibility == Visibility.Collapsed);

            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_CouldNotLoadBindings", page.ConnectionInfoBar.Title);
            Assert.Equal("BindingsPage_ConfigRequestFailed.Message", page.ConnectionInfoBar.Message);
            Assert.Equal(InfoBarSeverity.Error, page.ConnectionInfoBar.Severity);
            Assert.True(page.RefreshButton.IsEnabled);

            using var config = System.Text.Json.JsonDocument.Parse("""{"bindings":[]}""");
            page.UpdateConfig(config.RootElement);
            Assert.False(page.ConnectionInfoBar.IsOpen);
            Assert.True(page.RefreshButton.IsEnabled);
        }
        finally
        {
            page.Unload();
            Application.Current = null;
        }
    }

    [Fact]
    public async Task LateRejectedConfigGet_DoesNotFailTheRefreshThatReplacedIt()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        gateway.AutoTimeout = false;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();
            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);

            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();
            Assert.Equal(2, gateway.ConfigRequests);

            gateway.FailOldest();
            await WaitForUi(page, () => page.LoadingState.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);

            gateway.CompleteOldest(EmptyBindings());
            await WaitForUi(page, () => page.LoadingState.Visibility == Visibility.Collapsed);
            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);
            Assert.True(page.RefreshButton.IsEnabled);
        }
        finally
        {
            page.Unload();
            Application.Current = null;
        }
    }

    [Fact(Timeout = 5000)]
    public async Task ConfigGetSilence_ClearsTheSpinnerWhenTheDeadlineExpires()
    {
        var previous = BindingsConfigLoadDeadline.OverrideMilliseconds;
        BindingsConfigLoadDeadline.OverrideMilliseconds = 30;
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);

            await WaitForUi(page, () => page.LoadingState.Visibility == Visibility.Collapsed);

            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_ConfigRequestFailed.Message", page.ConnectionInfoBar.Message);
            Assert.True(page.RefreshButton.IsEnabled);
        }
        finally
        {
            page.Unload();
            BindingsConfigLoadDeadline.OverrideMilliseconds = previous;
            Application.Current = null;
        }
    }

    public class BindingsGatewayProxy : DispatchProxy
    {
        public bool Connected { get; private set; }
        public bool FailConfigGet { get; set; }
        public bool AutoTimeout { get; set; } = true;
        public int ConfigRequests { get; private set; }
        public EventHandler<ConnectionStatus>? StatusChanged { get; private set; }
        private readonly List<TaskCompletionSource<JsonElement>> _pending = new();

        public void SetStatus(ConnectionStatus status)
        {
            Connected = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }

        public void FailOldest() => SettleOldest(failed: true, default);

        public void CompleteOldest(JsonElement payload) => SettleOldest(failed: false, payload);

        private void SettleOldest(bool failed, JsonElement payload)
        {
            var pending = _pending[0];
            _pending.RemoveAt(0);
            if (failed)
                pending.TrySetException(new InvalidOperationException("config.get failed"));
            else
                pending.TrySetResult(payload);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsConnectedToGateway":
                    return Connected;
                case "add_StatusChanged":
                    StatusChanged += (EventHandler<ConnectionStatus>)args![0]!;
                    return null;
                case "remove_StatusChanged":
                    StatusChanged -= (EventHandler<ConnectionStatus>)args![0]!;
                    return null;
                case "SendWizardRequestAsync":
                    ConfigRequests++;
                    if (FailConfigGet)
                        return Task.FromException<JsonElement>(new InvalidOperationException("config.get failed"));
                    var timeout = args is { Length: >= 3 } && args[2] is int ms && ms > 0 ? ms : 15_000;
                    var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending.Add(pending);
                    if (AutoTimeout)
                        _ = FailAfter(pending, timeout);
                    return pending.Task;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }

        private static async Task FailAfter(TaskCompletionSource<JsonElement> pending, int timeout)
        {
            await Task.Delay(timeout).ConfigureAwait(false);
            pending.TrySetException(new TimeoutException());
        }
    }

    [Fact]
    public void ConfigLoadDeadline_UsesAPositiveOverrideOrTheDefault()
    {
        var previous = BindingsConfigLoadDeadline.OverrideMilliseconds;
        try
        {
            BindingsConfigLoadDeadline.OverrideMilliseconds = null;
            Assert.Equal(15_000, BindingsConfigLoadDeadline.CurrentMilliseconds);
            BindingsConfigLoadDeadline.OverrideMilliseconds = 0;
            Assert.Equal(15_000, BindingsConfigLoadDeadline.CurrentMilliseconds);
            BindingsConfigLoadDeadline.OverrideMilliseconds = 250;
            Assert.Equal(250, BindingsConfigLoadDeadline.CurrentMilliseconds);
        }
        finally
        {
            BindingsConfigLoadDeadline.OverrideMilliseconds = previous;
        }
    }

    private static async Task WaitForUi(BindingsPage page, Func<bool> done)
    {
        for (var attempt = 0; attempt < 100 && !done(); attempt++)
        {
            await Task.Delay(10);
            page.DispatcherQueue.Drain();
        }
    }

    private static JsonElement EmptyBindings()
    {
        using var document = JsonDocument.Parse("""{"bindings":[]}""");
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData(false, true, "Ignore")]
    [InlineData(false, false, "Ignore")]
    [InlineData(true, false, "ShowDisconnected")]
    [InlineData(true, true, "Recover")]
    public void DecideQueued_RechecksObservationAndConnection(
        bool observing,
        bool connected,
        string expected)
    {
        var decision = BindingsConnectionRecovery.DecideQueued(observing, connected);
        Assert.Equal(expected, decision.ToString());
    }

    [Fact]
    public void ConfigRequest_RequiresAConnectedGatewayClient()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "BindingsPage.xaml.cs"));

        AssertRequestRequiresConnection(MethodBody(source, "public void Initialize()"));
        AssertRequestRequiresConnection(MethodBody(source, "private void RefreshButton_Click"));
        var request = MethodBody(source, "private async Task LoadConfigAsync(");
        Assert.Contains("SendWizardRequestAsync(", request, StringComparison.Ordinal);
        Assert.Contains("BindingsConfigLoadDeadline.CurrentMilliseconds", request, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestConfigAsync()", request, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshButton.IsEnabled = CurrentApp.GatewayClient is { IsConnectedToGateway: true }",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertRequestRequiresConnection(string method)
    {
        var gate = method.IndexOf("IsConnectedToGateway: true", StringComparison.Ordinal);
        var request = method.IndexOf("RequestConfig(client)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "Expected the connected-gateway check.");
        Assert.True(request > gate, "RequestConfig must follow the connected-gateway check.");
        Assert.DoesNotContain("if (client != null)", method, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {signature}");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        Assert.True(next > start);
        return source[start..next];
    }
}
