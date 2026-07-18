using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Source-contract guards for the WinUI-bound half of the presentation seam (the parts
/// that cannot be compiled into this pure net10 test project): the App composition-root
/// wiring/disposal, the HubWindow activation hook, and the WinUI-free boundary of the
/// Presentation core.
/// </summary>
public sealed class PresentationSeamContractTests
{
    [Fact]
    public void App_BuildsServiceProvider_WithScopeAndBuildValidation()
    {
        var source = ReadAppSources();

        Assert.Contains("AddOpenClawTrayCore", source);
        Assert.Contains("BuildServiceProvider", source);
        Assert.Contains("ValidateScopes = true", source);
        Assert.Contains("ValidateOnBuild = true", source);
    }

    [Fact]
    public void App_DisposesServiceProvider_DuringShutdown()
    {
        var source = ReadAppSources();

        Assert.Contains("\"service provider\"", source);
        Assert.Contains("await services.DisposeAsync()", source);
    }

    [Fact]
    public void App_NullsServiceProviderField_BeforeAwaitingDisposal()
    {
        var source = ReadAppSources();

        // The shutdown must null _services before awaiting DisposeAsync so a queued
        // Frame.Navigated callback cannot resolve the page activator against a disposing
        // provider. Assert the field is nulled ahead of the disposal step.
        var captureIdx = source.IndexOf("var services = _services;", StringComparison.Ordinal);
        var disposeIdx = source.IndexOf("await services.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(captureIdx >= 0 && disposeIdx >= 0, "Expected shutdown disposal pattern not found.");

        // The relevant null-assignment is the one after the capture (an earlier
        // `_services = null;` also exists in the init failure path).
        var nullIdx = source.IndexOf("_services = null;", captureIdx, StringComparison.Ordinal);
        Assert.True(nullIdx >= 0, "Shutdown must null _services after capturing it.");
        Assert.True(nullIdx < disposeIdx,
            "App must set _services = null after capturing it and BEFORE awaiting DisposeAsync().");
    }

    [Fact]
    public void HubWindow_ContainsActivationHook_InTryCatch()
    {
        var source = ReadHubWindowSource();

        // The activation hook must be wrapped so a future view model's activation cannot
        // escape the frame-navigated XAML handler.
        var hookIdx = source.IndexOf("PageActivator?.OnNavigatedTo", StringComparison.Ordinal);
        Assert.True(hookIdx >= 0, "Activation hook call not found in HubWindow.");

        // Look at the surrounding block for a try/catch guarding the call.
        var windowStart = Math.Max(0, hookIdx - 400);
        var around = source.Substring(windowStart, Math.Min(source.Length - windowStart, 800));
        Assert.Contains("try", around);
        Assert.Contains("catch", around);
        Assert.Contains("Page activation failed", source);
    }

    [Fact]
    public void App_ResetsNavigationScope_OnHubClose()
    {
        var source = ReadAppSources();

        // Closing the hub must reset the navigation scope so page view models do not
        // outlive their window.
        Assert.Contains("PageActivator?.Reset()", source);
    }

    [Fact]
    public void HubWindow_InvokesPageActivator_OnNavigation()
    {
        var source = ReadHubWindowSource();

        Assert.Contains("PageActivator", source);
        Assert.Contains("OnNavigatedTo", source);
    }

    [Fact]
    public void PresentationCore_IsWinUiFree()
    {
        // Scan the whole core recursively but exclude the intentionally WinUI-bound
        // Adapters/ folder, so any future core code in a new subfolder is still covered.
        var adaptersDir = Path.Combine(PresentationDir, "Adapters") + Path.DirectorySeparatorChar;
        var files = Directory
            .EnumerateFiles(PresentationDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(adaptersDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var offending = FindWinUiToken(File.ReadAllText(file));
            Assert.True(offending is null,
                $"{Path.GetFileName(file)} contains banned WinUI token '{offending}'; the Presentation core must stay " +
                "WinUI-free (put WinUI-bound code in Presentation/Adapters/).");
        }
    }

    [Fact]
    public void WinUiFreeGuard_FlagsBannedTokensButIgnoresComments()
    {
        // The guard must have teeth: it flags WinUI usage in code (not just using
        // directives) while ignoring line and block comments and lowercase prose.
        Assert.Equal("Microsoft.UI", FindWinUiToken("using Microsoft.UI.Xaml;\nclass X {}"));
        Assert.Equal("Application.Current", FindWinUiToken("var x = Application.Current;"));
        Assert.Equal("Frame", FindWinUiToken("void M(Frame f) {}"));
        Assert.Equal("Window", FindWinUiToken("Window w;"));

        Assert.Null(FindWinUiToken("/// mentions Microsoft.UI.Dispatching.DispatcherQueue in a doc"));
        Assert.Null(FindWinUiToken("// forwards to the existing frame navigation path"));
        Assert.Null(FindWinUiToken("int frame = 0; // lowercase identifier is fine"));
        Assert.Null(FindWinUiToken("/* Window Frame Brush Color Microsoft.UI in a block comment */"));
        Assert.Null(FindWinUiToken("var s = new HubWindow();".Replace("HubWindow", "HubWidget")));
    }

    [Fact]
    public void PresentationAdapters_StayWinUiBound_AndAreNotLinkedIntoPureTests()
    {
        var adaptersDir = Path.Combine(PresentationDir, "Adapters");
        Assert.True(Directory.Exists(adaptersDir), "Expected Presentation/Adapters to hold the WinUI-bound implementations.");
        Assert.NotEmpty(Directory.EnumerateFiles(adaptersDir, "*.cs", SearchOption.TopDirectoryOnly));

        var csproj = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "tests", "OpenClaw.Tray.Tests", "OpenClaw.Tray.Tests.csproj"));

        Assert.DoesNotContain("Presentation\\Adapters", csproj);
        Assert.DoesNotContain("WinUIDispatcher", csproj);
        Assert.DoesNotContain("FramePageActivator", csproj);
    }

    private static string PresentationDir =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Presentation");

    private static readonly string[] BannedSubstrings =
    {
        "Microsoft.UI", "Windows.UI", "Microsoft.Extensions.Hosting", "Application.Current",
    };

    private static readonly Regex BannedTypeTokens =
        new(@"\b(Window|Frame|Brush|Color)\b", RegexOptions.Compiled);

    /// <summary>
    /// Returns the first banned WinUI token in <paramref name="source"/> (ignoring
    /// comments), or null when the source is WinUI-free.
    /// </summary>
    private static string? FindWinUiToken(string source)
    {
        var code = StripComments(source);

        foreach (var banned in BannedSubstrings)
        {
            if (code.Contains(banned, StringComparison.Ordinal))
            {
                return banned;
            }
        }

        var match = BannedTypeTokens.Match(code);
        return match.Success ? match.Value : null;
    }

    private static string StripComments(string source)
    {
        // Remove /* ... */ block comments first (may span lines), then // line comments.
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        var builder = new System.Text.StringBuilder(withoutBlocks.Length);
        foreach (var line in withoutBlocks.Split('\n'))
        {
            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            builder.Append(commentIndex >= 0 ? line[..commentIndex] : line).Append('\n');
        }

        return builder.ToString();
    }

    private static string ReadAppSources()
    {
        var appDir = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        return string.Join(
            "\n",
            Directory
                .EnumerateFiles(appDir, "App*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string ReadHubWindowSource()
    {
        return File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));
    }
}
