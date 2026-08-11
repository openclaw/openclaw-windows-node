using System;
using System.Collections.Generic;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// The durable argument binding is exchanged with the gateway and shared with the
// macOS node, so its written form is a wire contract rather than an internal detail.
// These tests pin the exact shapes both sides agree on.
public class ExecArgPatternTests
{
    [Fact]
    public void NoArguments_WritesTheEmptyArgumentForm()
    {
        // A command with no arguments is distinguishable from one with a single empty
        // argument, which is why the empty form is a pair of separators rather than
        // an empty string.
        Assert.Equal("^\0\0$", ExecArgPattern.BuildArgPattern([@"C:\Windows\System32\hostname.exe"]));
    }

    [Fact]
    public void Arguments_AreEscapedAndSeparatedByNul()
    {
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\python.exe", "script.py"]);
        Assert.Equal("^script\\.py\0$", pattern);
    }

    [Fact]
    public void RegexMetacharactersInAnArgument_AreMatchedLiterally()
    {
        // Without escaping, an approved argument containing regex syntax would widen
        // the rule to whatever that syntax happens to match.
        var argv = new[] { @"C:\tool.exe", "a.*b" };
        var pattern = ExecArgPattern.BuildArgPattern(argv);

        Assert.True(ExecArgPattern.Matches(pattern, argv));
        Assert.False(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "aXXXb"]));
    }

    [Fact]
    public void AnArgumentContainingASeparator_CannotImpersonateTwoArguments()
    {
        // A space-joined subject would render these two commands identically. The NUL
        // separator is what keeps one argument from spanning an argument boundary.
        var single = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "one two"]);

        Assert.False(ExecArgPattern.Matches(single, [@"C:\tool.exe", "one", "two"]));
        Assert.True(ExecArgPattern.Matches(single, [@"C:\tool.exe", "one two"]));
    }

    [Fact]
    public void ApprovedArguments_DoNotAuthorizeALongerCommandThatStartsWithThem()
    {
        // The matcher on the other side of the wire tests without anchoring, so the
        // written pattern has to carry its own anchors.
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "status"]);

        Assert.False(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "status", "--force"]));
    }

    [Fact]
    public void EitherSpellingOfAPathArgument_MatchesTheSameRule()
    {
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "dir/script.py"]);

        Assert.True(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", @"dir\script.py"]));
        Assert.True(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "dir/script.py"]));
    }

    [Fact]
    public void MalformedStoredPattern_FailsClosed()
    {
        // A stored pattern is remote-influenced input. An unparsable one must not
        // throw into the approval path, and must not authorize anything either.
        Assert.False(ExecArgPattern.Matches("^(unclosed", [@"C:\tool.exe", "x"]));
    }

    [Fact]
    public void HashedPatternWrittenByMacOs_IsMatchedByExactEquality()
    {
        var argv = new[] { "/usr/bin/tool", "--flag", "value" };
        var hashed = ExecArgPattern.BuildHashedArgPattern(argv);

        Assert.StartsWith("sha256:argv:", hashed, StringComparison.Ordinal);
        Assert.True(ExecArgPattern.Matches(hashed, argv));
        Assert.False(ExecArgPattern.Matches(hashed, ["/usr/bin/tool", "--flag", "other"]));
    }

    [Fact]
    public void HashedPattern_DistinguishesArgumentBoundaries()
    {
        // The digest covers a length-prefixed rendering, so no rearrangement of the
        // same characters across arguments produces the same pattern.
        var a = ExecArgPattern.BuildHashedArgPattern(["/bin/t", "ab", "c"]);
        var b = ExecArgPattern.BuildHashedArgPattern(["/bin/t", "a", "bc"]);

        Assert.NotEqual(a, b);
    }
}

