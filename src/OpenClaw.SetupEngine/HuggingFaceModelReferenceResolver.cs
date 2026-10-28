using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Resolves a user-typed Hugging Face reference to an immutable, content-pinned GGUF:
/// the commit revision, the exact file, its LFS SHA-256 and size, and the attention
/// geometry read from the file header. Nothing is downloaded beyond the GGUF header.
/// </summary>
internal sealed partial class HuggingFaceModelReferenceResolver(HttpClient client)
{
    private const int MaximumTreePages = 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private sealed record LfsFile(string Path, string Sha256, long SizeBytes);

    public async Task<LocalCustomModelRecipe> ResolveAsync(string input, CancellationToken ct)
    {
        if (!HuggingFaceModelReference.TryParse(input, out HuggingFaceModelReference? reference, out string error))
            throw new LocalAiModelSwitchException(error);
        try
        {
            string sha = await ResolveRevisionAsync(reference!, ct).ConfigureAwait(false);
            IReadOnlyList<LfsFile> files = await ListLfsFilesAsync(reference!.RepositoryId, sha, ct).ConfigureAwait(false);
            LfsFile file = SelectFile(reference, files);
            GgufModelMetadata metadata = await ReadHeaderAsync(reference.RepositoryId, sha, file.Path, ct).ConfigureAwait(false);
            var recipe = new LocalCustomModelRecipe(
                reference.RepositoryId,
                sha,
                file.Path,
                file.SizeBytes,
                file.Sha256,
                Path.GetFileNameWithoutExtension(file.Path),
                metadata.Architecture.ToLowerInvariant(),
                (int)Math.Min(metadata.BlockCount, int.MaxValue),
                (int)Math.Min(metadata.KeyValueHeadCount, int.MaxValue),
                (int)Math.Min(metadata.KeyValueHeadDimension, int.MaxValue),
                (int)Math.Min(metadata.ContextLength ?? 8192, 1_048_576));
            try
            {
                LocalCustomModel.Validate(recipe);
            }
            catch (InvalidDataException)
            {
                throw new LocalAiModelSwitchException("This GGUF model reports a layout Local AI cannot run.");
            }
            return recipe;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new LocalAiModelSwitchException(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or HuggingFaceModelInstallException or OperationCanceledException)
        {
            string detail = ex is OperationCanceledException ? "the request timed out." : ex.Message;
            throw new LocalAiModelSwitchException($"Hugging Face could not be reached: {detail}");
        }
    }

    private async Task<string> ResolveRevisionAsync(HuggingFaceModelReference reference, CancellationToken ct)
    {
        var uri = new Uri($"https://huggingface.co/api/models/{reference.RepositoryId}/revision/{Uri.EscapeDataString(reference.Revision)}");
        using JsonDocument document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("gated", out JsonElement gated) && gated.ValueKind != JsonValueKind.False)
            throw new LocalAiModelSwitchException(
                "This model is gated on Hugging Face. Only public models without access approval are supported.");
        string? sha = root.TryGetProperty("sha", out JsonElement shaElement) && shaElement.ValueKind == JsonValueKind.String
            ? shaElement.GetString()
            : null;
        if (sha is null || sha.Length != 40 || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new LocalAiModelSwitchException("Hugging Face did not report an immutable revision for this model.");
        return sha;
    }

    private async Task<IReadOnlyList<LfsFile>> ListLfsFilesAsync(string repositoryId, string sha, CancellationToken ct)
    {
        var files = new List<LfsFile>();
        Uri? next = new($"https://huggingface.co/api/models/{repositoryId}/tree/{sha}?recursive=true");
        for (int page = 0; next is not null && page < MaximumTreePages; page++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await SendAsync(next, timeout.Token).ConfigureAwait(false);
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new LocalAiModelSwitchException("Hugging Face returned an unreadable file list.");
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out JsonElement type) && type.GetString() == "file" &&
                    entry.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String &&
                    entry.TryGetProperty("lfs", out JsonElement lfs) && lfs.ValueKind == JsonValueKind.Object &&
                    lfs.TryGetProperty("oid", out JsonElement oid) && oid.ValueKind == JsonValueKind.String &&
                    lfs.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long sizeBytes))
                {
                    files.Add(new LfsFile(path.GetString()!, oid.GetString()!.ToLowerInvariant(), sizeBytes));
                }
                else if (entry.TryGetProperty("path", out JsonElement plain) && plain.ValueKind == JsonValueKind.String &&
                    plain.GetString()!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(new LfsFile(plain.GetString()!, string.Empty, 0));
                }
            }
            next = NextPage(response);
        }
        return files;
    }

    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
            return null;
        foreach (string header in values)
        {
            Match match = NextLinkPattern().Match(header);
            if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase))
                return uri;
        }
        return null;
    }

    private static LfsFile SelectFile(HuggingFaceModelReference reference, IReadOnlyList<LfsFile> files)
    {
        if (reference.FilePath is { } explicitPath)
        {
            LfsFile file = files.FirstOrDefault(f => string.Equals(f.Path, explicitPath, StringComparison.Ordinal))
                ?? throw new LocalAiModelSwitchException("That file was not found in the repository.");
            if (SplitPattern().IsMatch(file.Path))
                throw new LocalAiModelSwitchException("Split GGUF models are not supported. Choose a single-file quantization.");
            return RequireLfs(file);
        }

        LfsFile[] gguf = files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(f.Path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        LfsFile[] candidates = gguf.Where(f => !SplitPattern().IsMatch(f.Path)).ToArray();
        if (candidates.Length == 0)
        {
            throw new LocalAiModelSwitchException(gguf.Length > 0
                ? "Split GGUF models are not supported. Choose a single-file quantization."
                : "This repository has no single-file GGUF model.");
        }

        if (reference.Quantization is { } quantization)
        {
            LfsFile[] matches = MatchQuantization(candidates, quantization);
            if (matches.Length == 0)
            {
                string available = string.Join(", ", candidates
                    .Select(f => LocalCustomModel.QuantizationFromFileName(f.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                throw new LocalAiModelSwitchException(
                    $"No GGUF file in this repository matches {quantization}. Available: {available}.");
            }
            return RequireLfs(matches.OrderBy(f => f.Path.Length).First());
        }

        if (candidates.Length == 1)
            return RequireLfs(candidates[0]);
        LfsFile[] defaults = MatchQuantization(candidates, "Q4_K_M");
        if (defaults.Length > 0)
            return RequireLfs(defaults.OrderBy(f => f.Path.Length).First());
        throw new LocalAiModelSwitchException(
            $"This repository has several GGUF files. Add a quantization, for example {reference.RepositoryId}:Q4_K_M.");
    }

    private static LfsFile[] MatchQuantization(IEnumerable<LfsFile> candidates, string quantization)
    {
        var pattern = new Regex($"(^|[-_.]){Regex.Escape(quantization)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return candidates.Where(f => pattern.IsMatch(Path.GetFileNameWithoutExtension(f.Path))).ToArray();
    }

    private static LfsFile RequireLfs(LfsFile file) =>
        file.SizeBytes > 0 && file.Sha256.Length == 64
            ? file
            : throw new LocalAiModelSwitchException("This model file is not stored as a Large File, so it cannot be verified.");

    private async Task<GgufModelMetadata> ReadHeaderAsync(string repositoryId, string sha, string path, CancellationToken ct)
    {
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using HttpResponseMessage response = await SendAsync(
            new Uri($"https://huggingface.co/{repositoryId}/resolve/{sha}/{escaped}"), timeout.Token).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await GgufMetadataReader.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using HttpResponseMessage response = await SendAsync(uri, timeout.Token).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        HttpResponseMessage response = await HuggingFaceHttp.SendWithValidatedRedirectsAsync(client, uri, 0, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new LocalAiModelSwitchException(
                "This Hugging Face model was not found or needs sign-in. Only public models are supported.");
        }
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"HTTP status {status}.");
        }
        return response;
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPattern();

    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase)]
    private static partial Regex NextLinkPattern();
}
