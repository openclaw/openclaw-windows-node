using System.Diagnostics;
using System.Text;

namespace OpenClaw.Connection;

/// <summary>Secret-bearing CLI output is bounded in memory and never sent through setup diagnostics.</summary>
public static class BrowserBootstrapWslCommand
{
    public const string Script = """
        set -euo pipefail
        test "$(id -un)" = openclaw
        unset OPENCLAW_PROFILE OPENCLAW_STATE_DIR OPENCLAW_CONFIG_PATH OPENCLAW_HOME NODE_OPTIONS NODE_PATH
        export PATH=/home/openclaw/.openclaw/tools/node/bin:/usr/local/bin:/usr/bin:/bin
        export HOME=/home/openclaw
        pid=$(systemctl --user show openclaw-gateway.service -p MainPID --value)
        [[ "$pid" =~ ^[1-9][0-9]*$ ]]
        test -r "/proc/$pid/environ"
        while IFS= read -r -d '' entry; do
          case "$entry" in
            HOME=*|OPENCLAW_HOME=*) test "${entry#*=}" = /home/openclaw ;;
            OPENCLAW_PROFILE=*) test "${entry#*=}" = default ;;
            OPENCLAW_STATE_DIR=*) test "${entry#*=}" = /home/openclaw/.openclaw ;;
            OPENCLAW_CONFIG_PATH=*) test "${entry#*=}" = /home/openclaw/.openclaw/openclaw.json ;;
          esac
        done < "/proc/$pid/environ"
        export OPENCLAW_STATE_DIR=/home/openclaw/.openclaw
        export OPENCLAW_CONFIG_PATH=/home/openclaw/.openclaw/openclaw.json
        for cli in /home/openclaw/.openclaw/bin/openclaw /opt/openclaw/bin/openclaw /usr/local/bin/openclaw; do
          if test -x "$cli"; then
            exec "$cli" browser extension pair --json --local-gateway
          fi
        done
        exit 127
        """;

    internal static string NormalizeStandardInput(string script) =>
        script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";

    public static async Task<string> RunAsync(string distro, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "--distribution", distro, "--exec", "/bin/bash", "--noprofile", "--norc", "-s" })
            start.ArgumentList.Add(arg);
        // Do not propagate Windows CLI profile/runtime overrides through WSLENV.
        start.Environment.Remove("WSLENV");
        using var process = Process.Start(start) ?? throw new IOException("pairing_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(NormalizeStandardInput(Script).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // Await drains alongside exit so oversized output faults immediately rather than waiting for exit.
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException("pairing_unavailable");
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        var result = new StringBuilder();
        for (;;)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return result.ToString();
            if (result.Length + count > 16384) throw new IOException("pairing_unavailable");
            result.Append(buffer, 0, count);
        }
    }
}
