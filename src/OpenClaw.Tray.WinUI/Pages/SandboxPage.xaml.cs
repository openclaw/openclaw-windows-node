using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OpenClawTray.Pages;

public sealed partial class SandboxPage : Page
{
    private HubWindow? _hub;
    private bool _suppress;
    private bool _confirmDialogOpen;
    private OpenClaw.Shared.Mxc.MxcAvailability? _cachedAvailability;

    public ObservableCollection<CustomFolderRow> CustomFolders { get; } = new();

    /// <summary>
    /// Cached MxcAvailability for the lifetime of this page instance. Probe() does
    /// registry reads and filesystem walks; we don't need to repeat that work on every
    /// toggle/preset change. The result is stable as long as the OS doesn't change
    /// under us, which is fine for a settings page.
    /// </summary>
    private OpenClaw.Shared.Mxc.MxcAvailability GetAvailability()
        => _cachedAvailability ??= OpenClaw.Shared.Mxc.MxcAvailability.Probe();

    // ── Quick-preset definitions ─────────────────────────────────────
    //
    // Picking a preset writes ALL the values below into SettingsManager
    // in one go (auto-saves like any other change). Users can override any
    // individual control afterward — the cards just highlight whichever
    // preset matches the current state.

    private sealed record SandboxPreset(
        string Tag,
        bool SandboxEnabled,
        bool AllowOutbound,
        SandboxFolderAccess? DocumentsAccess,
        SandboxFolderAccess? DownloadsAccess,
        SandboxFolderAccess? DesktopAccess,
        SandboxClipboardMode Clipboard,
        int TimeoutMs,
        long MaxOutputBytes);

    private static readonly SandboxPreset s_lockedDown = new(
        Tag: "LockedDown",
        SandboxEnabled: true,
        AllowOutbound: false,
        DocumentsAccess: null,
        DownloadsAccess: null,
        DesktopAccess: null,
        Clipboard: SandboxClipboardMode.None,
        TimeoutMs: 30_000,
        MaxOutputBytes: 4 * 1024 * 1024);

    private static readonly SandboxPreset s_balanced = new(
        Tag: "Balanced",
        SandboxEnabled: true,
        AllowOutbound: true,
        DocumentsAccess: SandboxFolderAccess.ReadOnly,
        DownloadsAccess: SandboxFolderAccess.ReadOnly,
        DesktopAccess: SandboxFolderAccess.ReadOnly,
        Clipboard: SandboxClipboardMode.Read,
        TimeoutMs: 60_000,
        MaxOutputBytes: 16 * 1024 * 1024);

    private static readonly SandboxPreset s_permissive = new(
        Tag: "Permissive",
        SandboxEnabled: true,
        AllowOutbound: true,
        DocumentsAccess: SandboxFolderAccess.ReadWrite,
        DownloadsAccess: SandboxFolderAccess.ReadWrite,
        DesktopAccess: SandboxFolderAccess.ReadWrite,
        Clipboard: SandboxClipboardMode.Both,
        TimeoutMs: 300_000,
        MaxOutputBytes: 64 * 1024 * 1024);

    public SandboxPage()
    {
        InitializeComponent();
        CustomFoldersList.ItemsSource = CustomFolders;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadState();
        ProbeStatus();
        UpdateControlsEnabledState();
    }

    // ── Load + probe ─────────────────────────────────────────────────

