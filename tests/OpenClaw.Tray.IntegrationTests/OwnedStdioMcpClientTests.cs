using System.Diagnostics;
using System.Text.Json;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class OwnedStdioMcpClientTests
{
    [Fact]
    public async Task SuccessfulClientOwnsItsExactProcessAndDisposesIt()
    {
        using var directory = new TempDirectory();
        await using var server = await StartAsync("success", directory);
        var pid = server.ProcessId;
        var started = server.ProcessStartUtc;
        Assert.Equal("fixture", server.Client.ServerInfo.Version);
        var result = await server.Client.CallToolAsync("fixture", new Dictionary<string, object?>());
        Assert.True(LvtProtocol.RequireSuccess("fixture", result).GetProperty("ok").GetBoolean());
        await server.DisposeAsync();
        await server.DisposeAsync();
        AssertStopped(pid, started);
    }

    [Theory]
    [InlineData("hang-initialize")]
    [InlineData("exit")]
    public async Task FailedInitializationStopsOnlyItsOwnedChild(string mode)
    {
        using var directory = new TempDirectory();
        await Assert.ThrowsAsync<ContractAutomationException>(() => StartAsync(mode, directory, TimeSpan.FromSeconds(5)));
        var (pid, started) = ReadIdentity(directory);
        AssertStopped(pid, started);
    }

    [Fact]
    public async Task CancelledInitializationDoesNotLeaveAChild()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var pending = StartAsync("hang-initialize", directory, TimeSpan.FromSeconds(30), cancellation.Token);
        await WaitIdentityAsync(directory);
        cancellation.Cancel();
        await Assert.ThrowsAsync<ContractAutomationException>(() => pending);
        var (pid, started) = ReadIdentity(directory);
        AssertStopped(pid, started);
    }

    [Fact]
    public async Task CancelledToolCallCanStillCleanUpTheServer()
    {
        using var directory = new TempDirectory();
        var server = await StartAsync("hang-tool", directory);
        var pid = server.ProcessId;
        var started = server.ProcessStartUtc;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                server.Client.CallToolAsync("fixture", new Dictionary<string, object?>(), cancellationToken: cancellation.Token).AsTask());
        }
        finally { await server.DisposeAsync(); }
        AssertStopped(pid, started);
    }

    [Fact]
    public async Task StoppingOneServerDoesNotStopTheOther()
    {
        using var firstRoot = new TempDirectory();
        using var secondRoot = new TempDirectory();
        await using var first = await StartAsync("success", firstRoot);
        await using var second = await StartAsync("success", secondRoot);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        await first.DisposeAsync();
        var result = await second.Client.CallToolAsync("fixture", new Dictionary<string, object?>());
        Assert.True(LvtProtocol.RequireSuccess("fixture", result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentDisposalsShareTheSameCleanupTask()
    {
        using var directory = new TempDirectory();
        var server = await StartAsync("success", directory);
        var pid = server.ProcessId;
        var started = server.ProcessStartUtc;
        var first = server.DisposeAsync().AsTask();
        var second = server.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Task.WhenAll(first, second);
        AssertStopped(pid, started);
    }

    private static Task<OwnedStdioMcpClient> StartAsync(string mode, TempDirectory directory,
        TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(GatewayFixtureRun).Assembly.Location);
        start.ArgumentList.Add("--stdio-fixture");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory.Combine("identity.json"));
        start.Environment["OPENCLAW_TRAY_DATA_DIR"] = directory.Combine("profile");
        start.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = directory.Combine("roaming");
        start.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = directory.Combine("local");
        return OwnedStdioMcpClient.StartAsync(start, timeout ?? TimeSpan.FromSeconds(15), cancellation);
    }

    private static async Task WaitIdentityAsync(TempDirectory directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(directory.Combine("identity.json"))) await Task.Delay(20, deadline.Token);
    }

    private static (int Pid, DateTime Started) ReadIdentity(TempDirectory directory)
    {
        using var identity = JsonDocument.Parse(File.ReadAllText(directory.Combine("identity.json")));
        return (identity.RootElement.GetProperty("pid").GetInt32(), identity.RootElement.GetProperty("startUtc").GetDateTime());
    }

    private static void AssertStopped(int pid, DateTime started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited || process.StartTime.ToUniversalTime() != started, "The owned child is still running.");
        }
        catch (ArgumentException) { }
    }
}
