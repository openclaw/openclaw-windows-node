using System.Reflection;
using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the <c>FluentIconCatalog</c> contract: every advertised glyph is a
/// single Unicode Private Use Area character (or the documented Brand
/// emoji), and the helper builder uses the SymbolThemeFontFamily resource.
///
/// We parse the source rather than reflect on the assembly because
/// <c>OpenClaw.Tray.Tests</c> is a pure net10.0 project that doesn't
/// reference the WinUI tray assembly directly.
/// </summary>
public sealed class FluentIconCatalogTests
{
    private static readonly string[] ExpectedConstants =
    {
        "StatusOk", "StatusWarn", "StatusErr",
        "Sessions", "Approvals", "Devices", "Hostname", "Permissions",
        "Browser", "Camera", "Canvas", "Screen", "Location", "Voice", "Speech", "System", "Operator",
        "Dashboard", "OpenInBrowser", "Chat", "CanvasAct", "VoiceAct", "Settings", "QuickSend",
        "Setup", "About", "Exit",
        "Add", "Back", "Sync", "Lock", "Plug", "MoreOverflow",
        "People", "Money", "ServerEnvironment", "CapabilityOff", "Channels",
        "ChevronR", "Check",
        "Brand",
        // Diagnostics surface (see src/OpenClaw.Tray.WinUI/Pages/DebugPage.xaml).
        "Bug", "Briefcase", "Folder", "Copy", "Document", "Refresh", "Reset", "Clear", "Develop",
    };

    private static string ReadCatalogSource()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Helpers", "FluentIconCatalog.cs");
        return File.ReadAllText(path);
    }

    private static IDictionary<string, string> ParseConstants(string source)
    {
        // Matches:   public const string Name = "\uXXXX";   or
        //            public const string Name = "🦞";
        var rx = new Regex(
            @"public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>(?:\\u[0-9A-Fa-f]{4}|[^""\\]|\\.)*)"";",
            RegexOptions.Compiled);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(source))
        {
            map[m.Groups["name"].Value] = Regex.Unescape(m.Groups["value"].Value);
        }
        return map;
    }

    [Fact]
    public void Catalog_ExposesEveryAdvertisedConstant()
    {
        var src = ReadCatalogSource();
        var map = ParseConstants(src);
        foreach (var name in ExpectedConstants)
        {
            Assert.True(map.ContainsKey(name), $"FluentIconCatalog.{name} is missing");
            Assert.False(string.IsNullOrEmpty(map[name]), $"FluentIconCatalog.{name} is empty");
        }
    }

    [Fact]
    public void PuaConstants_AreSingleCharacterInPrivateUseArea()
    {
        var src = ReadCatalogSource();
        var map = ParseConstants(src);
        foreach (var name in ExpectedConstants)
        {
            if (name == "Brand")
                continue; // Brand is an emoji surrogate pair by design.

            Assert.True(map.TryGetValue(name, out var value),
                $"FluentIconCatalog.{name} not found in source");
            Assert.True(value!.Length == 1,
                $"FluentIconCatalog.{name} should be a single character; got {value.Length}");
            var c = value[0];
            Assert.True(c >= '\uE000' && c <= '\uF8FF',
                $"FluentIconCatalog.{name} codepoint U+{(int)c:X4} is outside PUA");
        }
    }

    [Fact]
    public void Catalog_DefinesBuildHelper()
    {
        var src = ReadCatalogSource();
        Assert.Contains("public static FontIcon Build", src);
        Assert.Contains("SymbolThemeFontFamily", src);
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}

