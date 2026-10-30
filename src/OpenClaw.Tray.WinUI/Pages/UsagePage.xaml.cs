using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages.Usage;
using OpenClawTray.Presentation.Usage;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Pages;

/// <summary>
/// Thin XAML host for the Reactor Usage page. Owns the page lifetime: mounts <see cref="ReactorUsagePage"/>,
/// forwards connection changes to <see cref="UsagePageController"/>, and performs the file-save side of export.
/// </summary>
public sealed partial class UsagePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly UsageViewport _viewport = new();
    private UsagePageController? _controller;
    private ReactorHostControl? _host;
    private IOperatorGatewayClient? _trackedClient;
    private IGatewayConnectionManager? _connectionManager;

    public UsagePage()
    {
        InitializeComponent();
        SizeChanged += (_, args) => _viewport.Update(args.NewSize.Width);
        Unloaded += (_, _) => Teardown();
    }

    public void Initialize()
    {
        EnsureMounted();
        if (_connectionManager is null && CurrentApp.ConnectionManager is { } manager)
        {
            _connectionManager = manager;
            manager.OperatorClientChanged += OnOperatorClientChanged;
        }
        AttachClient(CurrentApp.GatewayClient);
        _controller!.Activate();
    }

    private void EnsureMounted()
    {
        if (_host is not null) return;
        var dispatcher = DispatcherQueue;
        _controller = new UsagePageController(
            () => CurrentApp.GatewayClient,
            () => DateOnly.FromDateTime(DateTime.Now),
            action => dispatcher.TryEnqueue(() => action()));
        var props = new UsagePageProps(_controller, _viewport, OpenConnection, ExportAsync);
        _host = new ReactorHostControl();
        _host.Mount(_ => Component<ReactorUsagePage, UsagePageProps>(props));
        UsageHost.Child = _host;
    }

    private void Teardown()
    {
        if (_connectionManager is not null)
        {
            _connectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            _connectionManager = null;
        }
        DetachClient();
        _controller?.Dispose();
        _controller = null;
        if (_host is not null)
        {
            _host.Dispose();
            if (ReferenceEquals(UsageHost.Child, _host)) UsageHost.Child = null;
            _host = null;
        }
    }

    private void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_trackedClient, client)) return;
        DetachClient();
        _trackedClient = client;
        if (client is not null) client.StatusChanged += OnClientStatusChanged;
    }

    private void DetachClient()
    {
        if (_trackedClient is null) return;
        _trackedClient.StatusChanged -= OnClientStatusChanged;
        _trackedClient = null;
    }

    // Reconnects while the page is open reload usage instead of leaving the disconnected banner up.
    private void OnClientStatusChanged(object? sender, ConnectionStatus status) =>
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _trackedClient)) _controller?.Activate();
        });

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e) =>
        DispatcherQueue?.TryEnqueue(() =>
        {
            // Teardown can run between the event and this callback; never re-subscribe a torn-down page.
            if (_controller is null) return;
            AttachClient(e.NewClient);
            _controller.Activate();
        });

    private void OpenConnection() => ((IAppCommands)CurrentApp).Navigate("connection");

    private async Task ExportAsync(UsageExportFormat format)
    {
        var controller = _controller;
        if (controller is null) return;
        controller.SetExportNotice(null);
        var (content, error) = await controller.ExportAsync(format);
        // The page closed, or the Gateway changed, while usage was read: open no dialog for stale content.
        if (!ReferenceEquals(_controller, controller) || (content is null && error is null)) return;
        if (content is null)
        {
            controller.SetExportNotice(new UsageExportNotice(null, error));
            return;
        }

        var window = CurrentApp.ActiveHubWindow;
        if (window is null)
        {
            controller.SetExportNotice(new UsageExportNotice(null, UsageExportError.Failed));
            return;
        }
        try
        {
            var name = UsageExport.FileName(format, DateOnly.FromDateTime(DateTime.Now));
            var extension = format == UsageExportFormat.Json ? ".json" : ".csv";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(name),
            };
            picker.FileTypeChoices.Add(format == UsageExportFormat.Json ? "JSON" : "CSV", new List<string> { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, content);
            _controller?.SetExportNotice(new UsageExportNotice(file.Name, null));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _controller?.SetExportNotice(new UsageExportNotice(null, UsageExportError.Failed));
        }
    }
}
