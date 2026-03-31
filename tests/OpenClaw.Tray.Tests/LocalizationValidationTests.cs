using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Validates that all localization resource files are consistent with en-us.
/// Catches missing/extra keys and broken format placeholders early — before translation PRs land.
/// </summary>
public class LocalizationValidationTests
{
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

    [Fact]
    public void AllLocales_HaveExactlySameKeysAsEnUs()
    {
        var stringsDir = GetStringsDirectory();
        var referencePath = Path.Combine(stringsDir, "en-us", "Resources.resw");
        Assert.True(File.Exists(referencePath), $"Reference file not found: {referencePath}");

        var referenceKeys = LoadResw(referencePath).Keys.ToHashSet(StringComparer.Ordinal);

        var localeDirs = Directory.GetDirectories(stringsDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "en-us", StringComparison.OrdinalIgnoreCase))
            .ToList();

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

        var localeDirs = Directory.GetDirectories(stringsDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "en-us", StringComparison.OrdinalIgnoreCase));

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
}
