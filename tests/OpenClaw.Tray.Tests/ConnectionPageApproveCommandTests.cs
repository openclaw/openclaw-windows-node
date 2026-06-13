using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the CLI approve commands emitted by <c>ConnectionPagePlan</c>. The
/// OpenClaw CLI registers approve as a noun-first subcommand
/// (<c>openclaw nodes approve &lt;requestId&gt;</c> and
/// <c>openclaw devices approve &lt;requestId&gt;</c>). The previous verb-first
/// strings (<c>openclaw approve node …</c>) silently failed when users
/// pasted them on the gateway host, so this test guards against regressing
/// back to that broken form.
///
/// Source-parsed for the same reason as <c>FluentIconCatalogTests</c>:
/// <c>OpenClaw.Tray.Tests</c> is a pure net10.0 project that does not
/// reference the WinUI tray assembly.
/// </summary>
public sealed class ConnectionPageApproveCommandTests
{
    private static string ReadPlanSource()
    {
        var path = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");
        return File.ReadAllText(path);
    }

    [Fact]
    public void NodeApproveCommand_UsesNounFirstSubcommand()
    {
        var src = ReadPlanSource();
        // Pin the noun-first command prefix rather than the full
        // interpolated literal — this stays correct if someone renames
        // the local variable from `reqId` to `requestId`, switches to
        // `string.Format`, or extracts the literal to a constant.
        AssertContainsCli(
            src,
            "openclaw nodes approve ",
            "BuildNodeApproveCommand must emit noun-first `openclaw nodes approve <id>`; the verb-first form (`openclaw approve node`) silently failed when pasted.");

        // The legacy broken form must not return as an *emitted* string —
        // check the interpolated/quoted variants, not the bare phrase,
        // because the explanatory comment also names the legacy command.
        Assert.DoesNotContain("$\"openclaw approve node {reqId}\"", src);
        Assert.DoesNotContain("\"openclaw approve node\"", src);
    }

    [Fact]
    public void NodeRoleUpgradeDevicePairing_UsesDevicesApproveCommand()
    {
        var src = ReadPlanSource();
        var nodeBody = ExtractMethodBody(src, "BuildNodeApproveCommand");

        Assert.Contains("PairingApprovalKind.DevicePair", nodeBody);
        AssertContainsCli(
            nodeBody,
            "openclaw devices approve ",
            "Node role-upgrade requests are WebSocket device-pair approvals, not gateway node-pair command-trust approvals.");
        Assert.Contains("snap.NodeDeviceId", nodeBody);
    }

    [Fact]
    public void DevicesApproveCommand_UsesNounFirstSubcommand()
    {
        var src = ReadPlanSource();
        AssertContainsCli(
            src,
            "openclaw devices approve ",
            "BuildDevicePairingApproveCommand must emit noun-first `openclaw devices approve <id>`; the verb-first form (`openclaw approve device`) silently failed when pasted.");

        Assert.DoesNotContain("$\"openclaw approve device {reqId}\"", src);
        Assert.DoesNotContain("\"openclaw approve device\"", src);
    }

    [Fact]
    public void MissingRequestId_EmitsShellSafeDiscoveryCommand_NotBareApprove()
    {
        // CLI's approve subcommands require a <requestId> positional. A bare
        // `openclaw nodes approve` / `openclaw devices approve` exits
        // non-zero with "missing required argument", so the user-facing
        // fallback must lead with a discovery command instead.
        var src = ReadPlanSource();
        AssertContainsCli(src, "openclaw nodes pending",
            "Missing-requestId fallback for node pairing must emit `openclaw nodes pending` (a shell-safe discovery command).");
        AssertContainsCli(src, "openclaw devices list",
            "Missing-requestId fallback for device pairing must emit `openclaw devices list` (a shell-safe discovery command).");

        // Defense in depth: assert there's no bare end-of-string approve
        // literal (which would be the broken legacy fallback).
        Assert.DoesNotContain("\"openclaw nodes approve\"", src);
        Assert.DoesNotContain("\"openclaw devices approve\"", src);

        // And the clipboard text must not contain shell-hostile syntax
        // for the fallback path. `#` is parsed as a literal arg by
        // cmd.exe; `<requestId>` is parsed as input redirection. Either
        // would break the paste-and-run flow for Windows-cmd users.
        // We only check inside the two emission helpers, not the whole
        // file, because comments explaining the rationale legitimately
        // mention these characters.
        var nodeBody = ExtractMethodBody(src, "BuildNodeApproveCommand");
        var devBody = ExtractMethodBody(src, "BuildDevicePairingApproveCommand");
        AssertNoShellHostileChars(nodeBody, nameof(nodeBody));
        AssertNoShellHostileChars(devBody, nameof(devBody));
    }

    private static void AssertContainsCli(string source, string expected, string message)
    {
        if (!source.Contains(expected, System.StringComparison.Ordinal))
            Assert.Fail($"Expected source to contain `{expected}`. {message}");
    }

    private static void AssertNoShellHostileChars(string methodBody, string methodLabel)
    {
        // Scan only string literals inside the method body — comments
        // legitimately reference `#` and `<` while explaining the rationale.
        methodBody = System.Text.RegularExpressions.Regex.Replace(
            methodBody,
            @"//.*$",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            methodBody, "\\$?\"[^\"\\n]*\""))
        {
            var literal = m.Value;
            // Allow `<` and `#` only when not embedded inside an interpolation
            // hole (which we strip first so `{reqId}` etc. don't trigger).
            var stripped = System.Text.RegularExpressions.Regex.Replace(literal, "\\{[^}]*\\}", "");
            if (stripped.Contains('#') || stripped.Contains('<') || stripped.Contains('>'))
                Assert.Fail(
                    $"String literal in {methodLabel} contains shell-hostile char(s): `{literal}`. " +
                    "cmd.exe parses `#` as a literal arg and `<`/`>` as I/O redirection — pasting the " +
                    "copied text into cmd will fail. Use a single command without comment/redirect chars.");
        }
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        // Coarse extractor: from the method signature to the matching closing
        // brace at the same indentation. Sufficient for short pure helpers
        // like BuildNodeApproveCommand / BuildDevicePairingApproveCommand.
        var match = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"\b(?:private|internal|public)\s+static\s+[^\r\n]+?\b{System.Text.RegularExpressions.Regex.Escape(methodName)}\s*\(");
        if (!match.Success) return string.Empty;
        var bodyStart = source.IndexOf('{', match.Index);
        if (bodyStart < 0) return string.Empty;
        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(bodyStart, i - bodyStart + 1);
            }
        }
        return source.Substring(bodyStart);
    }
}
