using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>Shared test doubles for the presentation seam tests.</summary>
internal sealed class RecordingUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly List<Action> _pending = new();

    public bool Disposed { get; private set; }
    public int EnqueuedCount { get; private set; }

    /// <summary>Settable so tests can simulate on-thread vs background callers.</summary>
    public bool HasThreadAccess { get; set; } = true;

    /// <summary>When false, enqueued actions are held until <see cref="FlushPending"/>.</summary>
    public bool RunEnqueuedImmediately { get; set; } = true;

    public bool TryEnqueue(Action action)
    {
        EnqueuedCount++;
        if (RunEnqueuedImmediately)
        {
            action();
        }
        else
        {
            _pending.Add(action);
        }

        return true;
    }

    public void FlushPending()
    {
        var pending = _pending.ToList();
        _pending.Clear();
        foreach (var action in pending)
        {
            action();
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeAppCommands : IAppCommands, IDisposable
{
    public List<string> Navigations { get; } = new();
    public bool Disposed { get; private set; }

    public void OpenDashboard(string? path = null) { }
    public void Navigate(string pageTag) => Navigations.Add(pageTag);
    public void Reconnect() { }
    public void Disconnect() { }
    public void ShowVoiceOverlay() { }
    public void ShowChat() { }
    public void CheckForUpdates() { }
    public void ShowOnboarding() { }
    public void ShowGatewayWizard() { }
    public void ShowConnectionStatus() { }
    public void NotifySettingsSaved() => NotifySettingsSavedCount++;
    public Task<bool> ApplyAutoStart(bool autoStart)
    {
        AutoStartApplied = autoStart;
        AutoStartApplyCount++;
        return Task.FromResult(AutoStartResult);
    }
    public void SetChatSpeakerMuted(bool muted) => SpeakerMuted = muted;
    public void SetChatToolCallsVisible(bool visible) => ToolCallsVisible = visible;
    public Task<bool> ResendOpenTelemetryProbeAsync() => Task.FromResult(true);

    public bool? SpeakerMuted { get; private set; }
    public bool? ToolCallsVisible { get; private set; }
    public bool? AutoStartApplied { get; private set; }
    public int AutoStartApplyCount { get; private set; }

    /// <summary>Result returned by <see cref="ApplyAutoStart"/> so tests can simulate OS-write failure.</summary>
    public bool AutoStartResult { get; set; } = true;
    public int NotifySettingsSavedCount { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// App-commands double that mimics the real App-owned settings writes: it persists directly to a
/// real <see cref="SettingsManager"/> and marks the save as a store self-write via
/// <see cref="ISettingsStore.BeginSelfWrite"/>, so tests can prove those writes do not echo an
/// external-change reload (unlike <see cref="FakeAppCommands"/>, which no-ops the save).
/// </summary>
internal sealed class SelfWritingAppCommands : IAppCommands
{
    private readonly ISettingsStore _store;
    private readonly SettingsManager _settings;

    public SelfWritingAppCommands(ISettingsStore store, SettingsManager settings)
    {
        _store = store;
        _settings = settings;
    }

    public void OpenDashboard(string? path = null) { }
    public void Navigate(string pageTag) { }
    public void Reconnect() { }
    public void Disconnect() { }
    public void ShowVoiceOverlay() { }
    public void ShowChat() { }
    public void CheckForUpdates() { }
    public void ShowOnboarding() { }
    public void ShowGatewayWizard() { }
    public void ShowConnectionStatus() { }
    public void NotifySettingsSaved() { }

    public Task<bool> ApplyAutoStart(bool autoStart)
    {
        _settings.AutoStart = autoStart;
        using (_store.BeginSelfWrite())
        {
            _settings.Save();
        }
        return Task.FromResult(true);
    }

    public void SetChatSpeakerMuted(bool muted)
    {
        _settings.VoiceTtsEnabled = !muted;
        using (_store.BeginSelfWrite())
        {
            _settings.Save();
        }
    }

    public void SetChatToolCallsVisible(bool visible) { }
    public Task<bool> ResendOpenTelemetryProbeAsync() => Task.FromResult(true);
}

/// <summary>Fake navigation-aware, disposable view model for scope-lifetime tests.</summary>
internal sealed class FakeViewModel : INavigationAware, IDisposable
{
    public int ActivateCount { get; private set; }
    public int DeactivateCount { get; private set; }
    public bool Disposed { get; private set; }
    public object? LastParameter { get; private set; }

    public void Activate(object? parameter)
    {
        ActivateCount++;
        LastParameter = parameter;
    }

    public void Deactivate() => DeactivateCount++;

    public void Dispose() => Disposed = true;
}

/// <summary>A second distinct view-model type for the manager tests.</summary>
internal sealed class OtherFakeViewModel : INavigationAware, IDisposable
{
    public int ActivateCount { get; private set; }
    public int DeactivateCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Activate(object? parameter) => ActivateCount++;
    public void Deactivate() => DeactivateCount++;
    public void Dispose() => Disposed = true;
}

/// <summary>View model whose Deactivate throws, to prove the scope is still disposed.</summary>
internal sealed class ThrowingDeactivateViewModel : INavigationAware, IDisposable
{
    public bool Disposed { get; private set; }

    public void Activate(object? parameter) { }
    public void Deactivate() => throw new InvalidOperationException("deactivate boom");
    public void Dispose() => Disposed = true;
}

/// <summary>View model whose Activate throws, to prove no half-activated scope leaks.</summary>
internal sealed class ThrowingActivateViewModel : INavigationAware, IDisposable
{
    public bool Disposed { get; private set; }

    public void Activate(object? parameter) => throw new InvalidOperationException("activate boom");
    public void Deactivate() { }
    public void Dispose() => Disposed = true;
}

/// <summary>
/// View model that calls back into the manager during Activate/Deactivate, to prove the
/// manager rejects re-entrant navigation.
/// </summary>
internal sealed class ReentrantViewModel : INavigationAware, IDisposable
{
    private readonly NavigationScopeManager _manager;
    private readonly bool _reenterOnActivate;
    private readonly bool _reenterOnDeactivate;

    public ReentrantViewModel(NavigationScopeManager manager, bool reenterOnActivate, bool reenterOnDeactivate)
    {
        _manager = manager;
        _reenterOnActivate = reenterOnActivate;
        _reenterOnDeactivate = reenterOnDeactivate;
    }

    public Exception? ActivateException { get; private set; }
    public Exception? DeactivateException { get; private set; }
    public bool Disposed { get; private set; }

    public void Activate(object? parameter)
    {
        if (_reenterOnActivate)
        {
            ActivateException = Record.Exception(() => _manager.Navigate(typeof(FakeViewModel), null));
        }
    }

    public void Deactivate()
    {
        if (_reenterOnDeactivate)
        {
            DeactivateException = Record.Exception(() => _manager.Navigate(typeof(FakeViewModel), null));
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// View model that appends labeled lifecycle events to a shared log, so tests can
/// assert ordering across multiple navigations (e.g. A deactivates before B activates).
/// </summary>
internal sealed class OrderRecordingViewModel : INavigationAware, IDisposable
{
    private readonly List<string> _log;
    private readonly string _label;

    public OrderRecordingViewModel(List<string> log, string label)
    {
        _log = log;
        _label = label;
    }

    public void Activate(object? parameter) => _log.Add($"activate:{_label}");
    public void Deactivate() => _log.Add($"deactivate:{_label}");
    public void Dispose() => _log.Add($"dispose:{_label}");
}

/// <summary>Second labeled recorder ("B") for cross-navigation ordering assertions.</summary>
internal sealed class BViewModel : INavigationAware, IDisposable
{
    private readonly List<string> _log;

    public BViewModel(List<string> log) => _log = log;

    public void Activate(object? parameter) => _log.Add("activate:B");
    public void Deactivate() => _log.Add("deactivate:B");
    public void Dispose() => _log.Add("dispose:B");
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openclaw-tray-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
