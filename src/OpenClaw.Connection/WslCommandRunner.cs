using OpenClaw.Shared;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Connection;

public sealed record WslCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed record WslDistroInfo(string Name, string State, int Version);
public sealed record WslDistroRegistration(string Name, string BasePath);
public sealed record WslDistroConfiguration(uint Version, uint DefaultUid, uint Flags);
public sealed record WslDistroConfigurationResult(
    bool Success,
    WslDistroConfiguration? Configuration,
    int HResult = 0);

public interface IWslCommandRunner
{
    Task<WslCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);

    Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WslDistroRegistration>> ListRegistrationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WslDistroRegistration>>([]);

    Task<WslDistroConfigurationResult> GetDistroConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WslDistroConfigurationResult(false, null, -1));

    Task<WslCommandResult> ConfigureDistroRegistrationAsync(
        string name,
        uint defaultUid,
        uint flags,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WslCommandResult(-1, string.Empty, "WSL registration configuration is unavailable."));

    Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default);

    Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default);

    Task<WslCommandResult> RunInDistroAsync(
        string name, IReadOnlyList<string> command,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>
/// Lightweight WSL command runner for probing distro state.
/// Does not include diagnostics tee — use SetupEngine for full setup/teardown.
/// </summary>
public sealed class WslExeCommandRunner : IWslCommandRunner
{
    private const string LxssRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _defaultTimeout;

    public WslExeCommandRunner(
        IOpenClawLogger? logger = null,
        TimeSpan? defaultTimeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["--list", "--verbose"], cancellationToken);
        return result.Success ? ParseDistroList(result.StandardOutput) : [];
    }

    public Task<IReadOnlyList<WslDistroRegistration>> ListRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registrations = new List<WslDistroRegistration>();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<WslDistroRegistration>>(registrations);
        using var root = Registry.CurrentUser.OpenSubKey(LxssRegistryPath, writable: false);
        if (root is null)
            return Task.FromResult<IReadOnlyList<WslDistroRegistration>>(registrations);

        foreach (var subKeyName in root.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var distro = root.OpenSubKey(subKeyName, writable: false);
            if (distro?.GetValue("DistributionName") is not string name ||
                distro.GetValue("BasePath") is not string basePath ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            registrations.Add(new(name.Trim(), Environment.ExpandEnvironmentVariables(basePath.Trim())));
        }

        return Task.FromResult<IReadOnlyList<WslDistroRegistration>>(registrations);
    }

    public Task<WslDistroConfigurationResult> GetDistroConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new WslDistroConfigurationResult(false, null, -1));

        IntPtr environmentVariables = IntPtr.Zero;
        uint environmentVariableCount = 0;
        try
        {
            var hresult = WslGetDistributionConfiguration(
                name,
                out var version,
                out var defaultUid,
                out var flags,
                out environmentVariables,
                out environmentVariableCount);
            return Task.FromResult(hresult >= 0
                ? new WslDistroConfigurationResult(true, new(version, defaultUid, flags), hresult)
                : new WslDistroConfigurationResult(false, null, hresult));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult(new WslDistroConfigurationResult(false, null, -1));
        }
        finally
        {
            for (uint index = 0; environmentVariables != IntPtr.Zero && index < environmentVariableCount; index++)
            {
                var value = Marshal.ReadIntPtr(environmentVariables, checked((int)(index * IntPtr.Size)));
                if (value != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(value);
            }
            if (environmentVariables != IntPtr.Zero)
                Marshal.FreeCoTaskMem(environmentVariables);
        }
    }

    public Task<WslCommandResult> ConfigureDistroRegistrationAsync(
        string name,
        uint defaultUid,
        uint flags,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new WslCommandResult(-1, string.Empty, "WSL registration configuration requires Windows."));

        try
        {
            var hresult = WslConfigureDistribution(name, defaultUid, flags);
            return Task.FromResult(hresult >= 0
                ? new WslCommandResult(0, string.Empty, string.Empty)
                : new WslCommandResult(hresult, string.Empty, "WslConfigureDistribution failed."));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult(new WslCommandResult(-1, string.Empty, "WslConfigureDistribution is unavailable."));
        }
    }

    public Task<WslCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null) =>
        RunProcessAsync("wsl.exe", arguments, cancellationToken, environment);

    public Task<WslCommandResult> RunInDistroAsync(
        string name, IReadOnlyList<string> command,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { "-d", name, "--" };
        args.AddRange(command);
        return RunAsync(args, cancellationToken, environment);
    }

    public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--terminate", name], cancellationToken);

    public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--unregister", name], cancellationToken);

    public static IReadOnlyList<WslDistroInfo> ParseDistroList(string output)
    {
        var distros = new List<WslDistroInfo>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace("\0", string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line[0] == '*')
                line = line[1..].TrimStart();

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                continue;

            var state = parts[^2];
            var name = string.Join(" ", parts.Take(parts.Length - 2));
            if (!string.IsNullOrWhiteSpace(name))
                distros.Add(new WslDistroInfo(name, state, version));
        }

        return distros;
    }

    private async Task<WslCommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var kvp in environment)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        _logger.Info($"[WSL] {fileName} {string.Join(" ", arguments)}");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new WslCommandResult(-1, string.Empty, $"Failed to start wsl.exe: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_defaultTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        catch (OperationCanceledException)
        {
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string stdout, stderr;
        try { stdout = await stdoutTask; } catch { stdout = string.Empty; }
        try { stderr = await stderrTask; } catch { stderr = string.Empty; }

        return timedOut
            ? new WslCommandResult(-1, stdout, "wsl.exe timed out")
            : new WslCommandResult(process.ExitCode, stdout, stderr);
    }

    [DllImport("api-ms-win-wsl-api-l1-1-0.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WslGetDistributionConfiguration(
        string distributionName,
        out uint distributionVersion,
        out uint defaultUid,
        out uint wslDistributionFlags,
        out IntPtr defaultEnvironmentVariables,
        out uint defaultEnvironmentVariableCount);

    [DllImport("api-ms-win-wsl-api-l1-1-0.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WslConfigureDistribution(
        string distributionName,
        uint defaultUid,
        uint wslDistributionFlags);
}
