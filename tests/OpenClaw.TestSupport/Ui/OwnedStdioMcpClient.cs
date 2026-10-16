using System.Diagnostics;
using ModelContextProtocol.Protocol;
using SdkClient = ModelContextProtocol.Client.McpClient;

namespace OpenClaw.TestSupport.Ui;

/// <summary>Owns one exact stdio server process; the SDK owns protocol framing.</summary>
public sealed class OwnedStdioMcpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly object _disposeSync = new();
    private Task? _disposal;
    public SdkClient Client { get; }
    public int ProcessId => _process.Id;
    public DateTime ProcessStartUtc { get; }

    private OwnedStdioMcpClient(Process process, SdkClient client)
    {
        _process = process;
        Client = client;
        ProcessStartUtc = process.StartTime.ToUniversalTime();
    }

    public static async Task<OwnedStdioMcpClient> StartAsync(ProcessStartInfo start, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (start.UseShellExecute || !start.RedirectStandardInput || !start.RedirectStandardOutput ||
            !start.RedirectStandardError)
            throw new ArgumentException("An explicit redirected stdio process is required.", nameof(start));
        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new IOException("Failed to start the owned MCP process.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        SdkClient? client = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            client = await SdkClient.CreateAsync(new StreamClientTransport(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream), cancellationToken: deadline.Token);
            return new(process, client);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try { await StopAsync(process); }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            if (client is not null)
            {
                try { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            }
            throw new ContractAutomationException("MCP initialization or owned-process cleanup failed.",
                failures.Count == 1 ? failure : new AggregateException(failures));
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { process.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try { await StopAsync(_process); }
        catch (Exception failure) { failures.Add(failure); }
        try { await Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count > 0)
            throw new ContractAutomationException("Owned MCP cleanup failed.", new AggregateException(failures));
    }
}
