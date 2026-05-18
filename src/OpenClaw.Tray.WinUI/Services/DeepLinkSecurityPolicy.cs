using OpenClaw.Shared;
using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenClawTray.Services;

internal static class DeepLinkSecurityPolicy
{
    public const int MaxIpcMessageBytes = 8192;
    public static readonly TimeSpan IpcReadTimeout = TimeSpan.FromSeconds(2);

    private const string PipeNamePrefix = "OpenClawTray-DeepLink";

    private static readonly HashSet<string> StateChangingPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "send",
        "agent",
        "voice",
        "voice-start",
        "voice-stop",
        "ssh-restart",
        "restart-ssh",
        "restart-ssh-tunnel"
    };

    public static string BuildCurrentUserScopedPipeName(string dataPath)
        => BuildPipeName(dataPath, GetCurrentUserScope(), GetCurrentSessionId());

    internal static string BuildPipeName(string dataPath, string userScope, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userScope);

        var scope = $"{userScope}|{sessionId}|{Path.GetFullPath(dataPath)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return $"{PipeNamePrefix}-{Convert.ToHexString(hash, 0, 8)}";
    }

    public static bool IsIpcPayloadWithinLimit(string? uri)
        => !string.IsNullOrEmpty(uri) && Encoding.UTF8.GetByteCount(uri) <= MaxIpcMessageBytes;

    public static bool IsStateChangingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Trim().Trim('/').ToLowerInvariant();
        if (StateChangingPaths.Contains(normalized))
            return true;

        var slashIndex = normalized.IndexOf('/');
        return slashIndex > 0 && StateChangingPaths.Contains(normalized[..slashIndex]);
    }

    public static bool RequiresConfirmation(DeepLinkResult? result)
        => result != null && IsStateChangingPath(result.Path);

    public static string RedactForLog(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return "<empty-deep-link>";

        var result = DeepLinkParser.ParseDeepLink(uri);
        if (result == null)
            return "<invalid-deep-link>";

        var redactedPath = RedactPathForLog(result.Path);
        return string.IsNullOrEmpty(result.Query)
            ? $"openclaw://{redactedPath}"
            : $"openclaw://{redactedPath}?<redacted>";
    }

    internal static string GetActionDisplayName(DeepLinkResult result)
    {
        var path = result.Path.Trim().Trim('/').ToLowerInvariant();
        return path switch
        {
            "send" => "open the quick-send window with a prefilled message",
            "agent" => "send a message to the agent",
            "voice" or "voice-start" => "start voice input",
            "voice-stop" => "stop voice input",
            "ssh-restart" or "restart-ssh" or "restart-ssh-tunnel" => "restart the SSH tunnel",
            _ => "run this OpenClaw action"
        };
    }

    internal static string RedactPathForLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path.Trim().Trim('/');
        if (normalized.Length == 0)
            return "";

        var slashIndex = normalized.IndexOf('/');
        var firstSegment = slashIndex >= 0 ? normalized[..slashIndex] : normalized;

        return slashIndex >= 0 ? $"{firstSegment}/..." : firstSegment;
    }

    private static string GetCurrentUserScope()
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
                return sid;
        }

        return $"{Environment.MachineName}\\{Environment.UserName}";
    }

    private static int GetCurrentSessionId()
        => OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId : 0;
}
