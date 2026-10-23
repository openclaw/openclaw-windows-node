using System.Text.RegularExpressions;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// A user-typed Hugging Face model location: <c>owner/repo</c>, <c>owner/repo:QUANT</c>,
/// <c>owner/repo/path/file.gguf</c>, or a huggingface.co / hf.co link. Resolution to an
/// immutable revision and file happens against the Hub API, never here.
/// </summary>
internal sealed partial record HuggingFaceModelReference(
    string RepositoryId,
    string Revision,
    string? FilePath,
    string? Quantization)
{
    private const string Usage = "Use owner/repo, owner/repo:Q4_K_M, or a huggingface.co link.";

    public static bool TryParse(string? input, out HuggingFaceModelReference? reference, out string error)
    {
        reference = null;
        error = string.Empty;
        string text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Enter a Hugging Face model id.";
            return false;
        }

        if (text.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("www.huggingface.co/", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;

        return text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? TryParseLink(text, out reference, out error)
            : TryParseId(text, out reference, out error);
    }

    private static bool TryParseLink(string text, out HuggingFaceModelReference? reference, out string error)
    {
        reference = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
            uri.Host.ToLowerInvariant() is not ("huggingface.co" or "www.huggingface.co" or "hf.co"))
        {
            error = "Only huggingface.co links are supported. " + Usage;
            return false;
        }

        string[] segments = Uri.UnescapeDataString(uri.AbsolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            error = "The link does not name a model repository. " + Usage;
            return false;
        }

        string repository = $"{segments[0]}/{segments[1]}";
        if (segments.Length == 2)
            return TryCreate(repository, "main", null, null, out reference, out error);

        string kind = segments[2];
        if (kind == "tree" && segments.Length == 4)
            return TryCreate(repository, segments[3], null, null, out reference, out error);
        if (kind is "blob" or "resolve" && segments.Length >= 5)
            return TryCreate(repository, segments[3], string.Join('/', segments[4..]), null, out reference, out error);

        error = "The link must point to a model repository, a revision, or a GGUF file. " + Usage;
        return false;
    }

    private static bool TryParseId(string text, out HuggingFaceModelReference? reference, out string error)
    {
        reference = null;
        string? quantization = null;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            quantization = text[(colon + 1)..];
            text = text[..colon];
            if (!QuantizationPattern().IsMatch(quantization))
            {
                error = "The quantization after ':' may contain only letters, digits, '.', '_' and '-'. " + Usage;
                return false;
            }
        }

        string[] segments = text.Split('/');
        if (segments.Length < 2)
        {
            error = "A Hugging Face model id has the form owner/repo. " + Usage;
            return false;
        }
        string repository = $"{segments[0]}/{segments[1]}";
        string? path = segments.Length > 2 ? string.Join('/', segments[2..]) : null;
        if (path is not null && quantization is not null)
        {
            error = "Choose either a file path or a quantization, not both. " + Usage;
            return false;
        }
        return TryCreate(repository, "main", path, quantization, out reference, out error);
    }

    private static bool TryCreate(
        string repository,
        string revision,
        string? path,
        string? quantization,
        out HuggingFaceModelReference? reference,
        out string error)
    {
        reference = null;
        try
        {
            PinnedArtifactValidation.RequireRepositoryId(repository, nameof(repository));
        }
        catch (ArgumentException)
        {
            error = "The repository name is not a valid Hugging Face owner/repo id. " + Usage;
            return false;
        }
        if (!RevisionPattern().IsMatch(revision))
        {
            error = "The revision in the link is not valid. " + Usage;
            return false;
        }
        if (path is not null)
        {
            try
            {
                _ = PinnedArtifactValidation.EscapeRelativePath(path);
            }
            catch (ArgumentException)
            {
                error = "The file path is not valid. " + Usage;
                return false;
            }
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only GGUF model files are supported. " + Usage;
                return false;
            }
        }
        reference = new HuggingFaceModelReference(repository, revision, path, quantization);
        error = string.Empty;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex QuantizationPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex RevisionPattern();
}
