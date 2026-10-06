using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Retains an unadmitted restart handle privately. It never grants receipt authority or extends expiry.</summary>
internal sealed class NativeRestartRecoveryStore(string dataDirectory)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "setup-dashboard-handoff");
    private string FilePath => Path.Combine(_directory, "restart.json");

    public void Save(string handle)
    {
        if (SetupDashboardHandoff.ParseHandle(handle) is null)
            throw new ArgumentException("The setup restart handle is invalid.", nameof(handle));
        using var lease = PersistenceFileLease.Acquire(FilePath);
        Directory.CreateDirectory(_directory);
        RequireOwnedPath();
        OpenClaw.Shared.Mcp.McpAuthToken.TryRestrictDataDirectoryAcl(_directory);
        DeviceIdentity.AtomicWriteKeyFileRaw(FilePath, JsonSerializer.Serialize(handle));
    }

    public string? Read()
    {
        using var lease = PersistenceFileLease.Acquire(FilePath);
        return ReadCore();
    }

    public void Clear(string handle)
    {
        using var lease = PersistenceFileLease.Acquire(FilePath);
        if (ReadCore() == handle) File.Delete(FilePath);
    }

    private string? ReadCore()
    {
        try { return ReadValue(); }
        catch (InvalidDataException)
        {
            RequireOwnedPath();
            // Recheck under the same writer lease before deleting. Never remove a newer valid handle.
            try { _ = ReadValue(); }
            catch (InvalidDataException)
            {
                RequireOwnedPath();
                File.Delete(FilePath);
            }
            throw;
        }
    }

    private string? ReadValue()
    {
        RequireOwnedPath();
        if (!File.Exists(FilePath)) return null;
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 1024)
            throw new InvalidDataException("The saved setup restart is invalid.");
        string? value;
        try { value = JsonSerializer.Deserialize<string>(input); }
        catch (JsonException error) { throw new InvalidDataException("The saved setup restart is invalid.", error); }
        return SetupDashboardHandoff.ParseHandle(value) ?? throw new InvalidDataException("The saved setup restart is invalid.");
    }

    private void RequireOwnedPath()
    {
        if (Directory.Exists(_directory) && File.GetAttributes(_directory).HasFlag(FileAttributes.ReparsePoint) ||
            File.Exists(FilePath) && File.GetAttributes(FilePath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The setup restart recovery path is not an owned regular path.");
    }
}
