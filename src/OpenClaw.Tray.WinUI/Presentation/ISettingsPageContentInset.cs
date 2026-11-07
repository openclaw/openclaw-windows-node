// Lets a Settings page allocate shell chrome inside its content while retaining its full-frame backdrop.
// Usage: implement ApplyTopInset to update the retained content viewport, not the page root.
namespace OpenClawTray.Presentation;

/// <summary>Optional page-owned content allocation retaining full-frame backdrops beneath history controls.</summary>
internal interface ISettingsPageContentInset
{
    /// <summary>
    /// Applies the total shell and embedded-host inset, including zero when leaving an allocation.
    /// Keep backdrops and overlay surfaces full-frame; update content without remounting.
    /// Calls may precede Loaded or visual mounting; retain the value and apply it when ready.
    /// </summary>
    void ApplyTopInset(double topInset);
}
