using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Caches WinRT interop objects that are expensive to create but immutable once constructed.
/// FontFamily in particular crosses the managed→WinRT boundary on every allocation;
/// caching by name avoids creating thousands of identical instances during re-renders.
/// </summary>
internal static class WinRTCache
{
    private static readonly ConcurrentDictionary<string, FontFamily> _fontFamilies = new();

    /// <summary>
    /// Returns a cached FontFamily for the given family name.
    /// Thread-safe — concurrent callers with the same name get the same instance.
    /// </summary>
    internal static FontFamily GetFontFamily(string familyName) =>
        _fontFamilies.GetOrAdd(familyName, static name => new FontFamily(name));
}