/// <summary>
/// Guard against regressions in <c>BuildTrayMenuPopup</c>: section order,
/// theme-brush usage, presence of a Permissions submenu for the local
/// device, and routing of the new "about" action.
/// </summary>
public sealed class TrayMenuPopupCompositionTests
{
    private static string ReadAppXaml()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        return File.ReadAllText(path);
    }

    private static string ReadStateBuilder()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "TrayMenuStateBuilder.cs");
        return File.ReadAllText(path);
    }

    private static string ReadHubWindowXaml()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        return File.ReadAllText(path);
    }

    [Fact]
    public void BuildTrayMenuPopup_NoHardcodedColors()
    {
        var src = ReadAppXaml();
        // Anything in the tray menu must route through theme brushes; the
        // refactor explicitly forbids Microsoft.UI.Colors.* sneaking back
        // into the menu builders.
        Assert.DoesNotContain("Microsoft.UI.Colors.LimeGreen", src);
        Assert.DoesNotContain("Microsoft.UI.Colors.Orange", src);
        Assert.DoesNotContain("Microsoft.UI.Colors.OrangeRed", src);
        Assert.DoesNotContain("Microsoft.UI.Colors.Gray", src);
        Assert.DoesNotContain("Microsoft.UI.Colors.Red", src);
    }

    [Fact]
    public void BuildTrayMenuPopup_UsesThemeBrushes()
    {
        var src = ReadStateBuilder();
        Assert.Contains("SystemFillColorSuccessBrush", src);
        Assert.Contains("SystemFillColorCautionBrush", src);
        Assert.Contains("SystemFillColorNeutralBrush", src);
        Assert.Contains("SystemFillColorCriticalBrush", src);
    }

    [Fact]
    public void BuildTrayMenuPopup_SectionOrder_GatewayThenDevicesThenSessions()
    {
        var src = ReadStateBuilder();
        var gateway = src.IndexOf("// ── Gateway Section ──", StringComparison.Ordinal);
        var devices = src.IndexOf("// ── Connected Devices (moved above Sessions) ──", StringComparison.Ordinal);
        var sessions = src.IndexOf("// ── Sessions (now below Devices) ──", StringComparison.Ordinal);
        var actions = src.IndexOf("// ── Actions ──", StringComparison.Ordinal);
        var footer = src.IndexOf("// ── Footer ──", StringComparison.Ordinal);

        Assert.True(gateway > 0, "Gateway section marker missing");
        Assert.True(devices > gateway, "Devices must follow Gateway");
        Assert.True(sessions > devices, "Sessions must follow Devices");
        Assert.True(actions > sessions, "Actions must follow Sessions");
        Assert.True(footer > actions, "Footer must follow Actions");
    }

    [Fact]
    public void BuildTrayMenuPopup_EmitsPermissionsSubmenuForLocalDevice()
    {
        var src = ReadStateBuilder();
        Assert.Contains("BuildPermissionsFlyoutItems", src);
        Assert.Contains("FluentIconCatalog.Permissions", src);
    }

    [Fact]
    public void BuildTrayMenuPopup_RoutesAboutAction()
    {
        Assert.Contains("\"About\", FluentIconCatalog.Build(FluentIconCatalog.About), \"about\"", ReadStateBuilder());
        Assert.Contains("case \"about\":", ReadAppXaml());
    }

    [Fact]
    public void BuildTrayMenuPopup_BatchesUpdates()
    {
        var src = ReadAppXaml();
        Assert.Contains("menu.BeginUpdate();", src);
        Assert.Contains("menu.EndUpdate();", src);
    }

    [Fact]
    public void BuildTrayMenuPopup_DoesNotEmitInlinePermissionGrid()
    {
        var src = ReadAppXaml();
        // The old 3-column ToggleButton grid was the source of the
        // permission-row regression; ensure it's gone.
        Assert.DoesNotContain("Build compact toggle button grid (3 columns)", src);
    }

    /// <summary>
    /// Regression guard: every static action emitted by the tray menu's
    /// top-level entries (Gateway header, Permissions, Setup, QuickSend, etc.)
    /// must have an explicit case in <c>OnTrayMenuItemClicked</c>. The default
    /// fall-through to <c>ShowHub(action)</c> is convenient but easy to break
    /// silently — these specific actions are user-visible entry points and
    /// should be wired explicitly.
    /// </summary>
    [Fact]
    public void BuildTrayMenuPopup_TopLevelActions_AllHaveExplicitHandlers()
    {
        var src = ReadAppXaml();
        string[] requiredActions =
        {
            "connection",   // gateway card
            "disconnect",   // brand-header button (when connected)
            "reconnect",    // brand-header button (when disconnected)
            "permissions",  // permissions row
            "setup",        // setup/reconfigure row
            "quicksend",    // quicksend row
            "companion",    // footer
            "about",        // footer
            "exit",         // footer
        };

        foreach (var action in requiredActions)
        {
            Assert.True(
                src.Contains($"case \"{action}\":", StringComparison.Ordinal),
                $"OnTrayMenuItemClicked must explicitly handle case \"{action}\"");
        }
    }

    [Fact]
    public void HubWindow_NavigateTo_Normalizes_LegacyNodesTag_BeforeSelectingNavItem()
    {
        var src = ReadHubWindowXaml();

        // Legacy "nodes" deep links must still land on the Instances rail
        // item. The normalization rule lives in NormalizeNavTag, which
        // NavigateTo applies before handing the tag to NavigateInternal
        // (which is what actually highlights the rail item via
        // FindNavItemForTag and calls Frame.Navigate).
        var aliasIndex = src.IndexOf("if (tag == \"nodes\") return \"instances\";", StringComparison.Ordinal);
        var funnelIndex = src.IndexOf("NavigateInternal(NormalizeNavTag(tag))", StringComparison.Ordinal);
        var selectIndex = src.IndexOf("FindNavItemForTag(NavView.MenuItems, tag)", StringComparison.Ordinal);

        Assert.True(aliasIndex >= 0, "NormalizeNavTag must keep legacy 'nodes' deep links pointing at 'instances'.");
        Assert.True(funnelIndex >= 0, "NavigateTo must normalize the tag before routing through NavigateInternal.");
        Assert.True(selectIndex >= 0, "NavigateInternal must select a nav item by tag before falling back to direct navigation.");
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
