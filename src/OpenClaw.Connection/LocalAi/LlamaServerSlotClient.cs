// Loopback client for llama-server's slot save/restore endpoints, addressed through the
// router: the router only routes /slots calls that name the model in the JSON body, not in
// the query. A non-success status is a cache miss (false) and is logged by status code only;
// transport failures propagate to the caller, which treats them the same way.
using System.Text;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClaw.Connection.LocalAi;

internal sealed class LlamaServerSlotClient
{
    private readonly HttpClient _http;
    private readonly Uri _upstream;
    private readonly IOpenClawLogger _logger;

    public LlamaServerSlotClient(HttpClient http, Uri upstream, IOpenClawLogger logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> SaveAsync(
        string model, int slotId, string fileName, string? authorization, CancellationToken cancellationToken) =>
        PostAsync(
            $"/slots/{slotId}?action=save",
            new JsonObject { ["model"] = model, ["filename"] = fileName },
            authorization,
            cancellationToken);

    public Task<bool> RestoreAsync(
        string model, int slotId, string fileName, string? authorization, CancellationToken cancellationToken) =>
        PostAsync(
            $"/slots/{slotId}?action=restore",
            new JsonObject { ["model"] = model, ["filename"] = fileName },
            authorization,
            cancellationToken);

    /// <summary>Runs a chat request to completion so its prompt is resident in the slot.</summary>
    public Task<bool> WarmAsync(JsonObject body, string? authorization, CancellationToken cancellationToken) =>
        PostAsync("/v1/chat/completions", body, authorization, cancellationToken);

    private async Task<bool> PostAsync(
        string pathAndQuery, JsonObject body, string? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_upstream, pathAndQuery))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn($"Local AI prefix cache call {pathAndQuery.Split('?')[0]} returned {(int)response.StatusCode}.");
            return false;
        }
        return true;
    }
}
