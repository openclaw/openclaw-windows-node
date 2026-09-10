using OpenClaw.Connection.LocalAi;
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

internal enum HuggingFaceModelInstallPhase
{
    Downloading,
    Verifying,
}

internal sealed record HuggingFaceModelInstallProgress(
    long CompletedBytes,
    long TotalBytes,
    HuggingFaceModelInstallPhase Phase = HuggingFaceModelInstallPhase.Downloading)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1)
        : 0;
}

internal sealed record HuggingFaceModelInstallResult(
    string ModelPath,
    string? CacheRoot,
    HuggingFaceModelInstallDisposition Disposition,
    bool CreatedThisRun,
    string? LegacyModelPath = null,
    bool LegacyCreatedThisRun = false);

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
/// left by process termination is resumed with an HTTP range request. Shared
/// cache artifacts and resumable partials survive rollback and cancellation.
/// </summary>
internal sealed class HuggingFaceModelInstaller : IHuggingFaceModelAcquirer
{
    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalBytes = 4 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private const int MaximumDownloadAttempts = 4;

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private readonly Func<string> _cacheRootResolver;

    public HuggingFaceModelInstaller(HttpClient httpClient) =>
        (_httpClient, _retryDelay, _cacheRootResolver) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
             Task.Delay,
             HuggingFaceHubCache.ResolveCacheRoot);

    internal HuggingFaceModelInstaller(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> retryDelay,
        Func<string>? cacheRootResolver = null) =>
        (_httpClient, _retryDelay, _cacheRootResolver) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
             retryDelay ?? throw new ArgumentNullException(nameof(retryDelay)),
             cacheRootResolver ?? HuggingFaceHubCache.ResolveCacheRoot);

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

        string cacheRoot = _cacheRootResolver();
        if (!TryValidateCacheRootOwnershipBoundary(
                localDataDirectory,
                cacheRoot,
                out string cacheRootError))
        {
            throw new HuggingFaceModelInstallException(cacheRootError);
        }
        if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                cacheRoot,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out string modelPath,
                out string partialPath,
                out string pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        if (Directory.Exists(modelPath))
            throw new HuggingFaceModelInstallException("The managed Local AI model path is an existing directory.");
        if (Directory.Exists(partialPath))
            throw new HuggingFaceModelInstallException("The managed Local AI partial model path is an existing directory.");

        var expectedSha256 = model.Weights.Sha256;
        await using (FileStream? verified =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    cacheRoot,
                    modelPath,
                    model.Weights.SizeBytes,
                    expectedSha256,
                    new VerificationProgress(this, progress, model.Weights.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (verified is not null)
            {
                (string legacyModelPath, bool legacyCreatedThisRun) =
                    await EnsureLegacyCompatibilityCopyAsync(
                            localDataDirectory,
                            component,
                            model,
                            verified,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                return new HuggingFaceModelInstallResult(
                    modelPath,
                    cacheRoot,
                    HuggingFaceModelInstallDisposition.ReusedVerified,
                    CreatedThisRun: false,
                    legacyModelPath,
                    legacyCreatedThisRun);
            }
        }

        if (PathEntryExists(modelPath))
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face cache destination '{modelPath}' is unsafe or does not match " +
                "the pinned model. Remove it manually and retry setup.");
        }

        string destinationDirectory = Path.GetDirectoryName(modelPath)
            ?? throw new HuggingFaceModelInstallException(
                "The Hugging Face cache destination has no parent directory.");
        string destinationFileName = Path.GetFileName(modelPath);
        string partialFileName = Path.GetFileName(partialPath);
        LocalAiManifestMigration.SafeCacheDirectory? directory = null;
        LocalAiManifestMigration.CacheMigrationFile? partial = null;
        bool partialExistedBeforeInstall = false;
        HuggingFaceModelInstallDisposition disposition = HuggingFaceModelInstallDisposition.Downloaded;
        try
        {
            directory = LocalAiManifestMigration.SafeCacheDirectory.OpenOrCreate(
                cacheRoot,
                destinationDirectory);
            partial = directory.TryOpenExisting(partialFileName);
            partialExistedBeforeInstall = partial is not null;

            bool partialVerified = partial is not null &&
                partial.Stream.Length == model.Weights.SizeBytes &&
                await VerifyOpenFileAsync(
                        partial.Stream,
                        model.Weights,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (partial is not null &&
                partial.Stream.Length >= model.Weights.SizeBytes &&
                !partialVerified)
            {
                partial.Stream.SetLength(0);
                partial.Stream.Position = 0;
            }

            if (!partialVerified)
            {
                partial ??= directory.CreateNew(partialFileName);
                if (partial.Stream.Length == 0 &&
                    await TryCopyVerifiedBlobAsync(
                            cacheRoot,
                            source.RepositoryId,
                            model.Weights,
                            partial.Stream,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    disposition = HuggingFaceModelInstallDisposition.ReusedVerified;
                }
                else
                {
                    await DownloadAndVerifyAsync(
                            model.Weights,
                            partial.Stream,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalAiManifestMigration.CacheMigrationFile activePartial = partial
                ?? throw new HuggingFaceModelInstallException(
                    "The Hugging Face cache partial was not created.");
            if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                    cacheRoot,
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

            if (!await VerifyOpenFileAsync(
                    activePartial.Stream,
                    model.Weights,
                    progress,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new HuggingFaceModelInstallException(
                    "The Hugging Face cache partial does not match the pinned model.");
            }

            try
            {
                activePartial.Promote(destinationFileName);
            }
            catch (IOException ex)
            {
                if (partialExistedBeforeInstall)
                    activePartial.Commit();
                activePartial.Dispose();
                partial = null;
                await using FileStream? winner =
                    await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                            cacheRoot,
                            modelPath,
                            model.Weights.SizeBytes,
                            expectedSha256,
                            new VerificationProgress(this, progress, model.Weights.SizeBytes),
                            cancellationToken)
                        .ConfigureAwait(false);
                if (winner is null)
                {
                    throw new HuggingFaceModelInstallException(
                        "The Hugging Face cache destination changed before promotion.",
                        ex);
                }

                (string legacyModelPath, bool legacyCreatedThisRun) =
                    await EnsureLegacyCompatibilityCopyAsync(
                            localDataDirectory,
                            component,
                            model,
                            winner,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                return new HuggingFaceModelInstallResult(
                    modelPath,
                    cacheRoot,
                    HuggingFaceModelInstallDisposition.ReusedVerified,
                    CreatedThisRun: false,
                    legacyModelPath,
                    legacyCreatedThisRun);
            }

            if (partialExistedBeforeInstall)
                activePartial.Commit();
            directory.RequirePromotedFile(activePartial.Stream.SafeFileHandle, destinationFileName);
            activePartial.Commit();
            (string createdLegacyModelPath, bool createdLegacyThisRun) =
                await EnsureLegacyCompatibilityCopyAsync(
                        localDataDirectory,
                        component,
                        model,
                        activePartial.Stream,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            return new HuggingFaceModelInstallResult(
                modelPath,
                cacheRoot,
                disposition,
                CreatedThisRun: true,
                createdLegacyModelPath,
                createdLegacyThisRun);
        }
        catch (OperationCanceledException)
        {
            partial?.Commit();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException)
        {
            partial?.Commit();
            throw;
        }
        catch (HuggingFaceModelInstallException) when (partialExistedBeforeInstall)
        {
            // A later run can truncate and retry a non-resumable complete partial.
            // Preserve bytes that existed before this setup transaction.
            partial?.Commit();
            throw;
        }
        finally
        {
            partial?.Dispose();
            directory?.Dispose();
        }
    }

    public void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install)
    {
        ArgumentNullException.ThrowIfNull(install);
        // Final hub-cache artifacts are shared and survive rollback/uninstall.
        if (!install.LegacyCreatedThisRun || string.IsNullOrWhiteSpace(install.LegacyModelPath))
            return;
        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                localDataDirectory,
                install.LegacyModelPath,
                out string deletePath,
                out string error))
        {
            throw new InvalidDataException(error);
        }
        File.Delete(deletePath);
    }

    public void RemovePartialModel(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(model);
        // Partials live in a shared cache and may predate this setup transaction.
        // The installer removes only a newly created invalid partial before returning.
        if (model.Weights.Source is not HuggingFaceRevisionSource source ||
            !LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths paths,
                out _) ||
            !LocalAiPathPolicy.TryGetModelPaths(
                paths,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out string legacyModelPath,
                out _,
                out _))
        {
            return;
        }
        CleanupLegacyCompatibilityPartials(localDataDirectory, legacyModelPath);
    }

    private async Task DownloadAndVerifyAsync(
        PinnedArtifact artifact,
        FileStream partial,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAndVerifyAttemptAsync(
                        artifact,
                        partial,
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
        FileStream partial,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        long resumeOffset = partial.Length;
        if (resumeOffset < 0 || resumeOffset >= artifact.SizeBytes)
        {
            partial.SetLength(0);
            resumeOffset = 0;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (resumeOffset > 0)
            await HashExistingPartialAsync(partial, hash, cancellationToken).ConfigureAwait(false);

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
            partial.SetLength(0);
            hash.GetHashAndReset();
        }

        long expectedBodyBytes = artifact.SizeBytes - resumeOffset;
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBodyBytes)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face response declared {contentLength} bytes; expected {expectedBodyBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        partial.Position = resumeOffset;

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
            await partial.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (completed - lastReported >= ProgressIntervalBytes)
            {
                Report(progress, completed, artifact.SizeBytes);
                lastReported = completed;
            }
        }

        await partial.FlushAsync(cancellationToken).ConfigureAwait(false);
        partial.Flush(flushToDisk: true);
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
        FileStream partial,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        partial.Position = 0;
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read = await partial.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                partial.Position = partial.Length;
                return;
            }
            hash.AppendData(buffer, 0, read);
        }
    }

    private async Task<bool> TryCopyVerifiedBlobAsync(
        string cacheRoot,
        string repositoryId,
        PinnedArtifact artifact,
        FileStream destination,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!HuggingFaceHubCache.TryGetBlobPath(
                cacheRoot,
                repositoryId,
                artifact.Sha256,
                out string blobPath,
                out _))
        {
            return false;
        }

        await using FileStream? source =
            await HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync(
                    cacheRoot,
                    blobPath,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    new VerificationProgress(this, progress, artifact.SizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
            return false;

        destination.SetLength(0);
        destination.Position = 0;
        var buffer = new byte[BufferSize];
        long completed = 0;
        Report(progress, completed, artifact.SizeBytes, HuggingFaceModelInstallPhase.Downloading);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            completed += read;
            Report(progress, completed, artifact.SizeBytes, HuggingFaceModelInstallPhase.Downloading);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        destination.Position = 0;
        return completed == artifact.SizeBytes;
    }

    private async Task<(string Path, bool CreatedThisRun)> EnsureLegacyCompatibilityCopyAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model,
        FileStream verifiedSource,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        HuggingFaceRevisionSource source = (HuggingFaceRevisionSource)model.Weights.Source;
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
                out string legacyModelPath,
                out _,
                out error))
        {
            throw new HuggingFaceModelInstallException(error);
        }
        CleanupLegacyCompatibilityPartials(localDataDirectory, legacyModelPath);

        if (Directory.Exists(legacyModelPath))
        {
            throw new HuggingFaceModelInstallException(
                "The legacy-compatible Local AI model path is an existing directory.");
        }
        if (File.Exists(legacyModelPath))
        {
            if (await VerifyFileAsync(legacyModelPath, model.Weights, cancellationToken)
                    .ConfigureAwait(false))
            {
                return (legacyModelPath, false);
            }

            if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    legacyModelPath,
                    out string invalidLegacyModelPath,
                    out error))
            {
                throw new HuggingFaceModelInstallException(error);
            }
            File.Delete(invalidLegacyModelPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        LocalAiManifestMigration.EnsureSufficientFreeSpace(
            Path.GetDirectoryName(legacyModelPath)!,
            model.Weights.SizeBytes);
        string temporaryPath = legacyModelPath + $".compat-{Guid.NewGuid():N}.partial";
        try
        {
            await using var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            verifiedSource.Position = 0;
            await verifiedSource.CopyToAsync(temporary, BufferSize, cancellationToken)
                .ConfigureAwait(false);
            await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
            temporary.Flush(flushToDisk: true);
            if (!await VerifyOpenFileAsync(
                    temporary,
                    model.Weights,
                    progress,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new HuggingFaceModelInstallException(
                    "The legacy-compatible Local AI model copy does not match the pinned model.");
            }

            if (!LocalAiPathPolicy.TryResolve(
                    localDataDirectory,
                    component,
                    out LocalAiSetupPaths revalidatedPaths,
                    out error) ||
                !LocalAiPathPolicy.TryGetModelPaths(
                    revalidatedPaths,
                    source.RepositoryId,
                    source.RevisionSha,
                    model.Weights.RelativePath,
                    out string revalidatedLegacyModelPath,
                    out _,
                    out error) ||
                !string.Equals(
                    legacyModelPath,
                    revalidatedLegacyModelPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new HuggingFaceModelInstallException(
                    string.IsNullOrWhiteSpace(error)
                        ? "The legacy-compatible Local AI model path changed before promotion."
                        : error);
            }
            if (PathEntryExists(legacyModelPath))
            {
                throw new HuggingFaceModelInstallException(
                    "The legacy-compatible Local AI model path appeared before promotion.");
            }

            temporary.Close();
            File.Move(temporaryPath, legacyModelPath);
            return (legacyModelPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Cleanup must not replace the actionable acquisition failure.
                }
            }
        }
    }

    private static void CleanupLegacyCompatibilityPartials(
        string localDataDirectory,
        string legacyModelPath)
    {
        string directory = Path.GetDirectoryName(legacyModelPath)!;
        if (!Directory.Exists(directory))
            return;

        string pattern = Path.GetFileName(legacyModelPath) + ".compat-*.partial";
        foreach (string candidate in Directory.EnumerateFiles(directory, pattern))
        {
            if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    candidate,
                    out string deletePath,
                    out string error))
            {
                throw new InvalidDataException(error);
            }
            File.Delete(deletePath);
        }
    }

    private async Task<bool> VerifyOpenFileAsync(
        FileStream stream,
        PinnedArtifact artifact,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (stream.Length != artifact.SizeBytes)
            return false;

        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long completed = 0;
        Report(progress, completed, artifact.SizeBytes, HuggingFaceModelInstallPhase.Verifying);
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            completed += read;
            Report(progress, completed, artifact.SizeBytes, HuggingFaceModelInstallPhase.Verifying);
        }

        stream.Position = 0;
        return completed == artifact.SizeBytes &&
            CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(artifact.Sha256.Value));
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
        long total,
        HuggingFaceModelInstallPhase phase = HuggingFaceModelInstallPhase.Downloading)
    {
        var value = new HuggingFaceModelInstallProgress(completed, total, phase);
        progress?.Report(value);
        ProgressChanged?.Invoke(this, value);
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                return new FileInfo(path).LinkTarget is not null;
            }
            catch (Exception linkException) when (
                linkException is IOException or
                    UnauthorizedAccessException or
                    NotSupportedException)
            {
                return true;
            }
        }
    }

    internal static bool TryValidateCacheRootOwnershipBoundary(
        string localDataDirectory,
        string cacheRoot,
        out string error)
    {
        string managedRoot;
        string normalizedCacheRoot;
        try
        {
            managedRoot = LocalAiManifestMigration.ResolveFinalDirectoryPathForComparison(
                new LocalAiPaths(localDataDirectory).RootDirectory);
            normalizedCacheRoot = LocalAiManifestMigration.ResolveFinalDirectoryPathForComparison(
                cacheRoot);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                IOException or
                InvalidDataException or
                NotSupportedException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            error = $"The Hugging Face cache root is invalid: {ex.Message}";
            return false;
        }

        string relative = Path.GetRelativePath(managedRoot, normalizedCacheRoot);
        bool isManagedRootOrDescendant =
            string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!Path.IsPathRooted(relative) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 $"..{Path.DirectorySeparatorChar}",
                 StringComparison.Ordinal));
        if (!isManagedRootOrDescendant)
        {
            error = "";
            return true;
        }

        error =
            $"The Hugging Face cache root '{normalizedCacheRoot}' must be outside the " +
            $"app-owned Local AI directory '{managedRoot}' so uninstall cannot remove shared cache artifacts.";
        return false;
    }

    private sealed class VerificationProgress(
        HuggingFaceModelInstaller owner,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        long totalBytes) : IProgress<long>
    {
        public void Report(long value) =>
            owner.Report(progress, value, totalBytes, HuggingFaceModelInstallPhase.Verifying);
    }
}
