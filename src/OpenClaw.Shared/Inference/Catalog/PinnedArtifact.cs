namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>The role a downloaded artifact plays in a local inference installation.</summary>
public enum ArtifactRole
{
    RuntimeBinary = 0,
    RuntimeDependency = 1,
    ModelWeights = 2,
}

/// <summary>A validated lowercase SHA-256 digest.</summary>
public sealed record Sha256Digest
{
    public Sha256Digest(string value)
    {
        if (!PinnedArtifactValidation.IsLowerHex(value, 64))
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 lowercase hexadecimal characters.", nameof(value));

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Attribution for catalog facts that were adapted from an upstream catalog,
/// separate from the location that distributes each binary artifact.
/// </summary>
public sealed record CatalogProvenance
{
    public CatalogProvenance(
        string sourceId,
        string title,
        string creator,
        Uri? sourceUri,
        string licenseIdentifier,
        Uri licenseUri,
        string changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(creator);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(changes);
        if (sourceUri is not null)
            PinnedArtifactValidation.RequireHttps(sourceUri, nameof(sourceUri));
        PinnedArtifactValidation.RequireHttps(licenseUri, nameof(licenseUri));

        SourceId = sourceId;
        Title = title;
        Creator = creator;
        SourceUri = sourceUri;
        LicenseIdentifier = licenseIdentifier;
        LicenseUri = licenseUri;
        Changes = changes;
    }

    public string SourceId { get; }
    public string Title { get; }
    public string Creator { get; }
    public Uri? SourceUri { get; }
    public string LicenseIdentifier { get; }
    public Uri LicenseUri { get; }
    public string Changes { get; }
}

/// <summary>Distribution origin for an immutable artifact.</summary>
public abstract record ArtifactSource
{
    public abstract string RepositoryId { get; }
    public abstract string ImmutableRevision { get; }
    public abstract Uri RepositoryUri { get; }
    public abstract Uri RevisionUri { get; }

    internal abstract Uri ResolveDownloadUri(string relativePath);
}

/// <summary>An asset attached to a GitHub release whose tag and commit are both pinned.</summary>
public sealed record GitHubReleaseSource : ArtifactSource
{
    public GitHubReleaseSource(string repositoryId, string releaseTag, string commitSha)
    {
        PinnedArtifactValidation.RequireRepositoryId(repositoryId, nameof(repositoryId));
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        if (!PinnedArtifactValidation.IsLowerHex(commitSha, 40))
            throw new ArgumentException("A Git commit must contain exactly 40 lowercase hexadecimal characters.", nameof(commitSha));
        if (releaseTag.Any(char.IsWhiteSpace) || releaseTag.Contains('/') || releaseTag.Contains('\\'))
            throw new ArgumentException("A GitHub release tag must be a single safe path segment.", nameof(releaseTag));

        RepositoryId = repositoryId;
        ReleaseTag = releaseTag;
        CommitSha = commitSha;
    }

    public override string RepositoryId { get; }
    public string ReleaseTag { get; }
    public string CommitSha { get; }
    public override string ImmutableRevision => CommitSha;
    public override Uri RepositoryUri => new($"https://github.com/{RepositoryId}");
    public override Uri RevisionUri => new($"{RepositoryUri}/releases/tag/{Uri.EscapeDataString(ReleaseTag)}");

    internal override Uri ResolveDownloadUri(string relativePath)
    {
        string escapedPath = PinnedArtifactValidation.EscapeRelativePath(relativePath);
        return new Uri($"{RepositoryUri}/releases/download/{Uri.EscapeDataString(ReleaseTag)}/{escapedPath}");
    }
}

/// <summary>A file served from an immutable Hugging Face repository revision.</summary>
public sealed record HuggingFaceRevisionSource : ArtifactSource
{
    public HuggingFaceRevisionSource(string repositoryId, string revisionSha)
    {
        PinnedArtifactValidation.RequireRepositoryId(repositoryId, nameof(repositoryId));
        if (!PinnedArtifactValidation.IsLowerHex(revisionSha, 40))
            throw new ArgumentException("A Hugging Face revision must contain exactly 40 lowercase hexadecimal characters.", nameof(revisionSha));

        RepositoryId = repositoryId;
        RevisionSha = revisionSha;
    }

    public override string RepositoryId { get; }
    public string RevisionSha { get; }
    public override string ImmutableRevision => RevisionSha;
    public override Uri RepositoryUri => new($"https://huggingface.co/{RepositoryId}");
    public override Uri RevisionUri => new($"{RepositoryUri}/tree/{RevisionSha}");

    internal override Uri ResolveDownloadUri(string relativePath)
    {
        string escapedPath = PinnedArtifactValidation.EscapeRelativePath(relativePath);
        return new Uri($"{RepositoryUri}/resolve/{RevisionSha}/{escapedPath}?download=true");
    }
}

/// <summary>A content-verified file and the immutable upstream revision that distributes it.</summary>
public sealed record PinnedArtifact
{
    public PinnedArtifact(
        string id,
        ArtifactRole role,
        ArtifactSource source,
        string relativePath,
        long sizeBytes,
        Sha256Digest sha256,
        CatalogProvenance? catalogProvenance = null)
    {
        PinnedArtifactValidation.RequireSafeId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sha256);
        _ = PinnedArtifactValidation.EscapeRelativePath(relativePath);
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Artifact size must be positive.");

        Id = id;
        Role = role;
        Source = source;
        RelativePath = relativePath;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        CatalogProvenance = catalogProvenance;
    }

    public string Id { get; }
    public ArtifactRole Role { get; }
    public ArtifactSource Source { get; }
    public string RelativePath { get; }
    public long SizeBytes { get; }
    public Sha256Digest Sha256 { get; }
    public CatalogProvenance? CatalogProvenance { get; }
    public Uri DownloadUri => Source.ResolveDownloadUri(RelativePath);
}

/// <summary>Tracked attribution shared by catalog entries adapted from NVIDIA CAIR.</summary>
public static class LocalInferenceCatalogProvenance
{
    public static CatalogProvenance NvidiaCair { get; } = new(
        sourceId: "nvidia-cair",
        title: "NVIDIA CAIR recipe catalog",
        creator: "NVIDIA Corporation",
        sourceUri: null,
        licenseIdentifier: "CC-BY-4.0",
        licenseUri: new Uri("https://creativecommons.org/licenses/by/4.0/"),
        changes: "Adapted into typed Windows catalog records with independently verified public artifact pins.");
}

internal static class PinnedArtifactValidation
{
    public static bool IsLowerHex(string? value, int expectedLength) =>
        value is not null &&
        value.Length == expectedLength &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void RequireHttps(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The URI must be an absolute HTTPS URI.", parameterName);
    }

    public static void RequireRepositoryId(string repositoryId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId, parameterName);
        string[] segments = repositoryId.Split('/');
        if (segments.Length != 2 || segments.Any(segment => !IsSafeRepositorySegment(segment)))
            throw new ArgumentException("A repository id must contain exactly two safe path segments.", parameterName);
    }

    public static void RequireSafeId(string id, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, parameterName);
        if (id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
        {
            throw new ArgumentException("An artifact id may contain lowercase ASCII letters, digits, dots, and hyphens only.", parameterName);
        }
    }

    public static string EscapeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Contains('\\') || relativePath.StartsWith('/') || relativePath.EndsWith('/'))
            throw new ArgumentException("An artifact path must be a normalized relative URI path.", nameof(relativePath));

        string[] segments = relativePath.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(char.IsControl)))
        {
            throw new ArgumentException("An artifact path contains an unsafe segment.", nameof(relativePath));
        }

        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static bool IsSafeRepositorySegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment) &&
        segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
