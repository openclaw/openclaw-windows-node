using System.Diagnostics;
using System.Text;
using OpenClaw.Shared.Browser;

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
        cli_path=
        for cli in /home/openclaw/.openclaw/bin/openclaw /opt/openclaw/bin/openclaw /usr/local/bin/openclaw; do
          if test -x "$cli"; then cli_path="$cli"; break; fi
        done
        test -n "$cli_path"
        node=$(command -v node)
        """;

    internal static string NormalizeStandardInput(string script) =>
        script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";

    private static readonly Lazy<string> Broker = new(() =>
    {
        using var stream = typeof(BrowserBootstrapWslCommand).Assembly.GetManifestResourceStream("OpenClaw.Connection.BrowserBootstrapWslBroker.cjs")
            ?? throw new IOException("pairing_unavailable");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    });

    internal static string BuildInput(string requestId)
    {
        if (requestId.Length != 32 || requestId.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new InvalidDataException("pairing_unavailable");
        var source = Broker.Value;
        var source64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        // All code crosses WSL via stdin, never shell-variable-bearing wsl.exe argv.
        // The command and runtime selectors remain fixed; no new public option is introduced.
        var program = "const settings={mode:'broker',requestId:'" + requestId + "',node:process.execPath,command:[process.argv[1],'browser','extension','pair','--json','--local-gateway']};const supervisorSource='" + source64 + "';" + source;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(program));
        return NormalizeStandardInput(Script + "\nexec \"$node\" -e \"$(printf '%s' '" + encoded + "' | base64 -d)\" \"$cli_path\"\n");
    }

    public static async Task<string> RunAsync(string distro, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestId = Guid.NewGuid().ToString("N");
        var exchange = new BrowserBootstrapWslExchange(requestId, "openclaw-browser-bootstrap-");
        var input = Encoding.UTF8.GetBytes(BuildInput(requestId));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "--distribution", distro, "--exec", "/bin/bash", "--noprofile", "--norc", "-s" })
            start.ArgumentList.Add(arg);
        start.Environment.Remove("WSLENV");
        using var process = Process.Start(start) ?? throw new IOException("pairing_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        void Revoke()
        {
            exchange.Revoke();
            // EOF revokes permission in the guest. Killing wsl.exe is NOT guest settlement.
            try { process.StandardInput.BaseStream.Close(); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
        using var stop = deadline.Token.Register(Revoke);
        var output = exchange.ReadToEndAsync(process.StandardOutput.BaseStream);
        var error = DrainErrorAsync(process.StandardError);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        foreach (var task in new[] { output, error })
            _ = task.ContinueWith(t => { _ = t.Exception; Revoke(); }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            await exchange.Ready.WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            // Mark the single attempt before writing. A partial/failed write is never retried.
            await process.StandardInput.BaseStream.WriteAsync(exchange.Permit(), deadline.Token);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            await Task.WhenAll(output, error, exit).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("pairing_unavailable");
            return exchange.Result;
        }
        finally
        {
            if (!exchange.Acknowledged) Revoke();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                // The soft budget reports failure, never grants permission to retire. On lost
                // submission/acknowledgment this deliberately stays pending (UNKNOWN/BUSY).
                await BrowserBootstrapProcessLifetime.AwaitOwnedAsync(exchange.Settlement, cleanup.Token);
            }
            finally
            {
                // This block cannot be reached through the pending settlement wait without a
                // positive guest acknowledgment. Only then may Windows cleanup terminate a client.
                if (exchange.Acknowledged)
                {
                    try
                    {
                        if (!process.HasExited && (deadline.IsCancellationRequested || output.IsFaulted || error.IsFaulted || cleanup.IsCancellationRequested))
                        {
                            try { process.Kill(entireProcessTree: true); }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception) { }
                        }
                    }
                    finally
                    {
                        try { await BrowserBootstrapProcessLifetime.JoinAsync(process, cleanup.Token); }
                        finally { await BrowserBootstrapProcessLifetime.AwaitOwnedAsync(Task.WhenAll(output, error), cleanup.Token); }
                    }
                }
            }
        }
    }

    private static async Task DrainErrorAsync(StreamReader reader)
    {
        var buffer = new char[1024]; var total = 0;
        for (;;)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) return;
            if ((total += count) > 16384) throw new IOException("pairing_unavailable");
        }
    }
}