// The rule that decides whether a stored entry authorizes a command. It is shared
// with the gateway and the macOS node, so the same allowlist file has to mean the
// same thing in all three places.
public class ExecAllowlistArgBindingTests
{
    private static ExecCommandResolution Resolution(string path)
        => ExecCommandResolver.Resolve([path], cwd: null, env: null)
            ?? throw new InvalidOperationException("resolution failed");

    [Fact]
    public void GeneratedEntryWithNoArgumentBinding_IsNotHonored()
    {
        // Generated entries have pinned their arguments since argument binding was
        // introduced. One that lacks a binding is an older record whose arguments were
        // never captured, so honoring it would let a rule approved for one command
        // authorize every later command that reuses the executable.
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            Source = "allow-always",
        };

        Assert.Null(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    [Fact]
    public void HandWrittenEntryWithNoArgumentBinding_IsHonored()
    {
        // A rule with no source was written by a human who chose to authorize the
        // executable itself. That is a deliberate decision, not a stale record.
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
        };

        Assert.NotNull(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    // Provenance is what separates a deliberate path-only rule from a generated one
    // that lost its binding, so the check must not depend on the exact spelling this
    // node happens to write. A marker that is cased differently, padded, or written by
    // some other producer still means a generator made the entry.
    [Theory]
    [InlineData("ALLOW-ALWAYS")]
    [InlineData("Allow-Always")]
    [InlineData("allow-always ")]
    [InlineData(" allow-always")]
    [InlineData("generated")]
    [InlineData("some-future-source")]
    public void GeneratedEntryWithNoArgumentBinding_IsNotHonoredForAnySourceSpelling(
        string source)
    {
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            Source = source,
        };

        Assert.Null(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    // Whitespace is not provenance. An entry whose source is blank was never stamped by
    // a generator, so it keeps hand-written path-only semantics.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EntryWithBlankSourceAndNoArgumentBinding_IsStillHandWritten(string source)
    {
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            Source = source,
        };

        Assert.NotNull(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    // A generated entry that lost its binding must not gain path-only reach just
    // because its target is an ordinary program the legacy catalog never quarantined.
    [Fact]
    public void GeneratedEntryWithNoArgumentBinding_IsNotHonoredEvenForAnUnquarantinedHost()
    {
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\where.exe",
            Source = "UNKNOWN-PRODUCER",
        };

        Assert.Null(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\where.exe"),
            [@"C:\Windows\System32\where.exe", "hostname.exe"]));
    }

    [Fact]
    public void BoundEntry_AuthorizesOnlyTheApprovedArguments()
    {
        var argv = new[] { @"C:\Windows\System32\hostname.exe", "--fqdn" };
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            ArgPattern = ExecArgPattern.BuildArgPattern(argv),
            Source = "allow-always",
        };
        var resolution = Resolution(@"C:\Windows\System32\hostname.exe");

        Assert.NotNull(ExecAllowlistMatcher.Match([entry], resolution, argv));
        Assert.Null(ExecAllowlistMatcher.Match(
            [entry], resolution, [@"C:\Windows\System32\hostname.exe", "--other"]));
    }

    [Fact]
    public void ABoundEntryIsPreferredOverAHandWrittenPathOnlyEntry()
    {
        // Order in the file must not decide which rule applies, or an audit of the
        // file could not tell what authorized a command.
        var argv = new[] { @"C:\Windows\System32\hostname.exe", "--fqdn" };
        var pathOnly = new ExecAllowlistEntry { Pattern = @"**/hostname.exe" };
        var bound = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            ArgPattern = ExecArgPattern.BuildArgPattern(argv),
            Source = "allow-always",
        };

        var match = ExecAllowlistMatcher.Match(
            [pathOnly, bound], Resolution(@"C:\Windows\System32\hostname.exe"), argv);

        Assert.Same(bound, match);
    }

