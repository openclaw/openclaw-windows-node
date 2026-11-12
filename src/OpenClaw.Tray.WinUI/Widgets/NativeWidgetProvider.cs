using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Widgets;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Widgets;

/// <summary>Windows owns activation. This process reads display data, never Gateway credentials or source websites.</summary>
internal sealed class NativeWidgetProvider : IWidgetProvider, IWidgetProvider2, IDisposable
{
#if DEV_BUILD
    internal const string ClassId = "0D582DA5-334F-4FCD-9524-80FE1D138C86";
#else
    internal const string ClassId = "45A6BDF0-B332-4861-AB0E-2A524D421EA1";
#endif
    private static readonly object UpdateGate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private Timer? _timer;
    private readonly AppLogger _logger = new();

    internal void Start()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_WIDGET_POLL_SECONDS"), out var configured)
            && configured is >= 5 and <= 300 ? configured : 15;
        _timer = new Timer(_ => Guard(() => UpdateActive()), null, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    internal static WidgetHostStatus Publish(bool update)
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        if (result == 15700) // APPMODEL_ERROR_NO_PACKAGE
            return new(false, 0, "The unpackaged app can cache snapshots only. Windows widgets require the packaged Companion.");
        if (result != 122) // ERROR_INSUFFICIENT_BUFFER indicates a package identity.
            throw new System.ComponentModel.Win32Exception(result);
        lock (UpdateGate)
        {
            var widgets = GetWidgets()
                .Where(w => w.WidgetContext.DefinitionId == WidgetCard.DefinitionId).ToArray();
            if (update)
            {
                var snapshots = Store().List();
                foreach (var widget in widgets)
                    Render(widget.WidgetContext.Id, widget.CustomState, snapshots);
            }
            return new(true, widgets.Length);
        }
    }

    // The zero-length query does not dereference a string buffer.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

    public void CreateWidget(WidgetContext context) => Guard(() =>
    {
        lock (UpdateGate) { Render(context.Id, null, Store().List(), saveBinding: true); }
    });
    public void DeleteWidget(string widgetId, string customState)
    {
        lock (UpdateGate) { _active.Remove(widgetId); }
    }
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) => Guard(() => Update(args.WidgetContext.Id));
    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs args) => Guard(() => Update(args.WidgetContext.Id));
    public void Activate(WidgetContext context) => Guard(() =>
    {
        lock (UpdateGate)
        {
            _active.Add(context.Id);
            Update(context.Id);
        }
    });
    public void Deactivate(string widgetId)
    {
        lock (UpdateGate) { _active.Remove(widgetId); }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args) => Guard(() =>
    {
        using var data = JsonDocument.Parse(string.IsNullOrWhiteSpace(args.Data) ? "{}" : args.Data);
        lock (UpdateGate)
        {
            var snapshots = Store().List();
            var widget = GetWidgets().Single(w => w.WidgetContext.Id == args.WidgetContext.Id);
            if (widget.WidgetContext.DefinitionId != WidgetCard.DefinitionId)
                throw new ArgumentException("Unknown widget definition.");
            var state = WidgetInstanceState.Parse(widget.CustomState, snapshots);
            if (args.Verb == WidgetCard.SelectVerb)
            {
                var selection = data.RootElement.TryGetProperty("openclaw-tracker", out var currentSelection)
                    ? currentSelection : data.RootElement.GetProperty("trackerId");
                var id = selection.GetString()
                    ?? throw new ArgumentException("A tracker selection is required.");
                WidgetSnapshotStore.ValidateId(id);
                var selected = snapshots.SingleOrDefault(s => s.Id == id)
                    ?? throw new ArgumentException("The selected tracker no longer exists.");
                state = new(id, WidgetInstanceState.Initial(selected.Presentation));
            }
            else
            {
                var snapshot = snapshots.SingleOrDefault(s => s.Id == state.TrackerId)
                    ?? throw new ArgumentException("The selected tracker no longer exists.");
                state = state.Apply(snapshot.Presentation, args.Verb, data.RootElement);
            }
            Render(args.WidgetContext.Id, state.Serialize(), snapshots, saveBinding: true);
        }
    });

    private void UpdateActive()
    {
        lock (UpdateGate)
        {
            if (_active.Count == 0)
                return;
            var snapshots = Store().List();
            foreach (var widget in GetWidgets())
                if (_active.Contains(widget.WidgetContext.Id) && widget.WidgetContext.DefinitionId == WidgetCard.DefinitionId)
                    Render(widget.WidgetContext.Id, widget.CustomState, snapshots);
        }
    }

    private static void Update(string id)
    {
        lock (UpdateGate)
        {
            var widget = GetWidgets().Single(w => w.WidgetContext.Id == id);
            Render(id, widget.CustomState, Store().List(), saveBinding: true);
        }
    }

    private static WidgetSnapshotStore Store() => new(AppIdentity.ResolveRoamingDataDirectory());

    // The native projection can return null before the first widget is pinned.
    private static IReadOnlyList<WidgetInfo> GetWidgets() => WidgetManager.GetDefault().GetWidgetInfos() ?? [];

    private static void Render(string instanceId, string? state, IReadOnlyList<WidgetSnapshot> snapshots, bool saveBinding = false)
    {
        var instance = WidgetInstanceState.Parse(state, snapshots);
        var id = instance.TrackerId;
        var snapshot = snapshots.FirstOrDefault(s => s.Id == id);
        var copy = new WidgetCopy(
            LocalizationHelper.GetString("WidgetTitle"),
            LocalizationHelper.GetString("WidgetEmpty"),
            LocalizationHelper.GetString("WidgetSelect"),
            LocalizationHelper.GetString("WidgetShow"),
            LocalizationHelper.GetString("WidgetStale"),
            LocalizationHelper.GetString("WidgetSource"));
        var payload = WidgetCard.Render(snapshot, snapshots, copy, DateTimeOffset.UtcNow, instance.Values);
        var update = new WidgetUpdateRequestOptions(instanceId)
        {
            Template = payload.Template,
            Data = payload.Data
        };
        // Only provider lifecycle/selection callbacks own binding writes. A concurrent
        // Companion publication must not restore a binding read before user selection.
        if (saveBinding)
            update.CustomState = instance.Serialize();
        WidgetManager.GetDefault().UpdateWidget(update);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or COMException or KeyNotFoundException)
        {
            _logger.Error("Windows widget provider failed to update content.", ex);
        }
    }

    public void Dispose() => _timer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
