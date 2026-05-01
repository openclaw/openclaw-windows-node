using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Windows.System;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 5: Grant Permissions.
/// Shows real Windows permission status for 5 capabilities and lets users
/// open system settings to grant each one. Auto-refreshes when permissions change.
/// </summary>
public sealed class PermissionsPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (permissions, setPermissions) = UseState<List<PermissionChecker.PermissionResult>?>(null);
        var (refreshKey, setRefreshKey) = UseState(0);

        // Check permissions on mount and whenever refreshKey changes
        UseEffect(() =>
        {
            async void LoadPermissions()
            {
                var results = await PermissionChecker.CheckAllAsync();
                setPermissions(results);
            }
            LoadPermissions();
        }, refreshKey);

        // Subscribe to camera/mic access changes for auto-refresh
        UseEffect(() =>
        {
            var unsubscribe = PermissionChecker.SubscribeToAccessChanges(() =>
            {
                setRefreshKey(refreshKey + 1);
            });
            return unsubscribe;
        });

        async void OpenSettings(string settingsUri)
        {
            if (string.IsNullOrEmpty(settingsUri)) return;

            // SECURITY: Only allow ms-settings: URIs to prevent launching arbitrary protocols
            if (!Uri.TryCreate(settingsUri, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"[Permissions] Blocked non-settings URI: {settingsUri}");
                return;
            }

            try
            {
                var launched = await Launcher.LaunchUriAsync(uri);
                if (!launched)
                {
                    // Fallback: refresh status anyway in case it changed
                    setRefreshKey(refreshKey + 1);
                }
            }
            catch
            {
                setRefreshKey(refreshKey + 1);
            }
        }

        // Build permission rows from real data
        var rows = new List<Element>();
        if (permissions != null)
        {
            foreach (var perm in permissions)
            {
                var p = perm; // capture for closure
                rows.Add(PermissionRow(p, () =>
                {
                    OpenSettings(p.SettingsUri);
                    // Refresh after a brief delay to let user return from Settings
                    _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
                        setRefreshKey(refreshKey + 1));
                }));
            }
        }
        else
        {
            rows.Add(TextBlock(LocalizationHelper.GetString("Onboarding_Permissions_Checking"))
                .FontSize(13)
                .Opacity(0.6)
                .HAlign(HorizontalAlignment.Center));
        }

        return VStack(16,
            TextBlock(LocalizationHelper.GetString("Onboarding_Permissions_Title"))
                .FontSize(22)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock(LocalizationHelper.GetString("Onboarding_Permissions_Description"))
                .FontSize(14)
                .Opacity(0.6)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            Border(
                VStack(4,
                    VStack(4, rows.ToArray()),
                    Button($"↻ {LocalizationHelper.GetString("Onboarding_Permissions_Refresh")}", () => setRefreshKey(refreshKey + 1))
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(0, 8, 0, 0)
                ).Padding(12)
            )
            .CornerRadius(8)
            .Background("#FFFFFF")
            .Margin(0, 8, 0, 0)
        )
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }

    private static Element PermissionRow(PermissionChecker.PermissionResult perm, Action onOpenSettings)
    {
        var statusIcon = perm.Status switch
        {
            PermissionChecker.PermissionStatus.Granted => "✅",
            PermissionChecker.PermissionStatus.Supported => "✅",
            PermissionChecker.PermissionStatus.Denied => "❌",
            PermissionChecker.PermissionStatus.NoDevice => "➖",
            PermissionChecker.PermissionStatus.NotSupported => "➖",
            _ => "⚪"
        };

        // Left: icon + name/status (fills available width)
        var nameCol = HStack(8,
            TextBlock(perm.Icon).FontSize(18).Width(28),
            VStack(2,
                TextBlock(perm.Name).FontSize(14).TextWrapping(),
                TextBlock(perm.StatusLabel)
                    .FontSize(11)
                    .Opacity(0.6)
                    .TextWrapping()
            ).MinWidth(120).MaxWidth(180)
        ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0);

        // Right: emoji + button grouped as one unit so emoji always aligns
        // regardless of whether a button is present.
        // MinWidth ensures consistent column width even without a button.
        var rightSide = HStack(4,
            TextBlock(statusIcon).FontSize(16)
                .VAlign(VerticalAlignment.Center)
                .Width(30)
                .HAlign(HorizontalAlignment.Center),
            !string.IsNullOrEmpty(perm.SettingsUri)
                ? Button(LocalizationHelper.GetString("Onboarding_Permissions_OpenSettings"), onOpenSettings)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock("")
        ).VAlign(VerticalAlignment.Center).MinWidth(150).Grid(row: 0, column: 1);

        return Grid(["1*", "Auto"], ["Auto"],
            nameCol,
            rightSide
        ).Padding(6, 8, 6, 8);
    }
}
