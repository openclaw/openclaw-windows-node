using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Validates that all localization resource files are consistent with en-us.
/// Catches missing/extra keys and broken format placeholders early — before translation PRs land.
/// </summary>
public class LocalizationValidationTests
{
    private static readonly HashSet<string> InvariantOrDeferredResourceKeys = new(StringComparer.Ordinal)
    {
        "AboutPage_TextBlock_19.Text",
        "CanvasWindow_TextBlock_31.Text",
        "CanvasWindow_winexWindowEx_2.Title",
        "ChatWindow_winexWindowEx_2.Title",
        "HubWindow_winexWindowEx_2.Title",
        "TitleText.Text",
        "TokenPromptBox.Header",
        "TokenTextBox.Header",
        "TrayMenuWindow_winexWindowEx_2.Title",
        "Onboarding_Connection_QrButton",
        "Onboarding_Connection_Token",
        "WindowTitle_TrayMenu",
        "WindowTitle_Update",
    };

    private static readonly string[] RequiredRuntimeOnboardingKeys =
    [
        "Onboarding_Ready_Node_ScreenCapture",
        "Onboarding_Ready_Node_ScreenCapture_Sub",
        "Onboarding_Ready_Node_Camera",
        "Onboarding_Ready_Node_Camera_Sub",
        "Onboarding_Ready_Node_SystemCmd",
        "Onboarding_Ready_Node_SystemCmd_Sub",
        "Onboarding_Ready_Node_Canvas",
        "Onboarding_Ready_Node_Canvas_Sub",
        "Onboarding_Ready_Node_Notify",
        "Onboarding_Ready_Node_Notify_Sub",
    ];

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string GetStringsDirectory() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");

