using System.Text.RegularExpressions;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// F-11: skill.md duplicates the tray-side capability surface for agent
/// readability. This test compares the set of <c>### &lt;command&gt;</c>
/// headings in skill.md against <see cref="McpToolBridge.KnownCommands"/>
/// (the canonical list of documented capability commands) so additions
/// or renames in the tray fail loudly here instead of silently shipping
/// drifted documentation.
///
/// The test compares command identifiers only — descriptions, examples,
/// and prose can be tweaked freely without breaking the test.
/// </summary>
public class SkillMdDriftTests
{
    [Fact]
    public void SkillMd_command_set_matches_capability_registry()
    {
        var skillMdPath = LocateSkillMd();
        var content = File.ReadAllText(skillMdPath);

        var documented = ParseCommandHeadings(content);
        var canonical = new HashSet<string>(McpToolBridge.KnownCommands, StringComparer.Ordinal);

        var missingFromDoc = canonical.Except(documented).OrderBy(s => s).ToList();
        var extrasInDoc = documented.Except(canonical).OrderBy(s => s).ToList();

        if (missingFromDoc.Count > 0 || extrasInDoc.Count > 0)
        {
            var msg = "skill.md drifted from the capability registry " +
                      "(McpToolBridge.CommandDescriptions). Update " +
                      $"src/OpenClaw.WinNode.Cli/skill.md.\n  Missing from doc: " +
                      $"[{string.Join(", ", missingFromDoc)}]\n  Extras in doc: " +
                      $"[{string.Join(", ", extrasInDoc)}]";
            Assert.Fail(msg);
        }
    }

    /// <summary>
    /// skill.md lists each command under its own H3 heading like
    /// <c>### system.notify</c>. Anything matching <c>### &lt;dotted.name&gt;</c>
    /// counts as a documented command. We deliberately ignore other H3s
    /// (e.g. "### Message kinds", "### ComponentDef") which don't have a
    /// dotted-name shape.
    /// </summary>
    private static HashSet<string> ParseCommandHeadings(string md)
    {
        // Match ### followed by a single dotted token (lowercase, dots, dots+lowercase
        // segments only) to the end of the line. canvas.a2ui.pushJSONL has a
        // mixed-case suffix, so allow camelCase tail segments too.
        var rx = new Regex(@"^###\s+([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)\s*$",
                           RegexOptions.Multiline);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(md))
        {
            set.Add(m.Groups[1].Value);
        }
        return set;
    }

    /// <summary>
    /// skill.md ships next to winnode.exe. From the test's working directory
    /// (the test bin folder), walk up to the repo root and resolve the source
    /// copy — that's the canonical input the build copies to output. Falls
    /// back to the test bin's own copy if the source can't be located.
    /// </summary>
    private static string LocateSkillMd()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "OpenClaw.WinNode.Cli", "skill.md");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        var nextTo = Path.Combine(AppContext.BaseDirectory, "skill.md");
        if (File.Exists(nextTo)) return nextTo;
        throw new FileNotFoundException("Could not locate src/OpenClaw.WinNode.Cli/skill.md from the test working directory.");
    }
}
