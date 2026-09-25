namespace OpenClawTray.Services;

internal static class StoreMigrationListing
{
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
