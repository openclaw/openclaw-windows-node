using OpenClawTray.Presentation.Adapters;

namespace OpenClaw.Tray.UITests;

public sealed class HubCommandIconConverterTests
{
    [Theory]
    [InlineData("unknown-icon")]
    [InlineData(42)]
    [InlineData(null)]
    public void Convert_UnknownValues_ReturnSettingsGlyph(object? value)
    {
        var converter = new HubCommandIconConverter();

        var glyph = converter.Convert(value!, typeof(string), null!, string.Empty);

        Assert.Equal("\uE713", Assert.IsType<string>(glyph));
    }
}
