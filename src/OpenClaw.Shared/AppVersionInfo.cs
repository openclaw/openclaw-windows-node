using System;
using System.Reflection;

namespace OpenClaw.Shared;

/// <summary>
/// Single source of truth for the OpenClaw Companion app version that is
/// surfaced to users. Reads <see cref="AssemblyInformationalVersionAttribute"/>
/// (or falls back to <c>AssemblyVersion</c>) from the tray executable so every
/// UI/diagnostic/handshake site reports the same number driven by the csproj
/// <c>&lt;Version&gt;</c> property.
/// </summary>
/// <remarks>
/// Under <c>dotnet test</c> and inside CLI siblings, <see cref="Assembly.GetEntryAssembly"/>
/// is the host process (testhost / dotnet), not the tray exe — so we first
/// search the current <see cref="AppDomain"/> for the tray assembly by name.
/// Tests that need a deterministic value can set <see cref="TestOverride"/>.
/// </remarks>
public static class AppVersionInfo
{
    private const string TrayAssemblyName = "OpenClaw.Tray.WinUI";

    private static readonly string _version = ResolveVersion();

    /// <summary>
    /// Test-only override. When non-null, <see cref="Version"/> returns this
    /// value instead of the reflected one, giving tests a deterministic
    /// version string regardless of the host process.
    /// </summary>
    internal static string? TestOverride { get; set; }

    /// <summary>Bare version string, e.g. <c>"0.4.7"</c>.</summary>
    public static string Version => TestOverride ?? _version;

    /// <summary>Version prefixed with <c>v</c>, e.g. <c>"v0.4.7"</c>.</summary>
    public static string DisplayVersion => "v" + Version;

    private static string ResolveVersion()
    {
        try
        {
            var assembly = FindTrayAssembly()
                ?? Assembly.GetEntryAssembly()
                ?? typeof(AppVersionInfo).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return NormalizeSemVer(informational);
            }

            var name = assembly.GetName().Version;
            if (name != null)
            {
                // System.Version uses -1 for unspecified components; coerce to 0.
                var build = Math.Max(0, name.Build);
                var revision = Math.Max(0, name.Revision);
                return revision == 0
                    ? $"{name.Major}.{name.Minor}.{build}"
                    : $"{name.Major}.{name.Minor}.{build}.{revision}";
            }

            return "0.0.0";
        }
        catch
        {
            // Never let the type initializer fail — a TypeInitializationException
            // would poison every future access from every caller.
            return "0.0.0";
        }
    }

    private static Assembly? FindTrayAssembly()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(asm.GetName().Name, TrayAssemblyName, StringComparison.Ordinal))
                return asm;
        }
        return null;
    }

    private static string NormalizeSemVer(string s)
    {
        // Strip SourceLink build metadata, e.g. "0.4.7+abc123" -> "0.4.7".
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);

        // Strip the SemVer pre-release suffix, e.g. "0.4.7-beta.1" -> "0.4.7".
        // This keeps the UI string aligned with Updatum, which compares the
        // numeric AssemblyVersion only. Revisit if pre-release labels should
        // ever be surfaced to users.
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);

        return s;
    }
}

