using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class ConnectionDiagnosticsTests
{
    private readonly FakeClock _clock = new();
    private readonly ConnectionDiagnostics _diag;

    public ConnectionDiagnosticsTests()
    {
        _diag = new ConnectionDiagnostics(capacity: 5, clock: _clock);
    }

    [Fact]
    public void Record_AddsEvent()
    {
        _diag.Record("test", "hello");
        Assert.Equal(1, _diag.Count);
        var all = _diag.GetAll();
        Assert.Single(all);
        Assert.Equal("test", all[0].Category);
        Assert.Equal("hello", all[0].Message);
    }

    [Fact]
    public void Record_UsesClockTimestamp()
    {
        var ts = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _clock.Now = ts;
        _diag.Record("test", "msg");
        Assert.Equal(ts, _diag.GetAll()[0].Timestamp);
    }

    [Fact]
    public void RingBuffer_OverflowDropsOldest()
    {
        for (int i = 0; i < 7; i++)
            _diag.Record("test", $"msg-{i}");

        Assert.Equal(5, _diag.Count);
        var all = _diag.GetAll();
        Assert.Equal("msg-2", all[0].Message); // oldest kept
        Assert.Equal("msg-6", all[4].Message); // newest
    }

    [Fact]
    public void GetRecent_ReturnsLastN()
    {
        for (int i = 0; i < 5; i++)
            _diag.Record("test", $"msg-{i}");

        var recent = _diag.GetRecent(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("msg-2", recent[0].Message);
        Assert.Equal("msg-4", recent[2].Message);
    }

    [Fact]
    public void GetRecent_ClampedToCount()
    {
        _diag.Record("test", "only-one");
        var recent = _diag.GetRecent(100);
        Assert.Single(recent);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        _diag.Record("test", "msg");
        _diag.Clear();
        Assert.Equal(0, _diag.Count);
        Assert.Empty(_diag.GetAll());
    }

    [Fact]
    public void RecordStateChange_RecordsTransition()
    {
        _diag.RecordStateChange(OverallConnectionState.Idle, OverallConnectionState.Connecting);
        var all = _diag.GetAll();
        Assert.Single(all);
        Assert.Equal("state", all[0].Category);
        Assert.Contains("Idle", all[0].Message);
        Assert.Contains("Connecting", all[0].Message);
    }

    [Fact]
    public void RecordCredentialResolution_WithCredential()
    {
        var cred = new GatewayCredential("tok", false, "identity.DeviceToken");
        _diag.RecordCredentialResolution(cred);
        var all = _diag.GetAll();
        Assert.Equal("credential", all[0].Category);
        Assert.Contains("identity.DeviceToken", all[0].Message);
    }

    [Fact]
    public void RecordCredentialResolution_Null()
    {
        _diag.RecordCredentialResolution(null);
        var all = _diag.GetAll();
        Assert.Contains("No credential", all[0].Message);
    }

    [Fact]
    public void RecordWebSocketEvent_RecordsEvent()
    {
        _diag.RecordWebSocketEvent("Connected", "wss://test");
        var all = _diag.GetAll();
        Assert.Equal("websocket", all[0].Category);
        Assert.Equal("Connected", all[0].Message);
        Assert.Equal("wss://test", all[0].Detail);
    }

    [Fact]
    public void EventRecorded_FiresOnRecord()
    {
        ConnectionDiagnosticEvent? fired = null;
        _diag.EventRecorded += (s, e) => fired = e;
        _diag.Record("test", "msg");
        Assert.NotNull(fired);
        Assert.Equal("msg", fired.Message);
    }

    [Fact]
    public void Constructor_ThrowsOnZeroCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionDiagnostics(0));
    }

    private sealed class FakeClock : IClock
    {
        public DateTime Now { get; set; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Now;
    }
}
