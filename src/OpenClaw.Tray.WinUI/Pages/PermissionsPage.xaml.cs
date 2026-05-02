using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private HubWindow? _hub;
    private List<ExecPolicyRule> _policyRules = new();

    public PermissionsPage() { InitializeComponent(); }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadExecPolicy();
        LoadPermissions();
        LoadAllowlist(hub.LastConfig);
    }

    // ── Exec Policy ──────────────────────────────────────────────────

    private void LoadExecPolicy()
    {
        try
        {
            var policyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");

            if (File.Exists(policyPath))
            {
                var json = File.ReadAllText(policyPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("defaultAction", out var da))
                {
                    var action = da.GetString() ?? "deny";
                    for (int i = 0; i < DefaultActionCombo.Items.Count; i++)
                    {
                        if (DefaultActionCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == action)
                        { DefaultActionCombo.SelectedIndex = i; break; }
                    }
                }

                _policyRules.Clear();
                if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var rule in rules.EnumerateArray())
                    {
                        _policyRules.Add(new ExecPolicyRule
                        {
                            Pattern = rule.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "",
                            Action = rule.TryGetProperty("action", out var a) ? a.GetString() ?? "deny" : "deny",
                            Index = idx++
                        });
                    }
                }

                RefreshPolicyRulesList();
            }
            else
            {
                DefaultActionCombo.SelectedIndex = 0; // deny
            }
        }
        catch { DefaultActionCombo.SelectedIndex = 0; }
    }

    private void RefreshPolicyRulesList()
    {
        for (int i = 0; i < _policyRules.Count; i++) _policyRules[i].Index = i;
        PolicyRulesList.ItemsSource = null;
        PolicyRulesList.ItemsSource = _policyRules.Select(r => new
        {
            r.Pattern,
            r.Action,
            r.Index,
            ActionBrush = new SolidColorBrush(r.Action == "allow"
                ? global::Windows.UI.Color.FromArgb(255, 34, 139, 34)
                : global::Windows.UI.Color.FromArgb(255, 220, 53, 69))
        }).ToList();
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var pattern = NewRulePattern.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        var action = (NewRuleAction.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "deny";
        _policyRules.Add(new ExecPolicyRule { Pattern = pattern, Action = action });
        NewRulePattern.Text = "";
        RefreshPolicyRulesList();
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index < _policyRules.Count)
        {
            _policyRules.RemoveAt(index);
            RefreshPolicyRulesList();
        }
    }

    private void OnDefaultActionChanged(object sender, SelectionChangedEventArgs e) { }

    private void OnSaveExecPolicy(object sender, RoutedEventArgs e)
    {
        try
        {
            var policyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");

            var defaultAction = (DefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deny";
            var policy = new
            {
                defaultAction,
                rules = _policyRules.Select(r => new { r.Pattern, action = r.Action }).ToArray()
            };

            var json = JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
            File.WriteAllText(policyPath, json);

            if (sender is Button btn)
            {
                btn.Content = "✓ Saved";
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Tick += (t, a) => { btn.Content = "Save Exec Policy"; timer.Stop(); };
                timer.Start();
            }
        }
        catch { }
    }

    // ── Node Allowlist ───────────────────────────────────────────────

    private void LoadAllowlist(JsonElement? config)
    {
        if (!config.HasValue)
        {
            AllowlistEmpty.Visibility = Visibility.Visible;
            return;
        }

        UpdateAllowlist(config.Value);
    }

    public void UpdateAllowlist(JsonElement config)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var commands = new List<string>();

                // Parse gateway.nodes.allowCommands from config
                if (config.TryGetProperty("gateway", out var gw) &&
                    gw.TryGetProperty("nodes", out var nodes) &&
                    nodes.TryGetProperty("allowCommands", out var ac) &&
                    ac.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cmd in ac.EnumerateArray())
                    {
                        var s = cmd.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) commands.Add(s);
                    }
                }

                if (commands.Count == 0)
                {
                    AllowlistEmpty.Text = "No allowed commands configured in gateway.";
                    AllowlistEmpty.Visibility = Visibility.Visible;
                    AllowlistRepeater.ItemsSource = null;
                    return;
                }

                AllowlistEmpty.Visibility = Visibility.Collapsed;
                AllowlistRepeater.ItemsSource = commands.Select(cmd => CreateAllowlistTag(cmd)).ToList();
            }
            catch
            {
                AllowlistEmpty.Text = "Failed to parse allowlist from config.";
                AllowlistEmpty.Visibility = Visibility.Visible;
            }
        });
    }

    private static Border CreateAllowlistTag(string command)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = command,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
            }
        };
    }

    // ── System Permissions ───────────────────────────────────────────

    private void LoadPermissions()
    {
        ScreenPermStatus.Text = "✅ Available";
        CameraPermStatus.Text = "ℹ️ Check Privacy Settings";
        MicPermStatus.Text = "ℹ️ Check Privacy Settings";
    }

    private void OnOpenPrivacySettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }); }
        catch { }
    }

    // ── Types ────────────────────────────────────────────────────────

    private class ExecPolicyRule
    {
        public string Pattern { get; set; } = "";
        public string Action { get; set; } = "deny";
        public int Index { get; set; }
    }
}
