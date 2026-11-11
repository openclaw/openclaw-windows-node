using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages.Usage;
using OpenClawTray.Presentation;
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
/// forwards connection changes to <see cref="UsagePageController"/>, performs the file-save side of export, and
/// routes the native chat and web Usage handoffs through the app's existing navigation owners.
/// </summary>
public sealed partial class UsagePage : Page, ISettingsPageContentInset
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly UsageViewport _viewport = new();
    private UsagePageController? _controller;
    private UsageExportWorkflow? _exportWorkflow;
    private ReactorHostControl? _host;
    private IOperatorGatewayClient? _trackedClient;
    private IGatewayConnectionManager? _connectionManager;

    public UsagePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Teardown();
    }

    void ISettingsPageContentInset.ApplyTopInset(double topInset) => _viewport.UpdateTopInset(topInset);

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
            action => dispatcher.TryEnqueue(() => action()),
            providers: CurrentApp.UsageProviderReads);
        var (openWebUsage, openConversation) = UsageHandoffs.For(
            (IAppCommands)CurrentApp, sessionKey => CurrentApp.PendingChatSessionKey = sessionKey);
        var controller = _controller;
        _exportWorkflow = new UsageExportWorkflow(controller, SaveCsvAsync,
            () => ReferenceEquals(_controller, controller), Logger.Warn);
        var props = new UsagePageProps(_controller, _viewport, OpenConnection, openWebUsage, openConversation, _exportWorkflow.ExportAsync,
            () => ((IAppCommands)CurrentApp).Navigate(UsageHandoffs.ChatPageTag), OpenExportFolderAsync);
        _host = new ReactorHostControl();
        _host.SizeChanged += (_, args) => _viewport.Update(args.NewSize.Width);
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
        _exportWorkflow = null;
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

    private async Task<UsageSavedExport?> SaveCsvAsync(string name, string content, Func<bool> isCurrent)
    {
        var window = CurrentApp.ActiveHubWindow;
        if (window is null) throw new InvalidOperationException("The Usage save picker requires an active window.");
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(name),
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return await UsageExportWorkflow.SaveAfterSelectionAsync<StorageFile>(
            async () => await picker.PickSaveFileAsync(),
            async file =>
            {
                await FileIO.WriteTextAsync(file, content);
                return new(file.Name, System.IO.Path.GetDirectoryName(file.Path));
            }, isCurrent);
    }

    private async Task OpenExportFolderAsync(string path)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (!await global::Windows.System.Launcher.LaunchFolderAsync(folder))
                throw new InvalidOperationException("Windows did not open the saved export folder.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Logger.Warn($"[UsageExport] Open folder failed: {ex.GetType().Name}");
            _controller?.SetExportNotice(new UsageExportNotice(null, UsageExportError.OpenFolder));
        }
    }
}
