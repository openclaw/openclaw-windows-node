param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)]
    [ValidateSet('success', 'canceled', 'failure', 'duplicate-click', 'preview-failure', 'storage-failure', 'storage-refresh-after-success', 'prefetch-join-provider-change', 'prefetch-join-tts-enable')]
    [string]$Scenario
)
$ErrorActionPreference = 'Stop'

# Execute the production methods unchanged, substituting only external dependencies.
# This characterizes UI-thread ordering, not rendered WinUI or native synthesis.
# Retire this harness when VoiceSettingsPage has an injectable, WinUI-free owner.
$page = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\OpenClaw.Tray.WinUI\Pages\VoiceSettingsPage.xaml.cs') -Raw
$methods = foreach ($name in @('UpdateKokoroVoiceState', 'OnKokoroDownloadClickAsync', 'OnKokoroPreviewClickAsync', 'JoinActiveKokoroDownload', 'OnKokoroDownloadClick', 'OnTtsProviderChanged', 'OnTtsResponseToggled')) {
    $match = [regex]::Match($page, '(?ms)^    private (?:async )?(?:void|Task) ' + $name + '\([^)]*\)\s*(?:=>\s*[^\r\n;]+;|\r?\n(?:.|\n)*?^    \})')
    if (-not $match.Success) { throw "Production method not found: $name" }
    $match.Value
}
$fields = foreach ($name in @('_kokoroDownloadCts', '_kokoroPreviewInProgress', '_suppressEvents')) {
    $match = [regex]::Match($page, '(?m)^    private [^\r\n;]+ ' + $name + '(?: = [^;]+)?;')
    if (-not $match.Success) { throw "Production field not found: $name" }
    $match.Value
}
$singleFlight = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\OpenClaw.Shared\Audio\SingleFlightDownload.cs') -Raw
$singleFlight = $singleFlight.Replace('namespace OpenClaw.Shared.Audio;', '')
$guard = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\OpenClaw.Shared\AsyncEventHandlerGuard.cs') -Raw
$guard = $guard.Replace('namespace OpenClaw.Shared;', '')
$logger = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\OpenClaw.Shared\IOpenClawLogger.cs') -Raw
$logger = $logger.Replace('namespace OpenClaw.Shared;', '')
$fixture = @'
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public enum Visibility { Visible, Collapsed }
public sealed class Control
{
    public bool IsEnabled = true, IsIndeterminate;
    public string Text = "", Glyph = "";
    public Visibility Visibility;
    public double Value;
    public object? SelectedItem;
}
public sealed class ComboBoxItem { public object? Tag; }
public sealed class AppLogger { }
public static class Logger
{
    public static void Debug(string text) { }
    public static void Error(string text) { }
}
public sealed class SettingsManager
{
    public static string SettingsDirectoryPath = "unused-in-memory";
    public bool TtsKokoroAutoDownload = true;
    public string TtsProvider = "";
    // The production prefetcher subscribes to Saved and registers its single-flight
    // entry synchronously; this stub models that side effect of Save().
    public void Save()
    {
        if (TtsProvider != TtsCapability.KokoroProvider) return;
        // Production derives the pack from the selected Kokoro voice id.
        KokoroModelManager.StartPrefetch(ActiveVoiceTag ?? "Maple");
    }
    public static string? ActiveVoiceTag;
}
public sealed class AppState { public SettingsManager Settings = new(); }
public sealed record KokoroModelPackInfo(string PackId, long TotalSizeBytes);
public sealed record Voice(string LanguageTag);
public sealed class KokoroModelManager
{
    public static TaskCompletionSource<bool> Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Flights = new();
    public static IProgress<(long downloaded, long total)>? Progress;
    public static CancellationToken WaitToken;
    public static int Requests;
    public static bool Downloaded;
    public static bool StorageUnavailable;
    public KokoroModelManager(string path, AppLogger logger)
    {
        if (StorageUnavailable) throw new System.IO.IOException("Synthetic model storage unavailable");
    }
    public static KokoroModelPackInfo FindPackForVoice(string voiceId) => new(voiceId, 400_000_000);
    public static Voice FindVoice(string voiceId) => new("en-US");
    public bool IsPackDownloaded(string packId) => packId == "Maple" || Downloaded;
    public long GetPackSize(string packId) => 400_000_000;
    public Task DownloadPackAsync(string packId, IProgress<(long downloaded, long total)> progress, CancellationToken ct)
    {
        Requests++;
        Progress = progress;
        WaitToken = ct;
        return SingleFlightDownload.RunAsync(Flights, packId, _ => Download.Task, ct);
    }
    // Models KokoroVoicePackPrefetcher.Evaluate: registers the single-flight entry
    // synchronously so a save that triggered it sees IsDownloadInProgress=true.
    public static void StartPrefetch(string packId)
    {
        Requests++;
        _ = SingleFlightDownload.RunAsync(Flights, packId, _ => Download.Task, CancellationToken.None);
    }
    public static bool IsDownloadInProgress(string packId) => Flights.ContainsKey(packId);
}
public sealed class TtsSpeakArgs
{
    public string? Text, Provider, VoiceId;
    public bool Interrupt;
}
public static class TtsCapability { public const string KokoroProvider = "kokoro"; public const string PiperProvider = "piper"; }
public sealed class TextToSpeechService : IDisposable
{
    public static TaskCompletionSource<bool> Preview = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TextToSpeechService(AppLogger logger, SettingsManager settings) { }
    public Task SpeakAsync(TtsSpeakArgs args) => Preview.Task;
    public void Dispose() { }
}
public static class VoicePackDownloadFailure { public static int Classify(Exception error) => 0; }
// Production handlers construct 'new OpenClawTray.AppLogger()'; this namespace shim
// keeps the extracted method body source-identical.
namespace OpenClawTray
{
    internal sealed class AppLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
public sealed class RoutedEventArgs { }
public sealed class SelectionChangedEventArgs { }
// Production OnTtsResponseToggled persists through App.SetChatSpeakerMuted, which
// saves settings; this stub models the save side effect (prefetcher Evaluate).
public sealed class App
{
    public static App? Current;
    public KokoroDownloadHarness? Owner;
    public void SetChatSpeakerMuted(bool muted) => Owner?.CurrentAppSettings?.Save();
}
public sealed class Pump : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
    public void Until(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Source-method continuation did not complete.");
            if (_queue.TryDequeue(out var work)) work.Item1(work.Item2);
            else Thread.Sleep(1);
        }
    }
    public void Drain()
    {
        while (_queue.TryDequeue(out var work)) work.Item1(work.Item2);
    }
}
public sealed class KokoroDownloadHarness
{
    private static readonly AppState CurrentApp = new();
    private readonly Control KokoroVoiceCombo = new(), KokoroDownloadButton = new(),
        KokoroDownloadButtonText = new(), KokoroDownloadIcon = new(), KokoroDeleteButton = new(),
        KokoroPreviewButton = new(), KokoroStatusText = new(), KokoroDownloadProgress = new(),
        KokoroPreviewLabel = new(), KokoroPreviewIcon = new();
    // The provider-change and TTS-enable scenarios exercise production handlers that
    // read these controls; the fixtures mirror the xaml control names exactly.
    private readonly List<ComboBoxItem> TtsProviderComboItems = new();
    private ComboBoxItem? TtsProviderComboSelected;
    private bool TtsResponseToggleIsOn = true;
    private sealed class TtsProviderComboView(KokoroDownloadHarness page)
    {
        public ICollection<ComboBoxItem> Items => page.TtsProviderComboItems;
        public object? SelectedItem
        {
            get => page.TtsProviderComboSelected;
            set => page.TtsProviderComboSelected = value as ComboBoxItem;
        }
        public int SelectedIndex
        {
            get => page.TtsProviderComboSelected is null ? -1 : page.TtsProviderComboItems.IndexOf(page.TtsProviderComboSelected);
            set => page.TtsProviderComboSelected = value >= 0 && value < page.TtsProviderComboItems.Count ? page.TtsProviderComboItems[value] : null;
        }
    }
    private readonly TtsProviderComboView TtsProviderCombo;
    private sealed class TtsResponseToggleView(KokoroDownloadHarness page)
    {
        public bool IsOn
        {
            get => page.TtsResponseToggleIsOn;
            set => page.TtsResponseToggleIsOn = value;
        }
    }
    private readonly TtsResponseToggleView TtsResponseToggle;
    public KokoroDownloadHarness()
    {
        TtsProviderCombo = new TtsProviderComboView(this);
        TtsResponseToggle = new TtsResponseToggleView(this);
    }
    public SettingsManager CurrentAppSettings => CurrentApp.Settings;
    private static string L(string key) => key;
    private static string Lf(string key, params object?[] args) => key + ":" + string.Join(",", args);
    private static string PreviewTextFor(string? language) => "preview";
    private static string KokoroDownloadFailureText(int kind, KokoroModelPackInfo pack) => "sanitized-failure";
    private void UpdateCapabilityState() { }
    private void UpdateTtsProviderVisibility() { }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void AssertBusy(Task download, string expectedStatus)
    {
        Require(!download.IsCompleted, "Download must remain pending.");
        Require(!KokoroDownloadButton.IsEnabled, "Preview completion re-enabled Download during the pending transfer.");
        Require(!KokoroVoiceCombo.IsEnabled, "Pending transfer must keep the picker locked.");
        Require(KokoroDownloadProgress.Visibility == Visibility.Visible, "Preview completion hid active download progress.");
        Require(KokoroStatusText.Text == expectedStatus, "Unrelated preview overwrote download status.");
        Require(KokoroDownloadButtonText.Text == L("VoiceSettingsPage_KokoroButtonDownloading"), "Download label was reset.");
    }
    public static void Run(string scenario)
    {
        var previous = SynchronizationContext.Current;
        var pump = new Pump();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            if (scenario == "prefetch-join-provider-change" || scenario == "prefetch-join-tts-enable")
            {
                RunPrefetchJoinScenario(scenario == "prefetch-join-tts-enable");
                return;
            }
            var page = new KokoroDownloadHarness();
            page.KokoroVoiceCombo.SelectedItem = new ComboBoxItem { Tag = "Maple" };
            page.UpdateKokoroVoiceState();
            var preview = page.OnKokoroPreviewClickAsync();
            Require(page.KokoroVoiceCombo.IsEnabled, "Preview must allow selecting a different voice.");
            page.KokoroVoiceCombo.SelectedItem = new ComboBoxItem { Tag = "Dora" };
            page.UpdateKokoroVoiceState();
            var download = page.OnKokoroDownloadClickAsync();
            var owner = page._kokoroDownloadCts!;
            var oldProgress = KokoroModelManager.Progress!;
            page.AssertBusy(download, L("VoiceSettingsPage_KokoroConnecting"));
            oldProgress.Report((100_000_000, 400_000_000));
            pump.Drain();
            Require(page.KokoroDownloadProgress.Value == 25, "Active owner must apply progress.");
            var status = page.KokoroStatusText.Text;
            if (scenario == "preview-failure")
                TextToSpeechService.Preview.SetException(new InvalidOperationException("synthetic preview failure"));
            else
                TextToSpeechService.Preview.SetResult(true);
            pump.Until(() => preview.IsCompleted);
            preview.GetAwaiter().GetResult();
            page.AssertBusy(download, status);
            Require(page.KokoroDownloadProgress.Value == 25, "Preview reset the progress value.");
            Require(!page._kokoroPreviewInProgress, "Preview ownership was not released.");

            if (scenario == "duplicate-click")
            {
                var duplicate = page.OnKokoroDownloadClickAsync();
                Require(duplicate.IsCompletedSuccessfully, "Duplicate click should be ignored while busy.");
                Require(KokoroModelManager.Requests == 1, "Duplicate click started a second waiter.");
                Require(ReferenceEquals(owner, page._kokoroDownloadCts), "Duplicate click replaced the owner.");
                Require(!KokoroModelManager.WaitToken.IsCancellationRequested, "Duplicate click canceled the existing wait.");
                page.AssertBusy(download, status);
            }

            var storageFailure = scenario == "storage-failure" || scenario == "storage-refresh-after-success";
            KokoroModelManager.StorageUnavailable = storageFailure;
            if (scenario == "canceled") owner.Cancel();
            else if (scenario == "failure" || scenario == "storage-failure")
                KokoroModelManager.Download.SetException(new InvalidOperationException("synthetic download failure"));
            else
            {
                KokoroModelManager.Downloaded = true;
                KokoroModelManager.Download.SetResult(true);
            }
            pump.Until(() => download.IsCompleted);
            Require(!download.IsFaulted, "Terminal refresh escaped instead of restoring download recovery controls.");
            download.GetAwaiter().GetResult();
            Require(page.KokoroVoiceCombo.IsEnabled, "Terminal download must unlock the picker.");
            Require(page._kokoroDownloadCts == null, "Terminal download retained ownership.");
            Require(page.KokoroDownloadProgress.Visibility == Visibility.Collapsed, "Terminal download must hide progress.");
            if (scenario == "canceled" || scenario == "failure" || storageFailure)
            {
                Require(page.KokoroDownloadButton.IsEnabled, "Canceled/failed download must allow retry.");
                Require(page.KokoroStatusText.Text == (scenario == "canceled"
                    ? L("VoiceSettingsPage_KokoroDownloadCanceled") : "sanitized-failure"), "Terminal status was lost.");
                Require(page.KokoroDownloadButtonText.Text == L(scenario != "canceled"
                    ? "VoiceSettingsPage_KokoroButtonRetry" : "VoiceSettingsPage_KokoroButtonDownloadPack"), "Wrong retry label.");
            }
            else
            {
                Require(!page.KokoroDownloadButton.IsEnabled, "Ready pack must not offer download.");
                Require(page.KokoroPreviewButton.IsEnabled, "Completed preview must become available after download.");
                Require(page.KokoroDeleteButton.Visibility == Visibility.Visible, "Ready pack must allow deletion.");
                Require(page.KokoroStatusText.Text.StartsWith(L("VoiceSettingsPage_KokoroPackReady")), "Ready status missing.");
            }
            var terminalStatus = page.KokoroStatusText.Text;
            oldProgress.Report((400_000_000, 400_000_000));
            pump.Drain();
            Require(page.KokoroStatusText.Text == terminalStatus, "Late progress overwrote terminal status.");

            if (scenario == "canceled")
            {
                Require(!KokoroModelManager.Download.Task.IsCompleted, "Cancellation must stop only the caller's wait.");
                var retry = page.OnKokoroDownloadClickAsync();
                var retryStatus = page.KokoroStatusText.Text;
                oldProgress.Report((400_000_000, 400_000_000));
                pump.Drain();
                page.AssertBusy(retry, retryStatus);
                Require(page.KokoroDownloadProgress.Value == 0, "Old owner's progress reached the new wait.");
                KokoroModelManager.Downloaded = true;
                KokoroModelManager.Download.SetResult(true);
                pump.Until(() => retry.IsCompleted);
                retry.GetAwaiter().GetResult();
                Require(page._kokoroDownloadCts == null && page.KokoroVoiceCombo.IsEnabled, "Retry did not release its controls.");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    // PR #1777 P2: a provider or TTS-enable save starts a background prefetch; the
    // already-open page must attach to it (progress, then ready controls) instead of
    // staying on a stale "not downloaded" panel until reopened.
    private static void RunPrefetchJoinScenario(bool viaTtsEnable)
    {
        KokoroModelManager.Downloaded = false;
        var page = new KokoroDownloadHarness();
        page._suppressEvents = false; // production Initialize clears the guard before handlers fire
        App.Current = new App { Owner = page };
        page.KokoroVoiceCombo.SelectedItem = new ComboBoxItem { Tag = "Dora" };
        SettingsManager.ActiveVoiceTag = "Dora";
        page.TtsProviderComboItems.Add(new ComboBoxItem { Tag = TtsCapability.KokoroProvider });
        page.TtsProviderComboItems.Add(new ComboBoxItem { Tag = TtsCapability.PiperProvider });
        page.TtsProviderComboSelected = page.TtsProviderComboItems[1]; // provider-change variant opens on Piper
        page.CurrentAppSettings.TtsProvider = TtsCapability.PiperProvider;
        if (viaTtsEnable)
        {
            // TTS-enable variant: Kokoro already selected, spoken responses off.
            page.TtsProviderComboSelected = page.TtsProviderComboItems[0];
            page.CurrentAppSettings.TtsProvider = TtsCapability.KokoroProvider;
        }
        page.UpdateKokoroVoiceState();
        Require(page.KokoroStatusText.Text == Lf("VoiceSettingsPage_KokoroPackNotDownloaded", "381"),
            "Precondition: panel must show the not-downloaded state.");
        Require(page.KokoroDownloadProgress.Visibility == Visibility.Collapsed,
            "Precondition: no progress may be visible before the prefetch starts.");

        if (viaTtsEnable)
        {
            page.TtsResponseToggleIsOn = true;
            page.OnTtsResponseToggled(page, new RoutedEventArgs());
        }
        else
        {
            page.TtsProviderComboSelected = page.TtsProviderComboItems[0];
            page.OnTtsProviderChanged(page, new SelectionChangedEventArgs());
        }

        Require(KokoroModelManager.IsDownloadInProgress("Dora"),
            "The production save must have started the background prefetch.");
        Require(page._kokoroDownloadCts != null,
            "The changed handler did not attach the open page to the running prefetch.");
        Require(page.KokoroDownloadProgress.Visibility == Visibility.Visible,
            "Attached page must show download progress.");
        Require(page.KokoroStatusText.Text == L("VoiceSettingsPage_KokoroConnecting"),
            "Attached page must show the connecting status, not a stale panel.");
        Require(KokoroModelManager.Requests == 2,
            "Attach must reuse the prefetch's single flight, not start a second transfer.");

        KokoroModelManager.Progress!.Report((200_000_000, 400_000_000));
        ((Pump)SynchronizationContext.Current!).Drain();
        Require(page.KokoroDownloadProgress.Value == 50, "Joined page must receive shared progress.");

        KokoroModelManager.Downloaded = true;
        KokoroModelManager.Download.SetResult(true);
        var pump = (Pump)SynchronizationContext.Current!;
        pump.Until(() => page._kokoroDownloadCts == null);
        Require(page._kokoroDownloadCts == null, "Joined wait must release ownership when the transfer ends.");
        Require(page.KokoroPreviewButton.Visibility == Visibility.Visible,
            "Completed prefetch must expose Preview without reopening the page.");
        Require(page.KokoroDeleteButton.Visibility == Visibility.Visible,
            "Completed prefetch must expose Delete without reopening the page.");
        Require(!page.KokoroDownloadButton.IsEnabled, "Ready pack must not offer download.");
    }
'@

# SingleFlightDownload's and AsyncEventHandlerGuard's using directives must precede the fixture declarations.
$code = $singleFlight + "`n" + $guard + "`n" + $logger + "`n" +
    $fixture.Substring($fixture.IndexOf('public enum Visibility')) +
    "`n" + ($fields -join "`n") + "`n" + ($methods -join "`n") + "`n}"
$code = "#nullable enable`nusing System.Diagnostics;`nusing System.Globalization;`n" + $code
Add-Type -TypeDefinition $code -Language CSharp
[KokoroDownloadHarness]::Run($Scenario)
Write-Output "PASS $Scenario (production source methods; in-memory controls and tasks)"
