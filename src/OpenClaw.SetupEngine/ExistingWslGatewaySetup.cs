using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>Pins reopened onboarding and workspace finalization to the same managed WSL installation.</summary>
public sealed class ExistingWslGatewaySetup
{
    public GatewayRecord Record { get; }
    public string Binding { get; }
    private (string Distro, string? Url, int Port)? _original;

    public ExistingWslGatewaySetup(GatewayRecord record)
    {
        if (LocalGatewaySettings.Classify(record) != LocalGatewayKind.Wsl)
            throw new InvalidOperationException("Reopened WSL onboarding requires a managed WSL Gateway.");
        Record = record;
        Binding = GatewayDashboardBinding.Capture(record);
    }

    public void Apply(SetupConfig config)
    {
        _original ??= (config.DistroName, config.GatewayUrl, config.GatewayPort);
        config.DistroName = GatewayRecordEditing.ResolveManagedDistroName(Record)!;
        config.GatewayUrl = Record.Url;
        // A Tailscale endpoint's HTTPS port is not the WSL service port.
        if (GatewayRecordEditing.IsLoopbackEndpoint(Record.Url))
            config.GatewayPort = new Uri(Record.Url).Port;
    }

    public void Restore(SetupConfig config)
    {
        if (_original is not { } original) return;
        config.DistroName = original.Distro;
        config.GatewayUrl = original.Url;
        config.GatewayPort = original.Port;
    }

    public void RequireCurrent(GatewayRecord? current)
    {
        if (current?.Id != Record.Id || GatewayDashboardBinding.Capture(current) != Binding)
            throw new InvalidOperationException("The Gateway selected for onboarding changed. Reopen onboarding for the selected Gateway.");
    }
}