    private void LoadState()
    {
        if (_hub?.Settings is not { } settings) return;

        _suppress = true;
        try
        {
            SandboxEnabledToggle.IsOn = settings.SystemRunSandboxEnabled;

            NetInternetToggle.IsOn = settings.SystemRunAllowOutbound;

            SelectAccessTag(DocsAccessCombo, settings.SandboxDocumentsAccess);
            SelectAccessTag(DownloadsAccessCombo, settings.SandboxDownloadsAccess);
            SelectAccessTag(DesktopAccessCombo, settings.SandboxDesktopAccess);

            CustomFolders.Clear();
            foreach (var f in settings.SandboxCustomFolders ?? new())
                CustomFolders.Add(new CustomFolderRow(f.Path, f.Access));
            RefreshCustomFoldersUi();

            (settings.SandboxClipboard switch
            {
                SandboxClipboardMode.Read => ClipboardReadRadio,
                SandboxClipboardMode.Write => ClipboardWriteRadio,
                SandboxClipboardMode.Both => ClipboardBothRadio,
                _ => ClipboardNoneRadio,
            }).IsChecked = true;

            var secs = Math.Clamp(settings.SandboxTimeoutMs / 1000, 5, 300);
            TimeoutSlider.Value = secs;
            TimeoutLabel.Text = $"Command timeout: {secs} sec";

            SelectMaxOutputTag(settings.SandboxMaxOutputBytes);
        }
        finally
        {
            _suppress = false;
        }

        UpdatePresetHighlight();
        UpdateSandboxStatusCard();
        UpdateControlsEnabledState();
    }

    /// <summary>
    /// Drives the page header (icon + title + subtext + toggle visibility) based on
    /// MXC availability AND the current sandbox toggle state. Three visual states:
    ///   1. Available + ON   → 🛡 "Sandbox is on" + toggle visible
    ///   2. Available + OFF  → ⚠ "Sandbox is off — high risk" + toggle visible
    ///   3. Unavailable      → ⚠ "Sandbox unavailable — commands blocked" + toggle hidden
    /// When MXC is unavailable the toggle is hidden AND the runner fails closed
    /// (MxcCommandRunner.RunAsync short-circuits to a deny response). The user
    /// cannot opt out of the block on an unsupported machine — they must fix MXC.
    /// </summary>
    private void UpdateSandboxStatusCard()
    {
        var availability = GetAvailability();
        var enabled = SandboxEnabledToggle.IsOn;
        var available = availability.HasAnyBackend;

        UpdateUnavailableActionBar(availability);

        if (!available)
        {
            SandboxStatusIcon.Text = "⚠";
            SandboxStatusTitle.Text = "Sandbox unavailable — commands blocked";
            SandboxStatusSubtext.Text = "MXC sandboxing isn't supported on this machine. Agent commands won't run until you fix it — see below.";
            SandboxEnabledToggle.Visibility = Visibility.Collapsed;
            return;
        }

        SandboxEnabledToggle.Visibility = Visibility.Visible;

        if (enabled)
        {
            SandboxStatusIcon.Text = "🛡";
            SandboxStatusTitle.Text = "Sandbox is on";
            SandboxStatusSubtext.Text = "Windows commands started by agents run in a restricted AppContainer with only the access enabled below.";
        }
        else
        {
            SandboxStatusIcon.Text = "⚠";
            SandboxStatusTitle.Text = "Sandbox is off — high risk";
            SandboxStatusSubtext.Text = "Agent-started Windows commands run as your Windows user and can access your files, network, clipboard, and OpenClaw settings.";
        }
    }

