using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests.Helpers;

public class TrayTooltipFormatterTests
{
    [Fact]
    public void FitShellTooltip_RemovesLineBreaks()
    {
        var tooltip = TrayTooltipFormatter.FitShellTooltip("OpenClaw\r\nConnected\tReady");

        Assert.Equal("OpenClaw Connected Ready", tooltip);
    }

    [Fact]
    public void FitShellTooltip_CapsShellTooltipLength()
    {
        var tooltip = TrayTooltipFormatter.FitShellTooltip(new string('x', 200));

        Assert.Equal(TrayTooltipFormatter.MaxShellTooltipLength, tooltip.Length);
        Assert.EndsWith("...", tooltip);
    }
}
