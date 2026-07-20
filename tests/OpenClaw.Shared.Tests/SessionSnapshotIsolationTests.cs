using System;
using System.IO;
using System.Reflection;
using OpenClaw.Shared;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Regression: the SessionInfo[] snapshot raised through SessionsUpdated (and returned by
/// GetSessionList) must be isolated from the live tracked state. GetSessionListInternal
/// copies the array but its elements were the very SessionInfo instances held in _sessions,
/// which UpdateTrackedSession mutates in place under _sessionsLock — so a subscriber reading
/// a snapshot with no lock could observe fields (CurrentActivity/LastSeen/Status) changing
/// underneath it. Before the fix this test is red: a captured snapshot's CurrentActivity is
/// overwritten by a later update.
/// </summary>
public sealed class SessionSnapshotIsolationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _identityDir;

    public SessionSnapshotIsolationTests(ITestOutputHelper output)
    {
        _output = output;
        _identityDir = Path.Combine(Path.GetTempPath(), "session-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_identityDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_identityDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SessionSnapshot_IsNotAliasedToLiveTrackedState()
    {
        using var client = new OpenClawGatewayClient(
            "ws://127.0.0.1:1", "test-token", new TestLogger(), identityPath: _identityDir);

        var update = typeof(OpenClawGatewayClient).GetMethod(
            "UpdateTrackedSession", BindingFlags.NonPublic | BindingFlags.Instance)!;

        update.Invoke(client, new object[] { "agent:main:main", true, "compiling" });

        var snapshot = client.GetSessionList();
        var captured = Assert.Single(snapshot);
        Assert.Equal("compiling", captured.CurrentActivity);

        // A later update for the same session must not reach back through the earlier snapshot.
        update.Invoke(client, new object[] { "agent:main:main", true, "running-tool" });

        _output.WriteLine($"captured.CurrentActivity after later update = '{captured.CurrentActivity}'");
        Assert.Equal("compiling", captured.CurrentActivity);
    }
}
