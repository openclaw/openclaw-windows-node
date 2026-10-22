using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests;

internal static class GatewayRegistryFile
{
    internal static JsonDocument Read(string dataDir)
    {
        var path = Path.Combine(dataDir, "gateways.json");
        // Join the production writer lease: an uncoordinated Windows read can
        // collide with atomic replacement or prevent the writer from replacing.
        using var lease = PersistenceFileLease.Acquire(path);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
