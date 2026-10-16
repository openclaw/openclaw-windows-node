using System.Text.Json;

namespace OpenClaw.Connection.LocalAi;

public sealed record LocalAiNativeBinding(
    string GatewayId,
    string EndpointBinding,
    string IdentityBinding,
    string ModelRef,
    string? PreviousPrimary,
    string ConfigHash,
    bool AddedAllowlistEntry,
    bool Pending = false,
    bool AutomaticRecoveryEnabled = true,
    LocalAiNativeModelRebind? RebindFrom = null);

/// <summary>
/// Write-ahead record of a model rebind. The binding already names the new model while
/// the installation manifest may still name the old one; the next manifest load settles
/// the binding to whichever model the manifest committed. Rebinding writes nothing to the
/// Gateway, so the binding's config hash stays valid for either outcome.
/// </summary>
public sealed record LocalAiNativeModelRebind(string ModelRef, bool AddedAllowlistEntry);

/// <summary>
/// Separate from the portable artifact receipt. The write-ahead pending flag survives
/// a crash between dispatching a Gateway mutation and recording its acknowledgement.
/// </summary>
public sealed class LocalAiNativeBindingStore(LocalAiPaths paths)
{
    public bool Exists => File.Exists(PathFor("gateway-binding.json"));
    public void Delete() => File.Delete(PathFor("gateway-binding.json"));

    private string PathFor(string name) => paths.ResolveContainedPath(name, "Local AI Gateway binding");

    public LocalAiNativeBinding? Load()
    {
        var path = PathFor("gateway-binding.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 65536)
            throw new InvalidDataException("The Local AI Gateway binding is damaged.");
        var binding = JsonSerializer.Deserialize<LocalAiNativeBinding>(File.ReadAllText(path));
        if (binding is null || new[] { binding.GatewayId, binding.EndpointBinding,
                binding.IdentityBinding, binding.ModelRef, binding.ConfigHash }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 4096))
            throw new InvalidDataException("The Local AI Gateway binding is damaged.");
        LocalAiGatewayProviderDefinition.ValidateFallbackModel(binding.PreviousPrimary);
        return binding;
    }

    public void Save(LocalAiNativeBinding binding)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var temporary = PathFor($"gateway-binding-{Guid.NewGuid():N}.pending");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, binding);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathFor("gateway-binding.json"), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var path = PathFor("gateway-binding.lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
    }
}
