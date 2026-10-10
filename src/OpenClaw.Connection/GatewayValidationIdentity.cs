using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Owns a disposable copy of a gateway identity. Validation may initialize or read this copy,
/// but never writes the saved gateway's key or tokens.
/// </summary>
public sealed class GatewayValidationIdentity : IDisposable
{
    private const string FileName = "device-key-ed25519.json";
    private readonly string? _originalJson;
    public string DirectoryPath { get; }
    private readonly Dictionary<string, DeviceTokenReceivedEventArgs> _tokens = new(StringComparer.Ordinal);
    internal DeviceTokenReceivedEventArgs? OperatorCredential => _tokens.GetValueOrDefault("operator");
    internal bool UseBoundedBootstrapScopes { get; set; }
    internal bool OperatorTokenRecoveryAttempted { get; private set; }
    public bool UseV2Signature { get; internal set; }

    internal bool RejectStoredOperatorToken(string rejectedToken)
    {
        if (OperatorTokenRecoveryAttempted || OperatorCredential is not null ||
            DeviceIdentity.TryReadStoredDeviceToken(DirectoryPath) != rejectedToken)
            return false;
        OperatorTokenRecoveryAttempted = true;
        return DeviceIdentity.TryClearDeviceToken(DirectoryPath);
    }

    internal void CaptureToken(DeviceTokenReceivedEventArgs token)
    {
        if (token.Role is "operator" or "node" && !string.IsNullOrWhiteSpace(token.Token))
            _tokens[token.Role] = new(token.Token, token.Scopes?.ToArray(), token.Role);
    }

    public GatewayValidationIdentity(string? sourceDirectory = null)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "openclaw-connection-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            if (sourceDirectory is not null)
            {
                var source = Path.Combine(sourceDirectory, FileName);
                if (File.Exists(source))
                {
                    _originalJson = File.ReadAllText(source);
                    DeviceIdentity.AtomicWriteKeyFileRaw(Path.Combine(DirectoryPath, FileName), _originalJson);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DeviceIdentityReplacementTransaction CopyTo(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        return DeviceIdentity.ReplaceValidatedIdentity(destinationDirectory, null, CreateCommittedJson());
    }

    public DeviceIdentityReplacementTransaction ReplaceExisting(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        return DeviceIdentity.ReplaceValidatedIdentity(destinationDirectory, _originalJson, CreateCommittedJson());
    }

    private string CreateCommittedJson()
    {
        using var committed = new GatewayValidationIdentity(DirectoryPath);
        if (_tokens.Count > 0)
        {
            var identity = new DeviceIdentity(committed.DirectoryPath);
            identity.Initialize();
            foreach (var token in _tokens.Values)
                identity.StoreDeviceTokenForRole(token.Role, token.Token, token.Scopes);
        }
        return File.ReadAllText(Path.Combine(committed.DirectoryPath, FileName));
    }

    public void Dispose()
    {
        _tokens.Clear();
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Temporary gateway validation identity cleanup failed: {ex.GetType().Name}");
        }
    }
}
