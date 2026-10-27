using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Text;
using System.Text.Json;

namespace OpenClawTray.Dialogs;

internal sealed class CronDetailsDialog
{
    private readonly IOperatorGatewayClient _client;
    private readonly long _epoch;
    private readonly Func<bool> _isCurrent;
    private readonly ContentDialog _dialog;
    private readonly TextBox _text;
    private readonly InfoBar _error;
    private bool _closed;
    private static readonly JsonSerializerOptions DisplayJson = new() { WriteIndented = true };

    public CronDetailsDialog(IOperatorGatewayClient client, long epoch, XamlRoot root, Func<bool> isCurrent, string title)
    {
        _client = client;
        _epoch = epoch;
        _isCurrent = isCurrent;
        _text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinWidth = 320, MaxHeight = 360, Header = title };
        AutomationProperties.SetAutomationId(_text, "CronDetailsText");
        _error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = LocalizationHelper.GetString("CronPage_Loading") });
        content.Children.Add(_text);
        content.Children.Add(_error);
        _dialog = new ContentDialog { Title = title, XamlRoot = root, Content = content,
            CloseButtonText = LocalizationHelper.GetString("A2UI_ModalClose") };
        _dialog.Closed += (_, _) => _closed = true;
    }

    public Task ShowJobAsync(string id) => ShowAsync(async () =>
    {
        var job = await _client.GetCronJobAsync(id, _epoch);
        if (CanApply()) _text.Text = CronRunHistoryDisplay.SanitizeForDisplay(JsonSerializer.Serialize(job, DisplayJson));
    });

    public Task ShowScratchAsync(string id) => ShowAsync(async () =>
    {
        var snapshot = await _client.GetCronScratchAsync(id, _epoch);
        if (!CanApply()) return;
        _text.IsReadOnly = false;
        _text.Text = snapshot.Content ?? "";
        ((StackPanel)_dialog.Content).Children.Insert(1, new TextBlock
        {
            Text = LocalizationHelper.Format("CronPage_ScratchHelp", snapshot.MaxBytes),
            TextWrapping = TextWrapping.Wrap
        });
        _dialog.PrimaryButtonText = LocalizationHelper.GetString("CronPage_SaveChanges");
        _dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            _dialog.IsPrimaryButtonEnabled = false;
            _text.IsReadOnly = true;
            var conflict = false;
            try
            {
                await RunAsync(async () =>
                {
                    var draft = _text.Text;
                    if (Encoding.UTF8.GetByteCount(draft) > snapshot.MaxBytes)
                        throw new InvalidOperationException(LocalizationHelper.Format("CronPage_ScratchTooLarge", snapshot.MaxBytes));
                    var result = await _client.SetCronScratchAsync(id, draft.Length == 0 ? null : draft,
                        snapshot.CurrentRevision, _epoch);
                    if (!CanApply()) return;
                    if (!result.Ok)
                    {
                        conflict = true;
                        _error.Message = LocalizationHelper.GetString("CronPage_ScratchConflict");
                        _error.IsOpen = true;
                        return;
                    }
                    _dialog.Hide();
                });
            }
            finally
            {
                if (CanApply())
                {
                    _text.IsReadOnly = false;
                    _dialog.IsPrimaryButtonEnabled = !conflict;
                }
                deferral.Complete();
            }
        };
    });

    public Task ShowTranscriptAsync(string id, string? runId, long? runAtMs) => ShowAsync(async () =>
    {
        string? cursor = null;
        async Task LoadAsync()
        {
            var page = await _client.GetCronHistoryAsync(id, runId, runAtMs, cursor, _epoch);
            if (!CanApply()) return;
            _text.Text = CronRunHistoryDisplay.SanitizeForDisplay(JsonSerializer.Serialize(page.Messages, DisplayJson));
            cursor = page.NextCursor;
            _dialog.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(cursor);
        }
        _dialog.PrimaryButtonText = LocalizationHelper.GetString("CronPage_TranscriptNext");
        _dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            _dialog.IsPrimaryButtonEnabled = false;
            try { await RunAsync(LoadAsync); }
            finally
            {
                if (CanApply()) _dialog.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(cursor);
                deferral.Complete();
            }
        };
        await LoadAsync();
    });

    private bool CanApply() => !_closed && _isCurrent() && _client.IsConnectedToGateway
        && _client.SessionMutationConnectionEpoch == _epoch;

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            if (!CanApply())
            {
                _dialog.Hide();
                return;
            }
            _error.IsOpen = false;
            await operation();
        }
        catch (Exception ex)
        {
            new AppLogger().Warn($"Cron details request failed: {TokenSanitizer.Sanitize(ex.Message)}");
            if (!CanApply()) return;
            _error.Message = CronRunHistoryDisplay.SanitizeForDisplay(ex.Message);
            _error.IsOpen = true;
        }
    }

    private async Task ShowAsync(Func<Task> load)
    {
        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            if (status != ConnectionStatus.Connected)
                _dialog.DispatcherQueue.TryEnqueue(() => _dialog.Hide());
        }
        _client.StatusChanged += OnStatusChanged;
        try
        {
            var showing = _dialog.ShowAsync();
            await RunAsync(load);
            if (!_closed) ((StackPanel)_dialog.Content).Children.RemoveAt(0);
            await showing;
        }
        finally { _client.StatusChanged -= OnStatusChanged; }
    }
}
