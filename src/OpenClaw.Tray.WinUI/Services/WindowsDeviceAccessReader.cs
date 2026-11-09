// Reads Windows privacy access for the devices behind "This PC" Inbox alerts with
// AppCapability.CheckAccess, which reports the current state without ever showing a
// consent prompt, and raises Changed when Windows reports an access change. It is
// meaningful for the packaged (MSIX) app; an unpackaged process reads UserPromptRequired
// for every device, which maps to Unknown, so unpackaged builds never show these alerts.
// In Gateway fixture mode it reads only the fixture override so host privacy state never
// leaks into deterministic UI tests.
//
// Usage:
//   using var reader = WindowsDeviceAccessReader.Create();
//   var camera = reader.Read(ThisPcDevice.Camera);

using OpenClaw.Shared;
using OpenClawTray.Presentation;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace OpenClawTray.Services;

internal sealed class WindowsDeviceAccessReader : IDeviceAccessReader
{
    private readonly Dictionary<ThisPcDevice, AppCapability> _capabilities = new();

    private WindowsDeviceAccessReader()
    {
        foreach (var (device, name) in new[]
        {
            (ThisPcDevice.Camera, "webcam"),
            (ThisPcDevice.Microphone, "microphone"),
            (ThisPcDevice.Location, "location"),
        })
        {
            try
            {
                var capability = AppCapability.Create(name);
                capability.AccessChanged += OnAccessChanged;
                _capabilities[device] = capability;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThisPc] {device} access cannot be read: {ex.GetType().Name}");
            }
        }
    }

    public event EventHandler? Changed;

    /// <summary>The fixture override reader in Gateway fixture mode, otherwise the Windows reader.</summary>
    public static IDeviceAccessReader Create() =>
        GatewayFixtureIsolation.IsEnabled
            ? new FixtureDeviceAccessReader(ThisPcAlerts.ParseFixtureAccess(
                Environment.GetEnvironmentVariable(ThisPcAlerts.FixtureAccessEnvironmentVariable)))
            : new WindowsDeviceAccessReader();

    public ThisPcDeviceAccess Read(ThisPcDevice device)
    {
        if (!_capabilities.TryGetValue(device, out var capability))
            return ThisPcDeviceAccess.Unknown;
        try
        {
            var status = capability.CheckAccess();
            Logger.Info($"[ThisPc] {device} access status: {status}");
            return status switch
            {
                AppCapabilityAccessStatus.Allowed => ThisPcDeviceAccess.Allowed,
                AppCapabilityAccessStatus.DeniedByUser => ThisPcDeviceAccess.DeniedByUser,
                AppCapabilityAccessStatus.DeniedBySystem => ThisPcDeviceAccess.DeniedBySystem,
                // A pending prompt or an undeclared capability is not a block the user set.
                _ => ThisPcDeviceAccess.Unknown,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ThisPc] {device} access check failed: {ex.GetType().Name}");
            return ThisPcDeviceAccess.Unknown;
        }
    }

    public void Dispose()
    {
        foreach (var capability in _capabilities.Values)
            capability.AccessChanged -= OnAccessChanged;
        _capabilities.Clear();
    }

    private void OnAccessChanged(AppCapability sender, AppCapabilityAccessChangedEventArgs args) =>
        Changed?.Invoke(this, EventArgs.Empty);

    private sealed class FixtureDeviceAccessReader(IReadOnlyDictionary<ThisPcDevice, ThisPcDeviceAccess> access)
        : IDeviceAccessReader
    {
        public event EventHandler? Changed { add { } remove { } }

        public ThisPcDeviceAccess Read(ThisPcDevice device) =>
            access.TryGetValue(device, out var value) ? value : ThisPcDeviceAccess.Unknown;

        public void Dispose()
        {
        }
    }
}
