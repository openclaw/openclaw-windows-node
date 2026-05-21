using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Host env security policy: which environment variables an executor must
/// refuse to set on a spawned child process.
/// </summary>
/// <remarks>
/// <para><b>Provenance.</b> The policy data lives upstream in
/// <c>openclaw/openclaw</c> at
/// <c>src/infra/host-env-security-policy.json</c>. We keep a byte-identical
/// copy at <c>src/OpenClaw.Shared/Mxc/HostEnvSecurityPolicy.json</c> embedded
/// as an assembly resource. When the upstream JSON changes, re-copy and rerun
/// the <c>HostEnvSecurityPolicyTests</c> tests.</para>
/// <para><b>Why we enforce this on the Windows node side.</b> openclaw does
/// not centralize env scrubbing at "the gateway"; it scrubs at every exec
/// boundary as defense-in-depth (see CHANGELOG references like *"on both
/// node host and macOS companion paths"*). This class is the Windows-node
/// analog of the macOS consumer
/// <c>apps/macos/Sources/OpenClaw/HostEnvSanitizer.swift</c> +
/// <c>HostEnvSecurityPolicy.generated.swift</c>. The macOS code is generated
/// from the same JSON via <c>scripts/generate-host-env-security-policy-swift.mjs</c>;
/// we just load the JSON directly at runtime instead of code-generating.</para>
/// <para><b>Threat model.</b> Agent-supplied env in
/// <see cref="MxcConfigBuilder.BuildEnv"/> is untrusted. For our sandbox-boundary
/// purposes, a key is "blocked" if it appears in any of the policy's block
/// sets (<c>blockedEverywhereKeys</c> ∪ <c>blockedOverrideOnlyKeys</c>) OR if
/// it starts with any blocked prefix (<c>blockedPrefixes</c> ∪
/// <c>blockedOverridePrefixes</c>), all case-insensitive. We merge the
/// "everywhere" and "override-only" buckets because for the agent we are the
/// override path — they're setting env explicitly, not inheriting it from
/// host process state.</para>
/// </remarks>
public sealed class HostEnvSecurityPolicy
{
    public static HostEnvSecurityPolicy Default { get; } = LoadEmbedded();

    private readonly HashSet<string> _blocked;
    private readonly string[] _blockedPrefixes;

    public IReadOnlyCollection<string> BlockedKeys => _blocked;
    public IReadOnlyList<string> BlockedPrefixes => _blockedPrefixes;

    private HostEnvSecurityPolicy(HashSet<string> blocked, string[] blockedPrefixes)
    {
        _blocked = blocked;
        _blockedPrefixes = blockedPrefixes;
    }

    /// <summary>
    /// True if the agent must not be allowed to set <paramref name="name"/>
    /// in the sandbox env. Combines <c>blockedEverywhereKeys</c>,
    /// <c>blockedOverrideOnlyKeys</c>, <c>blockedPrefixes</c>, and
    /// <c>blockedOverridePrefixes</c> from the canonical JSON.
    /// </summary>
    public bool IsBlocked(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        foreach (var ch in name)
        {
            if (ch == '=' || ch == '\0' || ch == '\r' || ch == '\n') return true;
        }
        if (_blocked.Contains(name)) return true;
        foreach (var prefix in _blockedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static HostEnvSecurityPolicy LoadEmbedded()
    {
        var asm = typeof(HostEnvSecurityPolicy).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("HostEnvSecurityPolicy.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("HostEnvSecurityPolicy.json embedded resource not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Failed to open embedded resource {resourceName}.");
        var doc = JsonSerializer.Deserialize<PolicyDocument>(stream, JsonOpts)
            ?? throw new InvalidOperationException("HostEnvSecurityPolicy.json was empty or malformed.");

        // Agent env scrub merges the everywhere-blocked set with the
        // override-blocked set (for our purposes, the agent is the override
        // path — they are setting env explicitly, not inheriting it).
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in doc.BlockedEverywhereKeys ?? Array.Empty<string>()) blocked.Add(k);
        foreach (var k in doc.BlockedOverrideOnlyKeys ?? Array.Empty<string>()) blocked.Add(k);

        // Same logic for prefixes: agent override path blocks both prefix sets.
        var prefixSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.BlockedPrefixes ?? Array.Empty<string>()) prefixSet.Add(p);
        foreach (var p in doc.BlockedOverridePrefixes ?? Array.Empty<string>()) prefixSet.Add(p);

        return new HostEnvSecurityPolicy(blocked, prefixSet.ToArray());
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record PolicyDocument
    {
        [JsonPropertyName("blockedEverywhereKeys")]
        public string[]? BlockedEverywhereKeys { get; init; }

        [JsonPropertyName("blockedOverrideOnlyKeys")]
        public string[]? BlockedOverrideOnlyKeys { get; init; }

        [JsonPropertyName("blockedPrefixes")]
        public string[]? BlockedPrefixes { get; init; }

        [JsonPropertyName("blockedOverridePrefixes")]
        public string[]? BlockedOverridePrefixes { get; init; }

        [JsonPropertyName("allowedInheritedOverrideOnlyKeys")]
        public string[]? AllowedInheritedOverrideOnlyKeys { get; init; }
    }
}
