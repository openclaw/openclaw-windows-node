namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Abstracts string resource loading so that the localization system
/// can be tested without MRT/ResourceLoader.
/// </summary>
public interface IStringResourceProvider
{
    /// <summary>
    /// Gets a string resource for the given namespace and key in the specified locale.
    /// Returns null if the key is not found for that locale.
    /// </summary>
    string? GetString(string locale, string ns, string key);
}
