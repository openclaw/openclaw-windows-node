using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClawTray.Services;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Approves device pairing on local WSL gateways by manipulating
/// the gateway's pending/paired JSON files via wsl bash -c subprocess commands.
/// Uses wsl bash -c (not \\wsl$\ paths) to avoid Windows permission prompts.
/// </summary>
public static class LocalGatewayApprover
{
    /// <summary>
    /// Checks if the gateway URL points to localhost.
    /// </summary>
    public static bool IsLocalGateway(string gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl)) return false;
        try
        {
            var uri = new Uri(gatewayUrl);
            var host = uri.Host.ToLowerInvariant();
            return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to approve a device by moving it from pending.json to paired.json
    /// in the WSL gateway's devices directory.
    /// Returns (success, message).
    /// </summary>
    public static (bool Success, string Message) ApproveDevice(string deviceId)
    {
        try
        {
            // Find the WSL devices directory (returns a WSL-native path like ~/.openclaw-dev/devices)
            var wslDevicesDir = FindWslDevicesDir();
            if (wslDevicesDir == null)
                return (false, "Could not find gateway devices directory in WSL");

            var pendingWslPath = $"{wslDevicesDir}/pending.json";
            var pairedWslPath = $"{wslDevicesDir}/paired.json";

            // Read pending.json via wsl bash -c
            var pendingJson = WslReadFile(pendingWslPath);
            if (pendingJson == null)
                return (false, "No pending devices found");

            var pendingNode = JsonNode.Parse(pendingJson)?.AsObject();
            if (pendingNode == null)
                return (false, "Could not parse pending.json");

            // Find the entry matching our device ID
            string? requestId = null;
            JsonNode? pendingEntry = null;
            foreach (var kvp in pendingNode)
            {
                var entry = kvp.Value?.AsObject();
                if (entry == null) continue;
                var entryDeviceId = entry["deviceId"]?.GetValue<string>();
                if (string.Equals(entryDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    requestId = kvp.Key;
                    pendingEntry = entry.DeepClone();
                    break;
                }
            }

            if (requestId == null || pendingEntry == null)
                return (false, $"Device {deviceId[..16]}… not found in pending list");

            // Read or create paired.json
            JsonObject pairedNode;
            var pairedJson = WslReadFile(pairedWslPath);
            if (pairedJson != null)
            {
                pairedNode = JsonNode.Parse(pairedJson)?.AsObject() ?? new JsonObject();
            }
            else
            {
                pairedNode = new JsonObject();
            }

            // Build paired entry from pending entry
            var paired = pendingEntry.AsObject();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Generate a device token
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var deviceToken = Convert.ToBase64String(tokenBytes)
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');

            // Copy scopes to approvedScopes
            var scopes = paired["scopes"]?.DeepClone();

            // Determine role
            var role = paired["role"]?.GetValue<string>() ?? "operator";

            // Build tokens object
            var tokens = new JsonObject
            {
                [role] = new JsonObject
                {
                    ["token"] = deviceToken,
                    ["role"] = role,
                    ["scopes"] = scopes?.DeepClone(),
                    ["createdAtMs"] = nowMs
                }
            };

            paired["approvedScopes"] = scopes;
            paired["tokens"] = tokens;
            paired["createdAtMs"] = nowMs;
            paired["approvedAtMs"] = nowMs;

            // Remove request-only fields
            paired.Remove("requestId");
            paired.Remove("silent");
            paired.Remove("isRepair");
            paired.Remove("ts");

            // Add to paired.json keyed by deviceId
            pairedNode[deviceId] = paired;

            // Remove from pending.json
            pendingNode.Remove(requestId);

            // Write both files atomically via wsl bash -c (temp → rename)
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };

            var pairedContent = pairedNode.ToJsonString(writeOptions);
            WslWriteFileAtomic(pairedWslPath, pairedContent);

            var pendingContent = pendingNode.Count == 0 ? "{}" : pendingNode.ToJsonString(writeOptions);
            WslWriteFileAtomic(pendingWslPath, pendingContent);

            // Also store the device token in the Windows device key file
            // so the next connection attempt uses it
            StoreDeviceToken(deviceToken);

            return (true, "Device approved — reconnecting…");
        }
        catch (Exception ex)
        {
            return (false, $"Approval failed: {ex.Message}");
        }
    }

    /// <summary>Read a file inside WSL via subprocess. Path must be absolute (no ~).</summary>
    private static string? WslReadFile(string wslAbsPath)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl")
            {
                Arguments = $"bash -c \"cat {wslAbsPath} 2>/dev/null\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[LocalGatewayApprover] WSL read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Write a file inside WSL atomically (temp → mv). Uses base64 to avoid shell escaping issues.</summary>
    private static void WslWriteFileAtomic(string wslAbsPath, string content)
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var tmpPath = wslAbsPath + ".tmp";
        var cmd = $"echo {base64} | base64 -d > {tmpPath} && mv {tmpPath} {wslAbsPath}";
        var psi = new ProcessStartInfo("wsl")
        {
            Arguments = $"bash -c \"{cmd}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
        if (proc?.ExitCode != 0)
        {
            var stderr = proc?.StandardError.ReadToEnd();
            throw new IOException($"WSL write failed: {stderr}");
        }
    }

    /// <summary>Check if a directory exists inside WSL. Path must be absolute (no ~).</summary>
    private static bool WslDirectoryExists(string wslAbsPath)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl")
            {
                Arguments = $"bash -c \"test -d {wslAbsPath} && echo yes\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            return output == "yes";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolve WSL $HOME to an absolute path (e.g. /home/mharsh). Never use ~ directly.</summary>
    private static string? WslResolveHome()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl")
            {
                Arguments = "bash -c \"echo $HOME\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindWslDevicesDir()
    {
        // Resolve $HOME to absolute path first — never use ~ (bash doesn't expand it in all contexts)
        var home = WslResolveHome();
        if (string.IsNullOrEmpty(home))
        {
            Logger.Warn("[LocalGatewayApprover] Could not resolve WSL $HOME");
            return null;
        }

        string[] dataPaths = [".openclaw-dev/devices", ".openclaw/devices"];

        foreach (var dataPath in dataPaths)
        {
            if (dataPath.Contains("..") || dataPath.Contains('\0'))
                continue;

            var absPath = $"{home}/{dataPath}";
            if (WslDirectoryExists(absPath))
                return absPath;
        }

        return null;
    }

    private static void StoreDeviceToken(string token)
    {
        // SECURITY NOTE: Device token is stored in plaintext JSON (device-key-ed25519.json).
        // This follows the existing DeviceIdentity.cs pattern in the repo. The Ed25519 private key
        // is also stored in plaintext in the same file. Future improvement: encrypt sensitive
        // fields using DPAPI (ProtectedData.Protect with DataProtectionScope.CurrentUser).
        try
        {
            var keyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray", "device-key-ed25519.json");

            if (!File.Exists(keyPath)) return;

            var json = File.ReadAllText(keyPath);
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return;

            node["DeviceToken"] = token;
            File.WriteAllText(keyPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal — the device can re-pair on next connect
        }
    }
}
