using OpenClaw.Shared;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClawTray.Services;

/// <summary>
/// Manages an SSH local port-forward process for gateway access.
/// </summary>
public sealed class SshTunnelService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private Process? _process;
    private string? _lastSpec;
    private bool _stopping;

    /// <summary>Raised when the SSH tunnel exits unexpectedly (not during shutdown).</summary>
    public event EventHandler<int>? TunnelExited;

    public SshTunnelService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void EnsureStarted(SettingsManager settings)
    {
        if (!settings.UseSshTunnel)
        {
            Stop();
            return;
        }

        EnsureStarted(
            settings.SshTunnelUser,
            settings.SshTunnelHost,
            settings.SshTunnelRemotePort,
            settings.SshTunnelLocalPort);
    }

    public void EnsureStarted(string user, string host, int remotePort, int localPort)
    {
        user = user.Trim();
        host = host.Trim();

        var spec = BuildSpec(user, host, remotePort, localPort);

        if (IsRunning && string.Equals(_lastSpec, spec, StringComparison.Ordinal))
        {
            return;
        }

        Stop();
        StartProcess(user, host, remotePort, localPort);
        _lastSpec = spec;
    }

    public void Stop()
    {
        if (_process == null)
        {
            return;
        }

        _stopping = true;
        _logger.Info("Stopping SSH tunnel process");

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"SSH tunnel stop failed: {ex.Message}");
        }
        finally
        {
            try { _process.Dispose(); } catch { }
            _process = null;
            _lastSpec = null;
            _stopping = false;
        }
    }

    private void StartProcess(string user, string host, int remotePort, int localPort)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = BuildArguments(user, host, remotePort, localPort),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Info($"[SSH] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Warn($"[SSH] {e.Data}");
            }
        };

        process.Exited += (_, _) =>
        {
            var exitCode = process.ExitCode;
            if (_stopping)
            {
                _logger.Info($"SSH tunnel exited during shutdown (code {exitCode})");
            }
            else
            {
                _logger.Warn($"SSH tunnel exited unexpectedly (code {exitCode})");
                try { process.Dispose(); } catch { }
                _process = null;
                _lastSpec = null;
                TunnelExited?.Invoke(this, exitCode);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ssh process");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException("Unable to start SSH tunnel process. Ensure OpenSSH client is installed and available in PATH.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        _logger.Info($"SSH tunnel started: 127.0.0.1:{localPort} -> 127.0.0.1:{remotePort} via {user}@{host}");
    }

    private static string BuildSpec(string user, string host, int remotePort, int localPort)
        => $"{user}@{host}:{localPort}:{remotePort}";

    // Strict validation for SSH user/host to prevent command injection
    private static readonly Regex s_validSshUser = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex s_validSshHost = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    private static string BuildArguments(string user, string host, int remotePort, int localPort)
    {
        if (!s_validSshUser.IsMatch(user))
            throw new ArgumentException($"SSH user contains invalid characters: '{user}'");
        if (!s_validSshHost.IsMatch(host))
            throw new ArgumentException($"SSH host contains invalid characters: '{host}'");

        var sb = new StringBuilder();
        sb.Append("-N ");
        sb.Append("-L ");
        sb.Append(localPort);
        sb.Append(":127.0.0.1:");
        sb.Append(remotePort);
        sb.Append(' ');
        sb.Append(user);
        sb.Append('@');
        sb.Append(host);
        return sb.ToString();
    }

    public void Dispose()
    {
        Stop();
    }
}
