// <summary>
// Persists the native Gateway ownership receipt separately from the portable Local AI install receipt.
// The store serializes route mutations under a file lease and atomically replaces the receipt. Pending,
// rebind, and unconfirmed-primary journals must be settled by the lifecycle before normal route changes.
// Usage:
//   await using FileStream lease = await store.AcquireAsync(cancellationToken);
//   LocalAiNativeBinding? binding = store.Load();
//   if (binding is not null) store.Save(binding with { UnconfirmedPrimary = model });
//   // The owner settles or clears the journal after reading Gateway configuration.
// </summary>
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

/// <summary>
/// Durable native Gateway ownership receipt. Its journals preserve recoverable intent across interrupted
/// Gateway mutations and must be settled while the receipt lease is held.
/// </summary>
/// <param name="UnconfirmedPrimary">
/// Write-ahead record of a cloud primary sent to the Gateway but not yet confirmed. The next
/// Gateway config read settles it: if the Gateway shows this primary it becomes
/// <paramref name="PreviousPrimary"/>, otherwise it is dropped.
/// </param>
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
    LocalAiNativeModelRebind? RebindFrom = null,
    string? UnconfirmedPrimary = null);

/// <summary>
/// Write-ahead record of a model rebind. <c>CommitModelRebindAsync</c> writes and clears it
/// inside one receipt-lease hold, so a journal on disk means the process stopped mid-commit.
/// The runtime's next manifest load settles it to whichever model the manifest names.
/// </summary>
public sealed record LocalAiNativeModelRebind(string ModelRef, bool AddedAllowlistEntry);

/// <summary>
/// Persists native Gateway ownership independently of the portable artifact receipt. Its file lease and
/// atomic replacement make write-ahead route journals durable across process interruption.
/// </summary>
public sealed class LocalAiNativeBindingStore(LocalAiPaths paths)
{
    /// <summary>Whether the native Gateway ownership receipt exists.</summary>
    public bool Exists => File.Exists(PathFor("gateway-binding.json"));
    /// <summary>Deletes the native Gateway ownership receipt after confirmed withdrawal.</summary>
    public void Delete() => File.Delete(PathFor("gateway-binding.json"));

    private string PathFor(string name) => paths.ResolveContainedPath(name, "Local AI Gateway binding");

    /// <summary>Loads and validates the receipt, or returns null when no native ownership was recorded.</summary>
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
        LocalAiGatewayProviderDefinition.ValidateFallbackModel(binding.UnconfirmedPrimary);
        return binding;
    }

    /// <summary>Saves a validated ownership receipt with same-directory atomic replacement.</summary>
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

    /// <summary>Acquires the exclusive receipt lease that serializes native Gateway mutations.</summary>
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