    private static Dictionary<string, string> LoadResw(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Descendants("data")
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Element("value")?.Value ?? string.Empty);
    }

    private static List<string> GetNonEnglishLocaleDirectories(string stringsDir) =>
        Directory.GetDirectories(stringsDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "en-us", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    private static bool IsInvariantValue(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Regex.IsMatch(value, @"^\d+(\.\d+)?[Kk]?$", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(value, @"^(v\d|[A-Z0-9._%+-]+://|~?/|\.NET|WinUI|WinAppSDK|OpenClaw$|GitHub|MCP|JSON|API|HTTP|HTTPS|SSH|TLS|WebView2|OAuth|QR$|Cron$|main$|user$|machine-name$)", RegexOptions.CultureInvariant) ||
        value.Contains("openclaw://", StringComparison.Ordinal) ||
        value.Contains("github.com", StringComparison.Ordinal) ||
        value.Contains("openclaw.ai", StringComparison.Ordinal) ||
        value.Contains("localhost", StringComparison.Ordinal) ||
        value.Contains("ws://", StringComparison.Ordinal) ||
        value.Contains("wss://", StringComparison.Ordinal) ||
        value.Contains("http://", StringComparison.Ordinal) ||
        value.Contains("https://", StringComparison.Ordinal) ||
        value.Contains("~/", StringComparison.Ordinal);

    private static bool IsInvariantOrDeferred(string key, string value) =>
        InvariantOrDeferredResourceKeys.Contains(key) || IsInvariantValue(value);

    [Fact]
    public void AllLocales_HaveExactlySameKeysAsEnUs()
    {
        var stringsDir = GetStringsDirectory();
        var referencePath = Path.Combine(stringsDir, "en-us", "Resources.resw");
        Assert.True(File.Exists(referencePath), $"Reference file not found: {referencePath}");

        var referenceKeys = LoadResw(referencePath).Keys.ToHashSet(StringComparer.Ordinal);

        var localeDirs = GetNonEnglishLocaleDirectories(stringsDir);

        Assert.NotEmpty(localeDirs);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            Assert.True(File.Exists(reswPath), $"Expected Resources.resw for locale '{locale}'.");

            var localeKeys = LoadResw(reswPath).Keys.ToHashSet(StringComparer.Ordinal);

            var missing = referenceKeys.Except(localeKeys).OrderBy(k => k).ToList();
            var extra = localeKeys.Except(referenceKeys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0,
                $"Locale '{locale}' is missing {missing.Count} key(s): {string.Join(", ", missing.Take(10))}");
            Assert.True(extra.Count == 0,
                $"Locale '{locale}' has {extra.Count} unexpected key(s): {string.Join(", ", extra.Take(10))}");
        }
    }

    [Fact]
    public void AllLocales_PreserveFormatPlaceholders()
    {
        var stringsDir = GetStringsDirectory();
        var referenceResw = LoadResw(Path.Combine(stringsDir, "en-us", "Resources.resw"));

        var keysWithPlaceholders = referenceResw
            .Where(kv => Regex.IsMatch(kv.Value, @"\{\d+\}"))
            .ToList();

        if (keysWithPlaceholders.Count == 0)
            return;

        var localeDirs = GetNonEnglishLocaleDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var localeResw = LoadResw(reswPath);

            foreach (var (key, enValue) in keysWithPlaceholders)
            {
                if (!localeResw.TryGetValue(key, out var localeValue))
                    continue;

                var enPlaceholders = Regex.Matches(enValue, @"\{\d+\}")
                    .Select(m => m.Value).OrderBy(p => p).ToList();
                var localePlaceholders = Regex.Matches(localeValue, @"\{\d+\}")
                    .Select(m => m.Value).OrderBy(p => p).ToList();

                Assert.True(enPlaceholders.SequenceEqual(localePlaceholders),
                    $"Locale '{locale}', key '{key}': expected placeholders " +
                    $"[{string.Join(", ", enPlaceholders)}] but found " +
                    $"[{string.Join(", ", localePlaceholders)}]");
            }
        }
    }

    [Fact]
    public void AllFiveLocaleDirectories_Exist()
    {
        var stringsDir = GetStringsDirectory();
        string[] expected = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw"];

        foreach (var locale in expected)
        {
            var dir = Path.Combine(stringsDir, locale);
            Assert.True(Directory.Exists(dir), $"Locale directory missing: {locale}");
            Assert.True(File.Exists(Path.Combine(dir, "Resources.resw")),
                $"Resources.resw missing for locale: {locale}");
        }
    }

    [Fact]
    public void AllLocales_ContainOnboardingKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var keys = LoadResw(reswPath).Keys;
            var onboardingKeys = keys.Where(k => k.StartsWith("Onboarding_")).ToList();

            Assert.True(onboardingKeys.Count > 0,
                $"Locale '{locale}' has no Onboarding_* keys");
        }
    }

    [Fact]
    public void AllLocales_ContainRuntimeOnboardingKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var keys = LoadResw(reswPath).Keys.ToHashSet(StringComparer.Ordinal);
            var missing = RequiredRuntimeOnboardingKeys
                .Where(key => !keys.Contains(key))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Locale '{locale}' is missing runtime onboarding key(s): {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void NoLocale_HasDuplicateKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var doc = System.Xml.Linq.XDocument.Load(reswPath);
            var names = doc.Descendants("data")
                .Select(e => e.Attribute("name")!.Value)
                .ToList();

            var duplicates = names.GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"Locale '{locale}' has duplicate keys: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void AllLocales_HaveSameKeyCount()
    {
        var stringsDir = GetStringsDirectory();
        var referencePath = Path.Combine(stringsDir, "en-us", "Resources.resw");
        var referenceCount = LoadResw(referencePath).Count;

        var localeDirs = Directory.GetDirectories(stringsDir);
        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var count = LoadResw(reswPath).Count;
            Assert.Equal(referenceCount, count);
        }
    }

    [Fact]
    public void Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales()
    {
        var stringsDir = GetStringsDirectory();
        var referenceResw = LoadResw(Path.Combine(stringsDir, "en-us", "Resources.resw"));
        var localeResw = GetNonEnglishLocaleDirectories(stringsDir)
            .Select(d => (Locale: Path.GetFileName(d), Resources: LoadResw(Path.Combine(d, "Resources.resw"))))
            .ToList();

        Assert.NotEmpty(localeResw);

        var partial = new List<string>();
        var identicalWithoutRationale = new List<string>();

        foreach (var (key, enValue) in referenceResw.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var identicalLocales = localeResw
                .Where(l => l.Resources.TryGetValue(key, out var value) && value == enValue)
                .Select(l => l.Locale)
                .ToList();

            if (identicalLocales.Count == 0)
                continue;

            if (identicalLocales.Count != localeResw.Count)
            {
                partial.Add($"{key} ({enValue}) identical in [{string.Join(", ", identicalLocales)}]");
                continue;
            }

            if (!IsInvariantOrDeferred(key, enValue))
                identicalWithoutRationale.Add($"{key} ({enValue})");
        }

        Assert.True(partial.Count == 0,
            "Resources must be translated in all non-English locales or invariant in all. Partial entries: " +
            string.Join("; ", partial.Take(20)));
        Assert.True(identicalWithoutRationale.Count == 0,
            "Resources identical to en-us in every non-English locale need an invariant/deferred rationale. Entries: " +
            string.Join("; ", identicalWithoutRationale.Take(20)));
    }
}