    // Upgrade behavior. A provenance-less entry written under the previous model, when
    // the target was an interpreter or shell that model refused outright, must not
    // become an unconditional allow just because the model changed. It goes inert and
    // the command prompts.
    [Theory]
    [InlineData(@"C:\Python312\python.exe")]
    [InlineData(@"C:\Python312\python3.12.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe")]
    [InlineData(@"C:\Program Files\MSBuild\msbuild.exe")]
    [InlineData(@"C:\Windows\System32\certutil.exe")]
    [InlineData(@"C:\Windows\System32\wbem\wmic.exe")]
    public void LegacyPathOnlyEntryForACommandHost_IsInert(string target)
    {
        var entry = new ExecAllowlistEntry { Pattern = target };

        Assert.Null(ExecAllowlistMatcher.Match(
            [entry], Resolution(target), [target, "-c", "print(1)"]));
    }

    // Regression guard for the catalog itself, not for any one name.
    //
    // D6 asks a purely historical question: would this exact provenance-less entry
    // have been refused durable approval when it was written? So the quarantine set
    // must reproduce the catalog exactly as it stood immediately before argument
    // binding replaced it (e4ff61e7). An earlier revision of this branch restored only
    // the interpreter half and silently dropped the code-host half, which would have
    // converted previously denied entries into unconditional allows. Enumerating the
    // catalog here makes that class of truncation fail loudly.
    [Fact]
    public void LegacyQuarantineCoversTheFullPreBindingCatalog()
    {
        string[] catalog =
        [
            "sh", "bash", "zsh", "dash", "ash", "ksh", "fish",
            "cmd", "powershell", "pwsh",
            "wsl", "cscript", "wscript",
            "py", "pyw", "python", "pythonw", "pypy",
            "node", "nodejs", "deno", "bun", "qjs",
            "ruby", "jruby", "perl", "php", "lua", "luajit",
            "java", "javaw", "jshell", "dotnet", "csi", "fsi", "fsharpi",
            "r", "rscript", "tclsh", "wish", "groovy",
            "mshta", "regsvr32", "rundll32",
            "msbuild", "csc", "vbc", "dnx", "rcsi",
            "installutil", "regasm", "regsvcs", "mavinject",
            "msiexec", "certutil", "bitsadmin", "wmic",
            "forfiles", "scriptrunner", "pcalua", "cmstp", "odbcconf",
            "msdt", "ieexec", "presentationhost", "winrs", "hh", "msxsl", "xwizard",
        ];

        var missing = new List<string>();
        foreach (var name in catalog)
        {
            if (!ExecCommandToken.IsLegacyQuarantinedHost(@"C:\somewhere\" + name + ".exe"))
                missing.Add(name);
        }

        Assert.True(
            missing.Count == 0,
            "Names dropped from the pre-binding catalog: " + string.Join(", ", missing));
    }

    // Versioned interpreters were covered by the old catalog, so they stay covered.
    [Theory]
    [InlineData("python3")]
    [InlineData("python3.12")]
    [InlineData("pythonw3.11")]
    [InlineData("pypy3.10")]
    public void LegacyQuarantineCoversVersionedInterpreters(string name)
        => Assert.True(ExecCommandToken.IsLegacyQuarantinedHost(@"C:\tools\" + name + ".exe"));

    // The suffix must actually be a version. A different program that merely starts
    // with an interpreter name is an ordinary executable and keeps working.
    [Theory]
    [InlineData("pythonish")]
    [InlineData("python-wrapper")]
    [InlineData("pypycache")]
    public void LegacyQuarantineDoesNotCoverLookalikeNames(string name)
        => Assert.False(ExecCommandToken.IsLegacyQuarantinedHost(@"C:\tools\" + name + ".exe"));

    // The quarantine is narrow on purpose: an ordinary program keeps working from a
    // provenance-less rule, because a human who wrote one meant it.
    [Theory]
    [InlineData(@"C:\Windows\System32\hostname.exe")]
    [InlineData(@"C:\Program Files\Git\cmd\git.exe")]
    [InlineData(@"C:\tools\ripgrep\rg.exe")]
    public void LegacyPathOnlyEntryForAnOrdinaryExecutable_StillMatches(string target)
    {
        var entry = new ExecAllowlistEntry { Pattern = target };

        Assert.NotNull(ExecAllowlistMatcher.Match(
            [entry], Resolution(target), [target, "--anything"]));
    }

    // The quarantined entry is left alone, not rewritten. The only way a command host
    // becomes reusable is an explicit Allow always, which writes an argument-bound
    // sibling. That sibling matches its own command and nothing else.
    [Fact]
    public void ExplicitlyApprovedSiblingRestoresAMatchForAQuarantinedHost()
    {
        const string target = @"C:\Python312\python.exe";
        var argv = new[] { target, "build.py" };
        var legacy = new ExecAllowlistEntry { Pattern = target };
        var sibling = new ExecAllowlistEntry
        {
            Pattern = target,
            ArgPattern = ExecArgPattern.BuildArgPattern(argv),
            Source = "allow-always",
        };

        Assert.Same(
            sibling,
            ExecAllowlistMatcher.Match([legacy, sibling], Resolution(target), argv));
        Assert.Null(ExecAllowlistMatcher.Match(
            [legacy, sibling], Resolution(target), [target, "other.py"]));
    }

    // The quarantine reproduces the catalog exactly as it was (e4ff61e7), where
    // NormalizedBasename stripped .exe only. `python.com` therefore never normalized
    // to `python`, was never in the catalog, and was never refused, so a legacy entry
    // naming it must keep working. An earlier revision of this branch quarantined it
    // by teaching NormalizedBasename to strip .com as well, which invented a denial
    // that never happened and changed IsEnv, the shell-wrapper normalizer, and the
    // PowerShell and builtin classifiers as a side effect. Bindability is unaffected
    // either way: .com is not durably bindable (see
    // ExecReusableCommandBinderTests.NativeComExtensionTarget_DoesNotBind).
    [Fact]
    public void LegacyPathOnlyEntryForACommandHostWithComExtension_StillMatches()
    {
        const string target = @"C:\tools\python.com";
        var entry = new ExecAllowlistEntry { Pattern = target };

        Assert.NotNull(ExecAllowlistMatcher.Match(
            [entry], Resolution(target), [target, "-c", "1"]));
    }
}

// rawCommand is the text an operator is shown. If it could disagree with the argv
// that runs, a request could describe one command and execute another.
public class ExecRawCommandConsistencyTests
{
    [Fact]
    public void AbsentRawCommand_ImposesNoConstraint()
        => Assert.True(ExecRawCommandConsistency.IsConsistent(null, ["hostname.exe"]));

    [Fact]
    public void GatewayFormattedArgv_IsAccepted()
    {
        // The gateway quotes only on whitespace, a double quote, or an empty string.
        // Anything it produced has to be accepted here or valid traffic breaks.
        Assert.True(ExecRawCommandConsistency.IsConsistent(
            "cmd.exe /d /s /c echo SAFE&&whoami",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));
    }

    [Fact]
    public void InlineShellPayloadOfACarrier_IsAccepted()
    {
        // The gateway also accepts the text after /c for a wrapper invocation, so a
        // real carrier request legitimately carries only its payload as rawCommand.
        Assert.True(ExecRawCommandConsistency.IsConsistent(
            "echo SAFE&&whoami",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));
    }

    [Fact]
    public void TextThatDescribesADifferentCommand_IsRejected()
    {
        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "echo",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));

        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "hostname.exe",
            ["whoami.exe"]));
    }

    [Fact]
    public void PayloadFormIsNotAcceptedForANonCarrier()
    {
        // Only a cmd invocation carries an inline payload. Accepting the tail of an
        // ordinary command would let rawCommand omit the executable being run.
        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "SAFE&&whoami",
            ["hostname.exe", "SAFE&&whoami"]));
    }
}