    /// <summary>
    /// Shows or hides the prominent "Sandbox unavailable" InfoBar based on availability,
    /// and categorizes the failure mode so we can suggest a relevant action:
    ///   - Windows build/UBR too old → "Open Windows Update"
    ///   - wxc-exec.exe or run-command.cjs missing → "Show install instructions"
    ///   - Anything else → no primary action, just the learn-more hyperlink
    /// </summary>
    private void UpdateUnavailableActionBar(OpenClaw.Shared.Mxc.MxcAvailability availability)
    {
        if (availability.HasAnyBackend)
        {
            UnavailableActionBar.IsOpen = false;
            return;
        }

        var reasons = availability.UnsupportedReasons;
        var reasonText = reasons.Count > 0
            ? string.Join("  ·  ", reasons)
            : "MXC sandboxing primitives are not available on this machine.";

        var isWindowsIssue = reasons.Any(r =>
            r.Contains("Windows build", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Windows UBR", StringComparison.OrdinalIgnoreCase));

        var isSetupIssue = !availability.IsWxcExecResolvable
            || availability.RunCommandScriptPath is null;

        if (isWindowsIssue)
        {
            UnavailableActionBar.Title = "Your Windows version doesn't support sandboxing yet";
            UnavailableActionMessage.Text =
                $"{reasonText}\n\nMXC sandboxing requires a recent Windows build with the AppContainer primitives shipped. " +
                "Install the latest Windows updates (or join the Windows Insider Program for the newest builds).";
            UnavailablePrimaryButton.Content = "Open Windows Update";
            UnavailablePrimaryButton.Tag = "windowsupdate";
            UnavailablePrimaryButton.Visibility = Visibility.Visible;
        }
        else if (isSetupIssue)
        {
            UnavailableActionBar.Title = "Sandboxing components are missing";
            UnavailableActionMessage.Text =
                $"{reasonText}\n\nThe MXC bridge script or the wxc-exec binary couldn't be located. " +
                "If this is a developer build, run `npm ci` at the repository root. " +
                "Otherwise reinstall the companion app.";
            UnavailablePrimaryButton.Content = "Show install instructions";
            UnavailablePrimaryButton.Tag = "install";
            UnavailablePrimaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            UnavailableActionBar.Title = "Sandbox unavailable";
            UnavailableActionMessage.Text = reasonText;
            UnavailablePrimaryButton.Visibility = Visibility.Collapsed;
        }

        UnavailableActionBar.IsOpen = true;
    }

    private async void OnUnavailableActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var tag = btn.Tag as string;
        try
        {
            var uri = tag switch
            {
                "windowsupdate" => new Uri("ms-settings:windowsupdate"),
                "install" => new Uri("https://github.com/microsoft/mxc#getting-started"),
                _ => null,
            };
            if (uri != null)
                await global::Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Best-effort — if the URI handler isn't available we just no-op.
        }
    }

    /// <summary>
    /// Enables/disables sub-sections based on MXC availability AND master toggle state.
    /// When sandbox is off (or MXC unavailable), the entire page below the master
    /// toggle dims — presets and their heading included — so users can't tweak
    /// settings that don't apply.
    /// </summary>
    private void UpdateControlsEnabledState()
    {
        var availability = GetAvailability();
        var available = availability.HasAnyBackend;
        var sandboxOn = SandboxEnabledToggle.IsOn;
        var active = available && sandboxOn;

        // StackPanel doesn't expose IsEnabled, so we mimic the disabled look manually:
        // block hit-testing (no clicks reach children) and dim opacity for visual cue.
        SandboxControlsContainer.IsHitTestVisible = active;
        SandboxControlsContainer.Opacity = active ? 1.0 : 0.45;

        // Dim the Security-level section (heading + subtitle + preset cards) too.
        PresetSection.IsHitTestVisible = active;
        PresetSection.Opacity = active ? 1.0 : 0.45;

        PresetLockedButton.IsEnabled = active;
        PresetBalancedButton.IsEnabled = active;
        PresetPermissiveButton.IsEnabled = active;
    }

    private void ProbeStatus()
    {
        UpdateSandboxStatusCard();
    }

    // ── Presets ──────────────────────────────────────────────────────

    private void ApplyPreset(SandboxPreset preset)
    {
        if (_hub?.Settings is not { } s) return;

        _suppress = true;
        try
        {
            s.SystemRunSandboxEnabled = preset.SandboxEnabled;
            s.SystemRunAllowOutbound = preset.AllowOutbound;
            s.SandboxDocumentsAccess = preset.DocumentsAccess;
            s.SandboxDownloadsAccess = preset.DownloadsAccess;
            s.SandboxDesktopAccess = preset.DesktopAccess;
            s.SandboxClipboard = preset.Clipboard;
            s.SandboxTimeoutMs = preset.TimeoutMs;
            s.SandboxMaxOutputBytes = preset.MaxOutputBytes;
            // Note: custom folders are NOT touched by presets — users curate them.
        }
        finally
        {
            _suppress = false;
        }

        // Reflect into the UI controls and persist.
        LoadState();
        s.Save();

        // If the user just applied a preset and still has custom folder grants,
        // warn them — they may override the preset's intent (especially Locked Down).
        var customCount = s.SandboxCustomFolders?.Count ?? 0;
        if (customCount > 0)
        {
            var plural = customCount == 1 ? "" : "s";
            PresetCustomFoldersWarning.Message = preset.Tag == s_lockedDown.Tag
                ? $"Locked Down applied, but {customCount} custom folder grant{plural} remain active. Remove them in the Files section below for a fully locked-down sandbox."
                : $"{customCount} custom folder grant{plural} remain active alongside this preset.";
            PresetCustomFoldersWarning.IsOpen = true;
        }
        else
        {
            PresetCustomFoldersWarning.IsOpen = false;
        }
    }

    private void OnPresetLockedClick(object sender, RoutedEventArgs e) => ApplyPreset(s_lockedDown);
    private void OnPresetBalancedClick(object sender, RoutedEventArgs e) => ApplyPreset(s_balanced);
    private void OnPresetPermissiveClick(object sender, RoutedEventArgs e) => ApplyPreset(s_permissive);

    private void UpdatePresetHighlight()
    {
        var s = _hub?.Settings;
        var active = s is null ? null : DetectActivePreset(s);
        SetPresetCardActive(PresetLockedButton, active?.Tag == s_lockedDown.Tag);
        SetPresetCardActive(PresetBalancedButton, active?.Tag == s_balanced.Tag);
        SetPresetCardActive(PresetPermissiveButton, active?.Tag == s_permissive.Tag);
    }

    private static void SetPresetCardActive(Button button, bool active)
    {
        // Visual cue for the active preset: accent-themed border, slightly thicker.
        // The default ButtonStyle has visual states for BOTH the background and the
        // border brush (PointerOver / Pressed). Setting button.BorderBrush directly
        // only paints the Normal state — on hover the template animates to
        // ButtonBorderBrushPointerOver (grey), which is what was "stealing" the
        // accent. To stop that we override the per-state resources locally on the
        // button so hover/pressed keep the accent (or no change at all when idle).
        button.BorderThickness = new Thickness(active ? 2 : 1);
        var accent = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
        var defaultStroke = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"];
        button.BorderBrush = active ? accent : defaultStroke;

        if (active)
        {
            var defaultBg = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlFillColorDefaultBrush"];

            // Pin background to default fill for all states (no grey flash).
            button.Resources["ButtonBackground"] = defaultBg;
            button.Resources["ButtonBackgroundPointerOver"] = defaultBg;
            button.Resources["ButtonBackgroundPressed"] = defaultBg;

            // Pin border brush to accent for all states (no grey/dark-stroke flash).
            button.Resources["ButtonBorderBrush"] = accent;
            button.Resources["ButtonBorderBrushPointerOver"] = accent;
            button.Resources["ButtonBorderBrushPressed"] = accent;
        }
        else
        {
            button.Resources.Remove("ButtonBackground");
            button.Resources.Remove("ButtonBackgroundPointerOver");
            button.Resources.Remove("ButtonBackgroundPressed");
            button.Resources.Remove("ButtonBorderBrush");
            button.Resources.Remove("ButtonBorderBrushPointerOver");
            button.Resources.Remove("ButtonBorderBrushPressed");
        }

        // Force the visual state to refresh so the new resources take effect immediately.
        Microsoft.UI.Xaml.VisualStateManager.GoToState(button, "Normal", false);
    }

    private static SandboxPreset? DetectActivePreset(SettingsManager s)
    {
        if (Matches(s, s_lockedDown)) return s_lockedDown;
        if (Matches(s, s_balanced)) return s_balanced;
        if (Matches(s, s_permissive)) return s_permissive;
        return null;
    }

    private static bool Matches(SettingsManager s, SandboxPreset p)
    {
        return s.SystemRunSandboxEnabled == p.SandboxEnabled
            && s.SystemRunAllowOutbound == p.AllowOutbound
            && s.SandboxDocumentsAccess == p.DocumentsAccess
            && s.SandboxDownloadsAccess == p.DownloadsAccess
            && s.SandboxDesktopAccess == p.DesktopAccess
            && s.SandboxClipboard == p.Clipboard
            && s.SandboxTimeoutMs == p.TimeoutMs
            && s.SandboxMaxOutputBytes == p.MaxOutputBytes;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void SelectAccessTag(ComboBox combo, SandboxFolderAccess? access)
    {
        var tag = access switch
        {
            SandboxFolderAccess.ReadOnly => "ReadOnly",
            SandboxFolderAccess.ReadWrite => "ReadWrite",
            _ => "None",
        };
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
    }

    private void SelectMaxOutputTag(long bytes)
    {
        var match = MaxOutputCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => long.TryParse((string?)i.Tag, out var v) && v == bytes);
        MaxOutputCombo.SelectedItem = match ?? MaxOutputCombo.Items[1]; // default 4 MiB
    }

    private static SandboxFolderAccess? ReadAccessTag(ComboBox combo)
    {
        var tag = (string?)((ComboBoxItem?)combo.SelectedItem)?.Tag;
        return tag switch
        {
            "ReadOnly" => SandboxFolderAccess.ReadOnly,
            "ReadWrite" => SandboxFolderAccess.ReadWrite,
            _ => null,
        };
    }

    private void Save()
    {
        if (_suppress) return;
        _hub?.Settings?.Save();
        UpdatePresetHighlight();
    }

    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Event handlers ───────────────────────────────────────────────

    private async void OnSandboxEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;

        var newValue = SandboxEnabledToggle.IsOn;
        var oldValue = s.SystemRunSandboxEnabled;

        // Confirm before turning sandbox OFF — this is the high-risk transition.
        if (!newValue && oldValue)
        {
            // Re-entrancy guard: rapid toggling could otherwise stack ContentDialog
            // instances, which raises a COMException ("Cannot show another dialog
            // until the previous one is dismissed"). Snap the toggle back and bail.
            if (_confirmDialogOpen)
            {
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Turn off sandboxing?",
                Content = "Agent-started Windows commands will run as you and may access your files, network, clipboard, and OpenClaw settings.\n\nThis is the high-risk mode. Only do this if you trust the agent and need it for debugging or performance.",
                PrimaryButtonText = "Turn off",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            ContentDialogResult result;
            _confirmDialogOpen = true;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another dialog is already open (e.g., system surface popped
                // one underneath us). Treat as cancel — keep sandbox on.
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }
            finally
            {
                _confirmDialogOpen = false;
            }

            if (result != ContentDialogResult.Primary)
            {
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }
        }

        s.SystemRunSandboxEnabled = newValue;
        UpdateSandboxStatusCard();
        UpdateControlsEnabledState();
        Save();
    }

    private void OnNetInternetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SystemRunAllowOutbound = NetInternetToggle.IsOn;
        Save();
    }

