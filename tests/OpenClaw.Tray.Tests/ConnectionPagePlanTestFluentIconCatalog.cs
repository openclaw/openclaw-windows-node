namespace OpenClawTray.Helpers;

// ConnectionPagePlan is pure logic but references production glyph constants.
// Tray.Tests links the plan without the native WinUI icon helper, so these
// stable placeholders let the actual projection execute in the pure test lane.
internal static class FluentIconCatalog
{
    internal const string Lock = "lock";
    internal const string StatusErr = "error";
    internal const string StatusOk = "ok";
    internal const string StatusWarn = "warn";
    internal const string Sync = "sync";
    internal const string System = "system";
}
