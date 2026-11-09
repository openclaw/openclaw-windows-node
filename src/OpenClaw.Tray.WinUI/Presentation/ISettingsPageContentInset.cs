// Keeps page backdrops full-frame. Implement ApplyTopInset on the retained content viewport.
namespace OpenClawTray.Presentation;

/// <summary>Optional page-owned content allocation beneath window history controls.</summary>
internal interface ISettingsPageContentInset
{
    /// <summary>
    /// Applies the total shell/embedded-host inset, including zero when leaving an allocation.
    /// Keep backdrops full-frame and update content without remounting. Calls may precede Loaded;
    /// retain the value and apply it when ready.
    /// </summary>
    void ApplyTopInset(double topInset);
}
