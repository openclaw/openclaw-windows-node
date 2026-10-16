namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// A distinct synthetic identity for one gateway peer. Two peers with different slugs
/// yield independently assertable catalog, owner, and main-session content so that
/// routing between saved gateways can be proven rather than assumed. This carries no
/// real credentials, endpoints, or user data; it only derives deterministic synthetic
/// labels from a short validated slug.
/// </summary>
public sealed class GatewayPeerProfile
{
    public GatewayPeerProfile(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (slug.Length > 24 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException(
                "A peer slug must be 1-24 ASCII letters, digits, or hyphens so it cannot inject content into titles or sentinels.",
                nameof(slug));
        }

        Slug = slug;
    }

    public string Slug { get; }

    /// <summary>The main-session title this peer advertises in its session catalog.</summary>
    public string MainTitle => $"Fixture peer {Slug}: main conversation";

    /// <summary>A per-peer sentinel that must appear only in this peer's main-session history.</summary>
    public string MainSentinel => $"FIXTURE PEER {Slug} MAIN: the lighthouse is green.";

    /// <summary>The owner id this peer reports from users.self.</summary>
    public string OwnerId => $"fixture-owner-{Slug}";

    /// <summary>The owner display name this peer reports from users.self.</summary>
    public string OwnerDisplayName => $"Fixture Owner {Slug}";

    /// <summary>The owner email this peer reports from users.self.</summary>
    public string OwnerEmail => $"owner-{Slug}@example.test";

    /// <summary>A distinct friendly name for this peer's saved gateway record.</summary>
    public string FriendlyName => $"Fixture peer {Slug}";
}
