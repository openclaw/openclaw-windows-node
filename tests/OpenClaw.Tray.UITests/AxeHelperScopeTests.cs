using Axe.Windows.Core.Enums;
using System.Drawing;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AxeHelperScopeCollection
{
    public const string Name = "Axe helper scope";
}

[Collection(AxeHelperScopeCollection.Name)]
public sealed class AxeHelperScopeTests
{
    [Theory]
    [InlineData(2207, 1008, 986, 1.75, 2246)]
    [InlineData(1787, 754, 760, 1.75, 1777)]
    [InlineData(1750, 480, 738.86, 1.75, 1750)]
    public void ClosedLoopResize_UsesMeasuredHostDeltaAndPreservesShellMinimum(
        int currentPixels, double desiredHostDip, double measuredHostDip, double scale, int expectedPixels) =>
        Assert.Equal(expectedPixels, OwnedWindowCapture.CorrectedWindowWidthPixels(currentPixels, desiredHostDip, measuredHostDip, scale));

    [Theory]
    [InlineData(379, 395)]
    [InlineData(380, 396)]
    public void CaptureGeometry_AdmitsTheExactQualifiedNarrowFrame(int clientWidth, int windowWidth)
    {
        var qualified = CaptureGeometry() with
        {
            ClientPixels = new Rectangle(0, 0, clientWidth, 900),
            WindowPixels = new Rectangle(40, 40, windowWidth, 939),
        };
        OwnedWindowCapture.AssertCaptureGeometry(qualified.WindowPixels, qualified, qualified with { });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OwnedWindowCapture.AssertCaptureGeometry(qualified.WindowPixels));
    }

    [Theory]
    [InlineData(400, 300, true)]
    [InlineData(399, 300, false)]
    [InlineData(400, 299, false)]
    [InlineData(0, 300, false)]
    public void CaptureGeometry_OrdinaryThresholdsRemainUnchanged(int width, int height, bool admitted)
    {
        var bounds = new Rectangle(40, 40, width, height);
        if (admitted) OwnedWindowCapture.AssertCaptureGeometry(bounds);
        else Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => OwnedWindowCapture.AssertCaptureGeometry(bounds));
    }

    [Theory]
    [InlineData("hwnd")]
    [InlineData("pid")]
    [InlineData("process-start")]
    [InlineData("dpi")]
    [InlineData("client")]
    [InlineData("frame-size")]
    [InlineData("frame-position")]
    public void CaptureGeometry_RejectsIdentityOrGeometryDrift(string change)
    {
        var qualified = CaptureGeometry();
        var measured = change switch
        {
            "hwnd" => qualified with { Hwnd = "0x456" },
            "pid" => qualified with { ProcessId = 202 },
            "process-start" => qualified with { ProcessStartedUtc = qualified.ProcessStartedUtc.AddSeconds(1) },
            "dpi" => qualified with { Dpi = 120 },
            "client" => qualified with { ClientPixels = new Rectangle(0, 0, 380, 900) },
            "frame-size" => qualified with { WindowPixels = new Rectangle(40, 40, 396, 939) },
            "frame-position" => qualified with { WindowPixels = new Rectangle(41, 40, 395, 939) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OwnedWindowCapture.AssertCaptureGeometry(measured.WindowPixels, qualified, measured));
    }

    [Fact]
    public void CaptureGeometry_RequiresMeasurementAndRejectsMismatchedBounds()
    {
        var qualified = CaptureGeometry();
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OwnedWindowCapture.AssertCaptureGeometry(qualified.WindowPixels, qualified));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OwnedWindowCapture.AssertCaptureGeometry(new Rectangle(40, 40, 396, 939), qualified, qualified));
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(379, 0)]
    [InlineData(396, 900)]
    [InlineData(379, 940)]
    public void CaptureGeometry_RejectsEmptyOrClippedQualifiedClient(int width, int height)
    {
        var invalid = CaptureGeometry() with { ClientPixels = new Rectangle(0, 0, width, height) };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OwnedWindowCapture.AssertCaptureGeometry(invalid.WindowPixels, invalid, invalid));
    }

    private static OwnedWindowCapture.WindowGeometry CaptureGeometry() => new(
        "0x123", 101, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), 96,
        new Rectangle(40, 40, 395, 939), new Rectangle(0, 0, 379, 900), new Size(1940, 1100));

    [Theory]
    [InlineData(101, 101, false, false)]
    [InlineData(101, 101, true, false)]
    [InlineData(101, 202, false, true)]
    [InlineData(101, 202, true, false)]
    [InlineData(101, 0, false, false)]
    public void Scope_RetainsOwnedHostedAndUnknownProviders(int expected, int actual, bool hosted, bool excluded) =>
        Assert.Equal(excluded, AxeHelper.IsForeignUnhostedProcess(expected, actual, hosted));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Scan_RejectsMissingOrInvalidRootBeforeScannerRuns(long handle) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AxeHelper.AssertNoAccessibilityErrors(Environment.ProcessId, new IntPtr(handle)));

    [Theory]
    [InlineData("[pid:68088,providerId:0x5B0FF0 Hwnd(parent link):Unidentified Provider (unmanaged:coreclr.dll)]", "[2A,5B0FF0]", "5967856")]
    [InlineData("[pid:68088,providerId:0x6A0F20 Hwnd(parent link):Unidentified Provider (unmanaged:coreclr.dll)]", "[2A,6A0F20]", "6950688")]
    [InlineData("[pid:68088,providerId:0x9E12C4 Main:Nested [hr=0x80040201]; Hwnd(parent link):Unidentified Provider (unmanaged:coreclr.dll)]", "[2A,9E12C4]", null)]
    public void RecordedProxyShapes_RequireProvenAbsenceOfContent(string provider, string runtimeId, string? window)
    {
        var properties = ProxyProperties();
        properties["ProviderDescription"] = provider;
        properties["RuntimeId"] = runtimeId;
        if (window is not null) properties["NativeWindowHandle"] = window;
        Assert.True(AxeHelper.IsEmptyProxyViolation(RuleId.BoundingRectangleNotNull, properties, false));
        Assert.False(AxeHelper.IsEmptyProxyViolation(RuleId.BoundingRectangleNotNull, properties, true));
        Assert.False(AxeHelper.IsEmptyProxyViolation(RuleId.BoundingRectangleNotNull, properties, null));
    }

    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("AutomationId", "AutomationEditorName")]
    [InlineData("ControlType", "Edit")]
    [InlineData("ControlType", "50004")]
    [InlineData("LogicalSize", "[w=200,h=40]")]
    [InlineData("IsEnabled", "True")]
    [InlineData("ProviderDescription", "Windows XAML")]
    public void EmptyProxyException_NeverHidesNamedTypedSizedOrNormalControls(string key, string value)
    {
        var properties = ProxyProperties();
        properties[key] = value;
        Assert.False(AxeHelper.IsEmptyProxyViolation(RuleId.BoundingRectangleNotNull, properties, false));
    }

    [Theory]
    [InlineData(RuleId.BoundingRectangleSizeReasonable)]
    [InlineData(RuleId.ClickablePointOnScreen)]
    public void EmptyProxyException_LeavesOtherRulesStrict(RuleId rule) =>
        Assert.False(AxeHelper.IsEmptyProxyViolation(rule, ProxyProperties(), false));

    [Fact]
    public void RuntimeId_MatchesExactAxeHexadecimalIdentity()
    {
        var properties = ProxyProperties();
        properties["RuntimeId"] = "[2A,5B0FF0]";
        Assert.Equal(new[] { 42, 5967856 }, AxeHelper.SnapshotRuntimeId(properties));
        properties["RuntimeId"] = "[2A,not-an-id]";
        Assert.Null(AxeHelper.SnapshotRuntimeId(properties));
    }

    [Theory]
    [InlineData(120, 200, 80, 0, true, false, "top")]
    [InlineData(120, 600, 80, 0, true, false, "bottom")]
    [InlineData(120, 601, 80, 0, true, false, "bottom")]
    [InlineData(100, 220, 0, 80, false, true, "left")]
    [InlineData(400, 220, 0, 80, false, true, "right")]
    public void ScrollViewportClip_ClassifiesOnlyDegenerateLinesOnScrollableEdges(
        double x, double y, double width, double height, bool vertical, bool horizontal, string edge)
    {
        var result = AxeHelper.ClassifyScrollViewportClip(RuleId.BoundingRectangleSizeReasonable,
            new(x, y, width, height), false, new System.Windows.Rect(100, 200, 300, 400), vertical, horizontal);
        Assert.True(result.Excluded);
        Assert.Equal(edge, result.Reason);
    }

    [Theory]
    [InlineData(120, 400, 80, 0, true, false, "line is not on viewport edge")]
    [InlineData(120, 600, 80, 20, true, false, "not a finite degenerate line")]
    [InlineData(120, 600, 0, 0, true, false, "not a finite degenerate line")]
    [InlineData(120, 601.01, 80, 0, true, false, "line is not on viewport edge")]
    [InlineData(120, 198.99, 80, 0, true, false, "line is not on viewport edge")]
    [InlineData(120, 600, 80, 0, false, true, "clipped axis is not scrollable")]
    [InlineData(400, 220, 0, 80, true, false, "clipped axis is not scrollable")]
    [InlineData(98, 600, 80, 0, true, false, "other-axis extent exceeds viewport")]
    [InlineData(320, 600, 82, 0, true, false, "other-axis extent exceeds viewport")]
    [InlineData(400, 198, 0, 80, false, true, "other-axis extent exceeds viewport")]
    [InlineData(400, 522, 0, 80, false, true, "other-axis extent exceeds viewport")]
    public void ScrollViewportClip_RetainsInteriorSizedPointMismatchedAndOverflowingPeers(
        double x, double y, double width, double height, bool vertical, bool horizontal, string reason)
    {
        var result = AxeHelper.ClassifyScrollViewportClip(RuleId.BoundingRectangleSizeReasonable,
            new(x, y, width, height), false, new System.Windows.Rect(100, 200, 300, 400), vertical, horizontal);
        Assert.False(result.Excluded);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void ScrollViewportClip_RequiresAnAncestorViewport()
    {
        var result = AxeHelper.ClassifyScrollViewportClip(RuleId.BoundingRectangleSizeReasonable,
            new(120, 600, 80, 0), false, null, true, false);
        Assert.False(result.Excluded);
        Assert.Equal("no finite scroll viewport", result.Reason);
    }

    [Theory]
    [InlineData(RuleId.BoundingRectangleNotNull)]
    [InlineData(RuleId.ClickablePointOnScreen)]
    public void ScrollViewportClip_LeavesOtherRulesStrict(RuleId rule)
    {
        var result = AxeHelper.ClassifyScrollViewportClip(rule,
            new(120, 600, 80, 0), false, new System.Windows.Rect(100, 200, 300, 400), true, false);
        Assert.False(result.Excluded);
        Assert.Equal("different rule", result.Reason);
    }

    [Fact]
    public void ScrollViewportClip_DoesNotReclassifyOffscreenPeers()
    {
        var result = AxeHelper.ClassifyScrollViewportClip(RuleId.BoundingRectangleSizeReasonable,
            new(120, 600, 80, 0), true, new System.Windows.Rect(100, 200, 300, 400), true, false);
        Assert.False(result.Excluded);
        Assert.Equal("already offscreen", result.Reason);
    }

    private static Dictionary<string, string> ProxyProperties() => new()
    {
        ["LogicalSize"] = "[w=0,h=0]",
        ["IsEnabled"] = "False",
        ["IsOffscreen"] = "False",
        ["ProviderDescription"] = "[pid:68088,providerId:0x5B0FF0 Hwnd(parent link):Unidentified Provider (unmanaged:coreclr.dll)]",
    };
}
