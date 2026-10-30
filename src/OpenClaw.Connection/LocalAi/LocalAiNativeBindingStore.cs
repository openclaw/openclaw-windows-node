using System.Text.Json;

// Module: LocalAiNativeBindingStore
//
// Persisted ownership receipt for the native (non-WSL) Local AI Gateway binding:
// which Gateway record owns the local llama.cpp runtime, its endpoint/identity
// bindings, the published model, the previous primary model for fallback, and a
// configuration hash used to detect out-of-band Gateway changes. The write-ahead
// `Pending` flag survives a crash between dispatching a Gateway mutation and
// recording its acknowledgement; `AutomaticRecoveryEnabled` records explicit
// start/stop intent separately from ownership. State is a single JSON document
// under LocalAiPaths.RootDirectory, read and written under an exclusive
// cross-process file lock obtained via AcquireAsync.
//
// Main public API and typical use (see LocalAiGatewayLifecycle):
//
//   var store = new LocalAiNativeBindingStore(paths);
//   if (store.Exists) { ... }                      // cheap presence probe (e.g. HasNativeBinding)
//   LocalAiNativeBinding? binding = store.Load();  // null when absent; throws
//                                                  // InvalidDataException/JsonException when damaged
//
//   // Mutations are serialized across processes with the cross-process lock:
//   await using FileStream lease = await store.AcquireAsync(ct);
//   binding = store.Load() ?? throw new InvalidOperationException("...");
//   store.Save(binding with { Pending = false, ConfigHash = current.Hash });
//
//   store.Delete();                                // releases ownership (with lock held)

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
    bool AutomaticRecoveryEnabled = true);

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
