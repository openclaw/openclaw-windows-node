namespace OpenClawTray.Services;

internal static class StoreMigrationListing
{
    private const string ReleasesUrl = "https://github.com/openclaw/openclaw-windows-node/releases/latest";

    public static Uri CreateInstallerUri(string architecture) => architecture switch
    {
        "x64" => new Uri(ReleasesUrl + "/download/OpenClawCompanion-Setup-x64.exe"),
        "arm64" => new Uri(ReleasesUrl + "/download/OpenClawCompanion-Setup-arm64.exe"),
        _ => new Uri(ReleasesUrl)
    };

    public static bool TryCreateUri(string? productId, out Uri? uri)
    {
        uri = null;
        if (productId is not { Length: 12 } || productId.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
            return false;
        uri = new Uri($"ms-windows-store://pdp/?ProductId={productId}");
        return true;
    }
}