    private void OnDocsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDocumentsAccess = ReadAccessTag(DocsAccessCombo);
        Save();
    }

    private void OnDownloadsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDownloadsAccess = ReadAccessTag(DownloadsAccessCombo);
        Save();
    }

    private void OnDesktopAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDesktopAccess = ReadAccessTag(DesktopAccessCombo);
        Save();
    }

    private async void OnAddCustomFolder(object sender, RoutedEventArgs e)
        => await PickCustomFolderAsync(SandboxFolderAccess.ReadOnly);

    private async System.Threading.Tasks.Task PickCustomFolderAsync(SandboxFolderAccess access)
    {
        if (_hub is null) return;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(_hub);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrWhiteSpace(folder.Path)) return;
        if (CustomFolders.Any(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase))) return;

        var row = new CustomFolderRow(folder.Path, access);
        // Mark "initial" as already fired — when the ListView materializes this row's
        // ComboBox and its SelectedIndex binding fires SelectionChanged, we want to
        // skip processing (this isn't a user-driven change, the settings entry was
        // already added below).
        row.InitialSelectionFired = true;
        CustomFolders.Add(row);
        RefreshCustomFoldersUi();

        if (_hub.Settings is { } s)
        {
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = folder.Path, Access = access });
            Save();
        }
    }

    private void OnCustomFolderAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not ComboBox combo) return;
        if (combo.DataContext is not CustomFolderRow row) return;

        // The ComboBox's SelectedIndex binding fires SelectionChanged once on
        // initial materialization. Skip that "fake" change — only act on real
        // user-driven changes.
        if (!row.InitialSelectionFired)
        {
            row.InitialSelectionFired = true;
            return;
        }

        if (_hub?.Settings is not { } s) return;

        var newIndex = combo.SelectedIndex;
        var newAccess = newIndex switch
        {
            1 => (SandboxFolderAccess?)SandboxFolderAccess.ReadOnly,
            2 => (SandboxFolderAccess?)SandboxFolderAccess.ReadWrite,
            _ => null, // 0 = Blocked → remove the grant
        };

        var existing = s.SandboxCustomFolders.FirstOrDefault(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));

        if (newAccess is null)
        {
            // User picked "Blocked" — drop the grant entirely.
            if (existing != null) s.SandboxCustomFolders.Remove(existing);
            var rowToRemove = CustomFolders.FirstOrDefault(f =>
                string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            if (rowToRemove != null) CustomFolders.Remove(rowToRemove);
            RefreshCustomFoldersUi();
        }
        else if (existing != null)
        {
            existing.Access = newAccess.Value;
        }
        else
        {
            // Defensive: row exists in UI but not in settings (shouldn't happen).
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = row.Path, Access = newAccess.Value });
        }

        row.AccessIndex = newIndex;
        Save();
    }

    private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        var row = CustomFolders.FirstOrDefault(f => f.Path == path);
        if (row != null) CustomFolders.Remove(row);
        RefreshCustomFoldersUi();

        if (_hub?.Settings is { } s)
        {
            s.SandboxCustomFolders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private void OnClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxClipboard = tag switch
        {
            "Read" => SandboxClipboardMode.Read,
            "Write" => SandboxClipboardMode.Write,
            "Both" => SandboxClipboardMode.Both,
            _ => SandboxClipboardMode.None,
        };
        Save();
    }

    private void OnTimeoutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        var secs = (int)Math.Round(e.NewValue);
        TimeoutLabel.Text = $"Max time per command: {secs} sec";
        if (_hub?.Settings is not { } s) return;
        s.SandboxTimeoutMs = secs * 1000;
        Save();
    }

    private void OnMaxOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        var tag = (string?)((ComboBoxItem?)MaxOutputCombo.SelectedItem)?.Tag;
        if (tag != null && long.TryParse(tag, out var bytes))
        {
            s.SandboxMaxOutputBytes = bytes;
            Save();
        }
    }

    public sealed class CustomFolderRow
    {
        public string Path { get; }
        public int AccessIndex { get; set; }

        /// <summary>
        /// Tracks whether the ComboBox's SelectedIndex-binding has already fired
        /// its initial SelectionChanged event during materialization. Used by
        /// OnCustomFolderAccessChanged to skip that "fake" change.
        /// </summary>
        public bool InitialSelectionFired { get; set; }

        public CustomFolderRow(string path, SandboxFolderAccess access)
        {
            Path = path;
            AccessIndex = access switch
            {
                SandboxFolderAccess.ReadWrite => 2,
                SandboxFolderAccess.ReadOnly => 1,
                _ => 0,
            };
        }
    }
}
