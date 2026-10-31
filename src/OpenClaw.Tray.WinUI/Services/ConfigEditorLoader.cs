using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal readonly record struct ConfigEditorLoadTicket(int Generation, object Client, long? Epoch, int ProfileRevision)
{
    public bool MatchesConnection(object? client, long? epoch, int profileRevision) =>
        ReferenceEquals(Client, client) && Epoch == epoch && ProfileRevision == profileRevision;

    public bool IsCurrent(bool observing, int generation, object? client, long? epoch, int profileRevision, bool ready) =>
        observing && ready && Generation == generation && MatchesConnection(client, epoch, profileRevision);
}

internal sealed record ConfigEditorLoadResult(JsonElement? Config, JsonElement? Schema)
{
    public bool Succeeded => Config.HasValue && Schema.HasValue;
}

/// <summary>
/// Reads one response-aware pair. The page retains snapshot/draft ownership;
/// this transport adapter has no cache and never logs response or error bodies.
/// </summary>
internal static class ConfigEditorLoader
{
    internal const int DefaultTimeoutMilliseconds = 15_000;

    internal static async Task<ConfigEditorLoadResult> LoadAsync(
        Func<string, int, Task<JsonElement>> request)
    {
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_LOAD_TIMEOUT_MS"), out var configured)
            && configured > 0 ? configured : DefaultTimeoutMilliseconds;
        var config = ReadAsync("config.get", request, timeout);
        var schema = ReadAsync("config.schema", request, timeout);
        await Task.WhenAll(config, schema).ConfigureAwait(false);
        return new ConfigEditorLoadResult(await config.ConfigureAwait(false), await schema.ConfigureAwait(false));
    }

    private static async Task<JsonElement?> ReadAsync(string method, Func<string, int, Task<JsonElement>> request, int timeout)
    {
        try
        {
            var response = await request(method, timeout).ConfigureAwait(false);
            if (response.ValueKind != JsonValueKind.Object)
                return null;
            if (method == "config.get")
            {
                if (response.TryGetProperty("valid", out var valid) && valid.ValueKind != JsonValueKind.True)
                    return null;
                if (ConfigEditorModel.ExtractConfigRoot(response).ValueKind != JsonValueKind.Object)
                    return null;
            }
            else
            {
                var root = response.TryGetProperty("schema", out var nested) ? nested : response;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                    return null;
            }
            return response.Clone();
        }
        catch (Exception)
        {
            // Remote errors can contain configuration values. The page uses
            // localized load-failure copy, not exception messages or payloads.
            return null;
        }
    }
}
