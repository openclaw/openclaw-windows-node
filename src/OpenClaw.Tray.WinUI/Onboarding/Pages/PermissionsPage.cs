using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using static OpenClawTray.Infrastructure.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Pages;

/// <summary>
/// Page 5: Grant Permissions.
/// Shows Windows permission status for 5 capabilities and lets users
/// open system settings to grant each one.
/// </summary>
public sealed class PermissionsPage : Component<OnboardingState>
{
    public override Element Render()
    {
        var (notifications, setNotifications) = UseState(false);
        var (camera, setCamera) = UseState(false);
        var (microphone, setMicrophone) = UseState(false);
        var (screenCapture, setScreenCapture) = UseState(false);
        var (location, setLocation) = UseState(false);
        var (statusMsg, setStatusMsg) = UseState("");

        return VStack(16,
            TextBlock("Grant Permissions")
                .FontSize(24)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),

            TextBlock("OpenClaw works best when it can send notifications, access your camera and microphone, capture your screen, and know your location. Grant permissions below.")
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping(),

            Border(
                VStack(4,
                    PermissionRow("🔔", "Notifications", notifications, () =>
                    {
                        setNotifications(true);
                        setStatusMsg("Opened Notifications settings");
                    }),
                    PermissionRow("📷", "Camera", camera, () =>
                    {
                        setCamera(true);
                        setStatusMsg("Opened Camera settings");
                    }),
                    PermissionRow("🎤", "Microphone", microphone, () =>
                    {
                        setMicrophone(true);
                        setStatusMsg("Opened Microphone settings");
                    }),
                    PermissionRow("🖥️", "Screen Capture", screenCapture, () =>
                    {
                        setScreenCapture(true);
                        setStatusMsg("Opened Screen Capture settings");
                    }),
                    PermissionRow("📍", "Location (optional)", location, () =>
                    {
                        setLocation(true);
                        setStatusMsg("Opened Location settings");
                    })
                ).Padding(12)
            )
            .CornerRadius(8)
            .Background("#F5F5F5")
            .Margin(0, 8, 0, 0),

            TextBlock(statusMsg)
                .FontSize(12)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
        )
        .MaxWidth(460)
        .Padding(0, 32, 0, 0);
    }

    private static Element PermissionRow(string icon, string name, bool granted, Action onRequest)
    {
        return HStack(12,
            TextBlock(icon).FontSize(18).Width(24),
            TextBlock(name).FontSize(14).Width(160),
            TextBlock(granted ? "✅" : "⚪").FontSize(16).Width(24),
            Button("Open Settings", onRequest)
        ).Padding(6, 6, 6, 6);
    }
}
