using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection;

/// <summary>Stable endpoint ownership across app restarts, without transferring credentials.</summary>
public static class GatewayDashboardBinding
{
    public static string Capture(GatewayRecord record)
    {
        var endpoint = JsonSerializer.Serialize(new
        {
            record.Id, record.Url, record.IsLocal, record.SetupManagedDistroName,
            EffectiveEndpoint = GatewayClientEndpointResolver.Resolve(record),
            SshUser = record.SshTunnel?.User, SshHost = record.SshTunnel?.Host,
            SshPort = record.SshTunnel?.SshPort, RemotePort = record.SshTunnel?.RemotePort,
        });
        if (record.NativePackageFamilyName is { } family)
            endpoint += "\n" + family;
        if (record.NativeRuntimeContract is { } contract)
            endpoint += "\nruntime:" + contract;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
    }
}
