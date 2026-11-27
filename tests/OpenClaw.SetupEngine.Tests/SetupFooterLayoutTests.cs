namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupFooterLayoutTests
{
    [Theory]
    // Back, Retry and Close at the 960-DIP minimum: 960 minus the 405-DIP companion and 48-DIP gutter leaves 507.
    [InlineData(new double[] { 180, 180, 180 }, 507, false)]
    // The same failure footer at the 1120x720 reference size.
    [InlineData(new double[] { 180, 180, 180 }, 600, true)]
    // Back and Next on a 1440x900 display at 175%: an 823-DIP window leaves 395 DIPs.
    [InlineData(new double[] { 180, 180 }, 395, true)]
    // A localized label that outgrows the shared minimum still stacks instead of overflowing.
    [InlineData(new double[] { 180, 240 }, 395, false)]
    // Sub-pixel layout rounding does not stack an exactly fitting row.
    [InlineData(new double[] { 180, 180 }, 367.6, true)]
    [InlineData(new double[] { 180, 180, 180 }, double.PositiveInfinity, true)]
    [InlineData(new double[0], 0, true)]
    public void FitsOnOneRow_StacksOnlyWhenTheRowExceedsTheColumn(double[] widths, double available, bool fits) =>
        Assert.Equal(fits, SetupFooterLayout.FitsOnOneRow(widths, spacing: 8, available));

    [Fact]
    public void Extents_IncludeOneSpacingBetweenEachVisibleAction()
    {
        Assert.Equal(556, SetupFooterLayout.RowWidth([180, 180, 180], 8));
        Assert.Equal(136, SetupFooterLayout.StackHeight([40, 40, 40], 8));
        Assert.Equal(0, SetupFooterLayout.RowWidth([], 8));
    }
}
