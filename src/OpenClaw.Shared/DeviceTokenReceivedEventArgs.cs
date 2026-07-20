namespace OpenClaw.Shared;

/// <summary>
/// Event args raised when a client receives a device token from the gateway
/// during the hello-ok handshake. Additive event — clients continue to write
/// tokens internally for backward compatibility.
/// </summary>
public sealed class DeviceTokenReceivedEventArgs : EventArgs
{
    /// <summary>The device token string.</summary>
    public string Token { get; }

    /// <summary>Scopes granted with this token, if available.</summary>
    public string[]? Scopes { get; }

    /// <summary>"operator" or "node".</summary>
    public string Role { get; }

    public DeviceTokenReceivedEventArgs(string token, string[]? scopes, string role)
    {
        Token = token;
        Scopes = scopes;
        Role = role;
    }
}
