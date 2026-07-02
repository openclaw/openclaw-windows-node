using System;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Dev-only preview routing for the setup window. Lets <c>OPENCLAW_SETUP_PREVIEW_PAGE</c>
/// open a single page directly with sample content (no pipeline, no gateway) for visual
/// iteration during development.
///
/// In <b>Release</b> builds this is fully inert: the environment variable is never read,
/// so the preview route can never bypass the setup run lock or the real install pipeline
/// in production. The gating lives here so call sites stay simple and there is exactly one
/// place that reads the variable.
/// </summary>
internal static class SetupPreview
{
    private const string EnvVar = "OPENCLAW_SETUP_PREVIEW_PAGE";

    /// <summary>
    /// The requested preview page (lower-cased, trimmed), or <c>null</c> when preview mode
    /// is off. Always <c>null</c> in Release builds.
    /// </summary>
    public static string? RequestedPage
    {
#if DEBUG
        get
        {
            var page = Environment.GetEnvironmentVariable(EnvVar);
            return string.IsNullOrWhiteSpace(page) ? null : page.Trim().ToLowerInvariant();
        }
#else
        get => null;
#endif
    }

    /// <summary>True when a preview page is active. Always <c>false</c> in Release builds.</summary>
    public static bool IsActive => RequestedPage is not null;
}
