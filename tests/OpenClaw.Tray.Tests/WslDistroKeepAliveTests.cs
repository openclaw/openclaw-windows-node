using System.IO;
using System.Linq;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavioral tests for <see cref="WslDistroKeepAlive"/>. The class is global static
/// state, so each test resets the test seams in a try/finally to keep tests
/// independent.
/// </summary>
public class WslDistroKeepAliveTests
{
    [Fact]
    public void EnsureStarted_NoMarker_SpawnsAndPersistsMarker()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();
        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");

            Assert.Single(host.SpawnCalls);
            Assert.Equal("OpenClawGateway", host.SpawnCalls[0]);
            Assert.True(File.Exists(Path.Combine(temp.Path, "OpenClawGateway.json")),
                "Marker file should be written after a fresh spawn.");
        });
    }

    [Fact]
    public void EnsureStarted_AdoptsExistingProcess_WhenMarkerMatches()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            Assert.Single(host.SpawnCalls);

            // Second call should adopt — same host, same alive PID, marker still on disk.
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            Assert.Single(host.SpawnCalls); // Still only one spawn.
        });
    }

    [Fact]
    public void EnsureStarted_RecycledPid_SpawnsFresh()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            Assert.Single(host.SpawnCalls);
            var firstPid = host.LastSpawnedPid;

            // Simulate PID recycled to an unrelated process: same PID is reported alive
            // but with a different start time + different name.
            host.OverrideIdentity(firstPid, new KeepAliveProcessIdentity("notepad", System.DateTime.UtcNow.AddHours(1)));

            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            Assert.Equal(2, host.SpawnCalls.Count);
        });
    }

    [Fact]
    public void EnsureStarted_DeadPid_SpawnsFresh()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            var firstPid = host.LastSpawnedPid;

            // Marker still on disk but the process has exited.
            host.KillSilently(firstPid);

            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            Assert.Equal(2, host.SpawnCalls.Count);
        });
    }

    [Fact]
    public void EnsureStarted_CorruptMarker_RecoversBySpawning()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            File.WriteAllText(Path.Combine(temp.Path, "OpenClawGateway.json"), "{not valid json");
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");

            Assert.Single(host.SpawnCalls);
            // Marker should be overwritten with a valid record.
            var json = File.ReadAllText(Path.Combine(temp.Path, "OpenClawGateway.json"));
            Assert.Contains("\"DistroName\"", json);
        });
    }

    [Fact]
    public void Stop_MatchingMarker_KillsAndDeletesMarker()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            var pid = host.LastSpawnedPid;

            WslDistroKeepAlive.Stop("OpenClawGateway");

            Assert.Contains(pid, host.KillCalls);
            Assert.False(File.Exists(Path.Combine(temp.Path, "OpenClawGateway.json")));
        });
    }

    [Fact]
    public void Stop_StaleMarker_DeletesMarker_DoesNotKill()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            var pid = host.LastSpawnedPid;

            // Simulate PID recycled to unrelated process — Stop must NOT kill it.
            host.OverrideIdentity(pid, new KeepAliveProcessIdentity("explorer", System.DateTime.UtcNow.AddHours(2)));

            WslDistroKeepAlive.Stop("OpenClawGateway");

            Assert.DoesNotContain(pid, host.KillCalls);
            Assert.False(File.Exists(Path.Combine(temp.Path, "OpenClawGateway.json")));
        });
    }

    [Fact]
    public void Stop_NoMarker_IsNoOp()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.Stop("OpenClawGateway");

            Assert.Empty(host.KillCalls);
            Assert.Empty(host.SpawnCalls);
        });
    }

    [Fact]
    public void EnsureStarted_NullOrEmptyDistro_IsNoOp()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("");
            WslDistroKeepAlive.EnsureStarted("   ");
            WslDistroKeepAlive.EnsureStarted(null!);

            Assert.Empty(host.SpawnCalls);
            Assert.False(Directory.EnumerateFiles(temp.Path).Any());
        });
    }

    [Fact]
    public void EnsureStarted_DistroNameWithInvalidPathChars_SanitizesMarkerFilename()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            // Defensive: invalid filename chars must be replaced so we never write
            // outside the marker directory.
            WslDistroKeepAlive.EnsureStarted("Custom/Distro:Name");

            Assert.Single(host.SpawnCalls);
            var markers = Directory.EnumerateFiles(temp.Path, "*.json").ToList();
            var marker = Assert.Single(markers);
            Assert.DoesNotContain('/', Path.GetFileName(marker));
            Assert.DoesNotContain(':', Path.GetFileName(marker));
        });
    }

    [Fact]
    public void StopAll_StopsEveryRecordedDistro()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var host = new FakeKeepAliveHost();

        WithSeams(temp.Path, host, () =>
        {
            WslDistroKeepAlive.EnsureStarted("OpenClawGateway");
            WslDistroKeepAlive.EnsureStarted("OpenClawGatewayE2E");

            WslDistroKeepAlive.StopAll();

            Assert.Equal(2, host.KillCalls.Count);
            Assert.False(Directory.EnumerateFiles(temp.Path, "*.json").Any());
        });
    }

    /// <summary>
    /// Wraps a test body so the global test seams on <see cref="WslDistroKeepAlive"/>
    /// are always reset, even if the body throws.
    /// </summary>
    private static void WithSeams(string markerDirectory, IWslKeepAliveProcessHost host, Action body)
    {
        try
        {
            WslDistroKeepAlive.__TestMarkerDirectory = markerDirectory;
            WslDistroKeepAlive.__TestHost = host;
            body();
        }
        finally
        {
            WslDistroKeepAlive.__ResetForTests();
        }
    }

    /// <summary>
    /// In-memory keepalive host. Simulates Windows process lifecycle without forking
    /// real processes: each <see cref="Spawn"/> generates a synthetic PID + start time,
    /// and <see cref="TryGetProcessIdentity"/> returns identity records that callers
    /// can override per-PID to simulate PID recycling.
    /// </summary>
    private sealed class FakeKeepAliveHost : IWslKeepAliveProcessHost
    {
        private int _nextPid = 1000;
        private readonly Dictionary<int, KeepAliveProcessIdentity> _liveProcesses = new();

        public List<string> SpawnCalls { get; } = new();
        public List<int> KillCalls { get; } = new();
        public int LastSpawnedPid { get; private set; }

        public KeepAliveProcessIdentity Spawn(string distroName)
        {
            SpawnCalls.Add(distroName);
            var pid = _nextPid++;
            var identity = new KeepAliveProcessIdentity(
                "wsl",
                System.DateTime.UtcNow.AddTicks(pid));
            _liveProcesses[pid] = identity;
            LastSpawnedPid = pid;
            return identity;
        }

        public bool TryGetProcessIdentity(int pid, out KeepAliveProcessIdentity identity)
        {
            if (_liveProcesses.TryGetValue(pid, out var found))
            {
                identity = found;
                return true;
            }
            identity = default!;
            return false;
        }

        public void Kill(int pid)
        {
            KillCalls.Add(pid);
            _liveProcesses.Remove(pid);
        }

        public int GetCurrentPidForLastSpawn() => LastSpawnedPid;

        public void OverrideIdentity(int pid, KeepAliveProcessIdentity identity)
            => _liveProcesses[pid] = identity;

        public void KillSilently(int pid) => _liveProcesses.Remove(pid);
    }
}
