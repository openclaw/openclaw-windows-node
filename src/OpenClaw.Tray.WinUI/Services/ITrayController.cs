using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal interface ITrayController : IDisposable
{
    void Initialize();
    void BeginShutdown();
    void ShowMenu();
    void RefreshIcon();
    void ApplyConnectionState(ConnectionStatus status, OverallConnectionState? overallState);
    void HideMenu();
    void ApplyTheme();
    void CloseMenuForShutdown();
}
