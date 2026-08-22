using OpenClaw.Shared.Inference.Catalog;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine;

internal enum HuggingFaceModelInstallDisposition
{
    Downloaded,
    ReusedVerified,
}

internal sealed record HuggingFaceModelInstallProgress(long CompletedBytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1)
        : 0;
}

internal sealed record HuggingFaceModelInstallResult(
    string ModelPath,
    HuggingFaceModelInstallDisposition Disposition,
    bool CreatedThisRun);

internal class HuggingFaceModelInstallException : Exception
{
    public HuggingFaceModelInstallException(string message)
        : base(message)
    {
    }

    public HuggingFaceModelInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class TransientHuggingFaceModelInstallException : HuggingFaceModelInstallException
{
    public TransientHuggingFaceModelInstallException(string message)
        : base(message)
    {
    }
}

internal interface IHuggingFaceModelAcquirer
{
    Task<HuggingFaceModelInstallResult> InstallAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken);

    void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install);

    void RemovePartialModel(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model);
}

/// <summary>
/// Downloads one immutable Hugging Face GGUF, verifies its exact byte count and
/// SHA-256 digest, and atomically promotes it beside its partial file. A partial
/// left by process termination is resumed with an HTTP range request. Any
/// observed setup failure or cancellation removes the partial file.
/// </summary>
internal sealed class HuggingFaceModelInstaller : IHuggingFaceModelAcquirer
{
    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalBytes = 4 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private const int MaximumDownloadAttempts = 4;

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    public HuggingFaceModelInstaller(HttpClient httpClient) =>
        (_httpClient, _retryDelay) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)), Task.Delay);

    internal HuggingFaceModelInstaller(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> retryDelay) =>
        (_httpClient, _retryDelay) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
             retryDelay ?? throw new ArgumentNullException(nameof(retryDelay)));

    public event EventHandler<HuggingFaceModelInstallProgress>? ProgressChanged;

    public async Task<HuggingFaceModelInstallResult> InstallAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Weights.Role != ArtifactRole.ModelWeights ||
            model.Weights.Source is not HuggingFaceRevisionSource source)
        {
            throw new HuggingFaceModelInstallException(
                "The Local AI model must be an immutable Hugging Face weights artifact.");
        }

        if (!LocalAiPathPolicy.TryResolve(localDataDirectory, component, out LocalAiSetupPaths paths, out string pathError) ||
            !LocalAiPathPolicy.TryGetModelPaths(
                paths,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out string modelPath,
                out string partialPath,
                out pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        if (Directory.Exists(modelPath))
            throw new HuggingFaceModelInstallException("The managed Local AI model path is an existing directory.");
        if (Directory.Exists(partialPath))
            throw new HuggingFaceModelInstallException("The managed Local AI partial model path is an existing directory.");

        if (File.Exists(modelPath))
        {
            if (await VerifyFileAsync(modelPath, model.Weights, cancellationToken).ConfigureAwait(false))
            {
                return new HuggingFaceModelInstallResult(
                    modelPath,
                    HuggingFaceModelInstallDisposition.ReusedVerified,
                    CreatedThisRun: false);
            }

            if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    modelPath,
                    out string invalidModelPath,
                    out pathError))
            {
                throw new HuggingFaceModelInstallException(pathError);
            }
            File.Delete(invalidModelPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var promoted = false;
        var preservePartial = false;
        try
        {
            bool verifiedCompletePartial = File.Exists(partialPath) &&
                new FileInfo(partialPath).Length == model.Weights.SizeBytes &&
                await VerifyFileAsync(partialPath, model.Weights, cancellationToken).ConfigureAwait(false);
            if (!verifiedCompletePartial)
            {
                if (File.Exists(partialPath) &&
                    new FileInfo(partialPath).Length >= model.Weights.SizeBytes)
                {
                    TryDeletePartial(localDataDirectory, partialPath);
                }

                await DownloadAndVerifyAsync(
                        model.Weights,
                        localDataDirectory,
                        partialPath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!LocalAiPathPolicy.TryResolve(
                    localDataDirectory,
                    component,
                    out LocalAiSetupPaths revalidatedPaths,
                    out pathError) ||
                !LocalAiPathPolicy.TryGetModelPaths(
                    revalidatedPaths,
                    source.RepositoryId,
                    source.RevisionSha,
                    model.Weights.RelativePath,
                    out string revalidatedModelPath,
                    out string revalidatedPartialPath,
                    out pathError) ||
                !string.Equals(modelPath, revalidatedModelPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(partialPath, revalidatedPartialPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new HuggingFaceModelInstallException(
                    string.IsNullOrWhiteSpace(pathError)
                        ? "The Local AI model paths changed before promotion."
                        : pathError);
            }

            if (File.Exists(modelPath))
            {
                throw new HuggingFaceModelInstallException(
                    "The Local AI model target appeared while the download was in progress.");
            }

            File.Move(partialPath, modelPath);
            promoted = true;
            return new HuggingFaceModelInstallResult(
                modelPath,
                HuggingFaceModelInstallDisposition.Downloaded,
                CreatedThisRun: true);
        }
        catch (OperationCanceledException)
        {
            preservePartial = File.Exists(partialPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException)
        {
            preservePartial = File.Exists(partialPath);
            throw;
        }
        finally
        {
            if (!promoted && !preservePartial)
                TryDeletePartial(localDataDirectory, partialPath);
        }
    }

    public void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (!install.CreatedThisRun)
            return;

        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                localDataDirectory,
                install.ModelPath,
                out string deletePath,
                out string error))
        {
            throw new InvalidDataException(error);
        }

        if (File.Exists(deletePath))
            File.Delete(deletePath);
    }

    public void RemovePartialModel(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Weights.Source is not HuggingFaceRevisionSource source)
            throw new InvalidDataException("The Local AI model does not have immutable Hugging Face provenance.");
        if (!LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths paths,
                out string error) ||
            !LocalAiPathPolicy.TryGetModelPaths(
                paths,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out _,
                out string partialPath,
                out error))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error) ? "The Local AI partial model path is invalid." : error);
        }

        if (Directory.Exists(partialPath))
            throw new InvalidDataException("The Local AI partial model path is an existing directory.");
        if (File.Exists(partialPath))
        {
            if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    partialPath,
                    out string deletePath,
                    out error))
            {
                throw new InvalidDataException(error);
            }
            File.Delete(deletePath);
        }
    }

    private async Task DownloadAndVerifyAsync(
        PinnedArtifact artifact,
        string localDataDirectory,
        string partialPath,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAndVerifyAttemptAsync(
                        artifact,
                        localDataDirectory,
                        partialPath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException &&
                attempt < MaximumDownloadAttempts &&
                !cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.FromSeconds(1 << (attempt - 1));
                await _retryDelay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadAndVerifyAttemptAsync(
        PinnedArtifact artifact,
        string localDataDirectory,
        string partialPath,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        long resumeOffset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (resumeOffset < 0 || resumeOffset >= artifact.SizeBytes)
        {
            TryDeletePartial(localDataDirectory, partialPath);
            resumeOffset = 0;
        }

        using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                artifact.DownloadUri,
                resumeOffset,
                cancellationToken)
            .ConfigureAwait(false);

        bool append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeOffset > 0 && !append && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face range request failed with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }
        if (resumeOffset == 0 && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face download failed with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (append)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.From != resumeOffset || range.To is null || range.Length != artifact.SizeBytes)
            {
                throw new HuggingFaceModelInstallException(
                    "The Hugging Face range response did not match the partial model file.");
            }
        }
        else
        {
            resumeOffset = 0;
        }

        long expectedBodyBytes = artifact.SizeBytes - resumeOffset;
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBodyBytes)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face response declared {contentLength} bytes; expected {expectedBodyBytes} bytes.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (append)
            await HashExistingPartialAsync(partialPath, hash, cancellationToken).ConfigureAwait(false);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

        long completed = resumeOffset;
        long lastReported = completed;
        Report(progress, completed, artifact.SizeBytes);
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            completed += read;
            if (completed > artifact.SizeBytes)
                throw new HuggingFaceModelInstallException("The Hugging Face response exceeded the pinned model size.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (completed - lastReported >= ProgressIntervalBytes)
            {
                Report(progress, completed, artifact.SizeBytes);
                lastReported = completed;
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        if (completed != artifact.SizeBytes)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face response contained {completed} bytes; expected {artifact.SizeBytes} bytes.");
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(artifact.Sha256.Value)))
        {
            throw new HuggingFaceModelInstallException("The Hugging Face model SHA-256 digest did not match its pin.");
        }

        Report(progress, completed, artifact.SizeBytes);
    }

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        Uri initialUri,
        long resumeOffset,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUri(initialUri, initialRequest: true);
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (resumeOffset > 0)
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            Uri observed = response.RequestMessage?.RequestUri ?? current;
            ValidateDownloadUri(observed, initialRequest: false);
            if (!IsRedirect(response.StatusCode))
            {
                if (IsTransientStatus(response.StatusCode))
                {
                    int statusCode = (int)response.StatusCode;
                    string reason = response.StatusCode.ToString();
                    response.Dispose();
                    throw new TransientHuggingFaceModelInstallException(
                        $"The Hugging Face download returned transient HTTP status {statusCode} ({reason}).");
                }

                return response;
            }

            if (redirect == MaximumRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(observed, response.Headers.Location);
            response.Dispose();
            ValidateDownloadUri(next, initialRequest: false);
            current = next;
        }

        throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
    }

    private static void ValidateDownloadUri(Uri uri, bool initialRequest)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new HuggingFaceModelInstallException("The model download URI must be credential-free HTTPS.");
        }

        bool allowed = string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            (!initialRequest &&
             (uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
            throw new HuggingFaceModelInstallException("The model download redirected to an untrusted host.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is >= 500 and <= 599;

    private static async Task HashExistingPartialAsync(
        string partialPath,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            partialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            hash.AppendData(buffer, 0, read);
        }
    }

    internal static async Task<bool> VerifyFileAsync(
        string path,
        PinnedArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != artifact.SizeBytes)
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(artifact.Sha256.Value));
    }

    private void Report(
        IProgress<HuggingFaceModelInstallProgress>? progress,
        long completed,
        long total)
    {
        var value = new HuggingFaceModelInstallProgress(completed, total);
        progress?.Report(value);
        ProgressChanged?.Invoke(this, value);
    }

    private static void TryDeletePartial(string localDataDirectory, string partialPath)
    {
        try
        {
            if (LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    partialPath,
                    out string deletePath,
                    out _) &&
                File.Exists(deletePath))
            {
                File.Delete(deletePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not mask the acquisition result.
        }
    }
}
