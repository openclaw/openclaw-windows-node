using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared.Commands;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ExecReusableCommandBinderTests
{
    // Before pinning, the carrier ran verbatim and cmd.exe re-resolved the payload
    // executable at launch, searching the current directory before PATH. A check at
    // approval time could not close that: whoever can write to the working directory
    // after the check simply wins. The payload executable is now pinned to its resolved
    // absolute path, so cmd has nothing left to search and a shadow dropped afterwards
    // cannot win.
    [Fact]
    public void CarrierPayloadShadowedByCurrentDirectory_PinsResolvedPath()
    {
        var cwd = Directory.CreateTempSubdirectory("openclaw-cwd-shadow").FullName;
        try
        {
            var shadow = Path.Combine(cwd, "hostname.exe");
            File.WriteAllBytes(shadow, [0x4D, 0x5A]);

            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                cwd,
                env: ExecTestPath.SystemOnly,
                out var failure);

            Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
            Assert.NotNull(bound);
            Assert.True(bound!.IsCarrierTransport);
            Assert.False(string.Equals(
                shadow,
                bound.Resolution.ResolvedPath,
                StringComparison.OrdinalIgnoreCase));
            Assert.Equal(bound.Resolution.ResolvedPath, bound.ExecutionArgv[4], ignoreCase: true);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // The end-to-end version of the same claim, run through real cmd.exe: bind while the
    // working directory is clean, then insert the shadow, then execute exactly the argv
    // the binder authorized. The pinned path must still be what runs.
    [Fact]
    public void PinnedCarrier_IgnoresShadowInsertedAfterApproval()
    {
        var cwd = Directory.CreateTempSubdirectory("openclaw-post-approval-shadow").FullName;
        try
        {
            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                cwd,
                env: ExecTestPath.SystemOnly,
                out var failure);
            Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
            Assert.NotNull(bound);

            // whoami prints DOMAIN\user, hostname prints the machine name, so which one
            // ran is unambiguous from the output alone.
            File.Copy(
                Path.Combine(Environment.SystemDirectory, "whoami.exe"),
                Path.Combine(cwd, "hostname.exe"));

            var stdout = RunCapturingStdout(bound!.ExecutionArgv, cwd);

            Assert.Equal(Environment.MachineName, stdout.Trim(), ignoreCase: true);
            Assert.DoesNotContain(@"\", stdout.Trim(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // Pinning must never change an argument. The executed payload has to be the
    // approved one with only its executable token replaced.
    [Fact]
    public void PinnedCarrier_PreservesPayloadArgumentsExactly()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "hostname.exe\t--first   --second"],
            cwd: null,
            env: ExecTestPath.SystemOnly,
            out var failure);

        Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
        Assert.NotNull(bound);
        Assert.Equal(
            bound!.Resolution.ResolvedPath + "\t--first   --second",
            bound.ExecutionArgv[4],
            ignoreCase: true);
        Assert.Equal(new[] { "--first", "--second" }, bound.Argv.Skip(1).ToArray());
        Assert.True(
            ExecApprovalsCoordinator.CarrierTransportMatchesRequest(
                bound.ExecutionArgv,
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe\t--first   --second"]));
    }

    // A tokenized tail keeps its arity, so no new process-creation quoting is
    // introduced by pinning.
    [Fact]
    public void PinnedCarrier_PreservesTokenizedTailArity()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "hostname.exe", "--version"],
            cwd: null,
            env: ExecTestPath.SystemOnly,
            out var failure);

        Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
        Assert.NotNull(bound);
        Assert.Equal(6, bound!.ExecutionArgv.Count);
        Assert.Equal(bound.Resolution.ResolvedPath, bound.ExecutionArgv[4], ignoreCase: true);
        Assert.Equal("--version", bound.ExecutionArgv[5]);
    }

    // A resolved path containing a space cannot be pinned. Quoting it does not help:
    // under /s cmd strips the first and last quote of the payload and uses the rest
    // verbatim, which leaves the path ambiguous again. Fail closed to prompt-only
    // rather than pin something cmd will re-split.
    [Fact]
    public void CarrierPayloadResolvingToPathWithSpace_DoesNotBind()
    {
        var root = Directory.CreateTempSubdirectory("openclaw-spaced").FullName;
        var spaced = Path.Combine(root, "program files");
        Directory.CreateDirectory(spaced);
        try
        {
            File.Copy(
                Path.Combine(Environment.SystemDirectory, "hostname.exe"),
                Path.Combine(spaced, "spacedtool.exe"));

            ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "spacedtool.exe"],
                cwd: null,
                env: new Dictionary<string, string>
                {
                    ["PATH"] = spaced,
                    ["PATHEXT"] = ".EXE",
                },
                out var failure);

            Assert.Equal(
                ExecReusableCommandBinder.BindFailure.CarrierPayloadNotPinnable,
                failure);
            Assert.Equal(
                "carrier-payload-not-pinnable",
                ExecReusableCommandBinder.DescribeFailure(failure));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\tools\a b\tool.exe")]
    [InlineData(@"C:\tools\a%b\tool.exe")]
    [InlineData(@"C:\tools\a!b\tool.exe")]
    [InlineData(@"C:\tools\a^b\tool.exe")]
    [InlineData("C:\\tools\\a\"b\\tool.exe")]
    [InlineData(@"C:\tools\a&b\tool.exe")]
    [InlineData(@"C:\tools\a(b)\tool.exe")]
    [InlineData("C:\\tools\\a\tb\\tool.exe")]
    [InlineData(@"C:\tools\a,b\tool.exe")]
    [InlineData(@"C:\tools\a;b\tool.exe")]
    [InlineData(@"C:\tools\a=b\tool.exe")]
    public void UnsafelyRepresentedPinnedPath_RefusesReconstruction(string pinned)
    {
        Assert.False(
            CanonicalCmdCarrier.TryBuildPinnedCarrier(
                ["cmd.exe", "/d", "/s", "/c", "tool.exe --flag"],
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                pinned,
                out _));
    }

    // Rewriting must work from the token span, never from a search and replace: an
    // argument that repeats the executable's text has to survive untouched.
    [Fact]
    public void PinnedCarrier_DoesNotRewriteArgumentsThatRepeatTheExecutableText()
    {
        Assert.True(
            CanonicalCmdCarrier.TryBuildPinnedCarrier(
                ["cmd.exe", "/d", "/s", "/c", "tool.exe --compare=tool.exe"],
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                @"C:\tools\tool.exe",
                out var pinnedArgv));

        Assert.Equal(@"C:\tools\tool.exe --compare=tool.exe", pinnedArgv[4]);
    }

    // Adopted from the contributor's resolved-carrier-identity fix (b9be5f40), which
    // detected a bare "cmd.exe" resolving outside System32 and refused to bind. Pinning
    // supersedes that: argv[0] is set to the system image, so a rogue cmd.exe first on
    // PATH is not merely detected, it is never the thing that runs. The invariant the
    // original test protected (an attacker-supplied cmd image is never looked through)
    // is preserved and strengthened.
    [Fact]
    public void BareCmdResolvingOutsideSystemDirectory_ExecutesTheSystemImage()
    {
        var directory = Directory.CreateTempSubdirectory("openclaw-untrusted-bare-cmd");
        try
        {
            var rogueCmd = Path.Combine(directory.FullName, "cmd.exe");
            File.Copy(FindTestHostExecutable(), rogueCmd);
            // The rogue directory is first so a bare "cmd.exe" would resolve to it, but
            // the system directory still has to be reachable or the payload cannot
            // resolve and the test would fail before reaching its actual claim.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = directory.FullName + Path.PathSeparator + ExecTestPath.SystemDirectory,
                ["PATHEXT"] = ".EXE",
            };

            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                cwd: null,
                env);

            Assert.NotNull(bound);
            Assert.NotEqual(rogueCmd, bound!.ExecutionArgv[0], StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(
                Environment.SystemDirectory,
                bound.ExecutionArgv[0],
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // A relocated image merely *named* cmd.exe is still never looked through.
    [Fact]
    public void RelocatedCmdImage_IsNotATrustedCarrier()
    {
        var directory = Directory.CreateTempSubdirectory("openclaw-relocated-cmd");
        try
        {
            var rogueCmd = Path.Combine(directory.FullName, "cmd.exe");
            File.Copy(FindTestHostExecutable(), rogueCmd);

            Assert.Null(ExecReusableCommandBinder.TryBind(
                [rogueCmd, "/d", "/s", "/c", "hostname.exe"],
                cwd: null,
                env: null));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // b9be5f40 proposed making .com durably bindable alongside .exe. That widening is
    // deliberately NOT taken here: adding an executable format to the durable
    // authorization allowlist is its own decision, and this change is scoped to
    // carrier binding. A real Windows .com image therefore stays prompt-only.
    [Fact]
    public void NativeComExtensionTarget_DoesNotBind()
    {
        var target = Path.Combine(Environment.SystemDirectory, "chcp.com");
        Assert.True(File.Exists(target), $"Native Windows .com target was not found: {target}");

        Assert.Null(ExecReusableCommandBinder.TryBind([target], cwd: null, env: null));
    }

    // The legacy quarantine reproduces the catalog as it actually was (e4ff61e7),
    // where NormalizedBasename stripped .exe only. A .com spelling was therefore
    // never classified as a command host and never refused, so it must not be
    // quarantined now. Inventing a denial that never happened would be the same
    // class of error as dropping one that did.
    [Theory]
    [InlineData("powershell.com")]
    [InlineData(@"C:\tools\python.com")]
    [InlineData("PYTHON.COM")]
    public void ComExtension_IsNotLegacyQuarantined(string token)
        => Assert.False(ExecCommandToken.IsLegacyQuarantinedHost(token));

    private static string RunCapturingStdout(IReadOnlyList<string> argv, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        return stdout;
    }
    [Fact]
    public void CarrierPayloadWithoutCurrentDirectoryShadow_StillBinds()
    {
        var cwd = Directory.CreateTempSubdirectory("openclaw-cwd-clean").FullName;
        try
        {
            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                cwd,
                env: ExecTestPath.SystemOnly,
                out var failure);

            Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
            Assert.NotNull(bound);
            Assert.EndsWith(
                @"\hostname.exe",
                bound!.Resolution.ResolvedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // A relative payload already resolves against the current directory in both the
    // binder and cmd.exe, so it is not ambiguous and must keep binding.
    [Fact]
    public void CarrierPayloadWithExplicitRelativePath_IsNotTreatedAsAmbiguous()
    {
        var cwd = Directory.CreateTempSubdirectory("openclaw-cwd-relative").FullName;
        try
        {
            var tool = Path.Combine(cwd, "tool.exe");
            File.WriteAllBytes(tool, [0x4D, 0x5A]);

            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", @".\tool.exe"],
                cwd,
                env: null,
                out var failure);

            Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
            Assert.Equal(tool, bound!.Resolution.ResolvedPath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // NUL is the argument separator inside a persisted argPattern, so an argument that
    // contains one renders identically to two separate arguments. Persisting it would let
    // a stored rule authorize a differently segmented argv. Reject before anything is
    // bound or written.
    [Fact]
    public void ArgumentContainingNul_DoesNotBind()
    {
        ExecReusableCommandBinder.TryBind(
            [@"C:\Windows\System32\hostname.exe", "a\0b"],
            cwd: null,
            env: null,
            out var failure);

        Assert.Equal(ExecReusableCommandBinder.BindFailure.ArgumentContainsNul, failure);
        Assert.Equal("argument-contains-nul", ExecReusableCommandBinder.DescribeFailure(failure));
    }

    // The NUL-in-argument rejection has to apply to the carrier payload too, not only to
    // a direct invocation. The carrier tokenizes a payload string into a fresh argv, so a
    // NUL carried inside that string would otherwise reach the persisted argument
    // pattern and collide with a differently segmented payload.
    [Fact]
    public void CarrierPayloadContainingNul_DoesNotBind()
    {
        ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "hostname.exe a\0b"],
            cwd: null,
            env: null,
            out var failure);

        Assert.Equal(ExecReusableCommandBinder.BindFailure.ArgumentContainsNul, failure);
    }

    // An omitted cwd is not the same as "no current directory": the child inherits this
    // process's, and cmd.exe searches it before PATH just the same. Pinning covers that
    // case too, because the payload no longer contains anything for cmd to search for.
    [Fact]
    public void CarrierPayloadShadowedByInheritedDirectory_PinsResolvedPath()
    {
        var cwd = Directory.CreateTempSubdirectory("openclaw-inherited-shadow").FullName;
        var original = Environment.CurrentDirectory;
        try
        {
            var shadow = Path.Combine(cwd, "hostname.exe");
            File.WriteAllBytes(shadow, [0x4D, 0x5A]);
            Environment.CurrentDirectory = cwd;

            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                cwd: null,
                env: ExecTestPath.SystemOnly,
                out var failure);

            Assert.Equal(ExecReusableCommandBinder.BindFailure.None, failure);
            Assert.NotNull(bound);
            Assert.NotEqual(shadow, bound!.ExecutionArgv[4], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(bound.Resolution.ResolvedPath, bound.ExecutionArgv[4], ignoreCase: true);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(cwd, recursive: true);
        }
    }


    [Fact]
    public void NulBearingArgument_CannotShareABindingWithSplitArguments()
    {
        var split = ExecReusableCommandBinder.TryBind(
            [@"C:\Windows\System32\hostname.exe", "a", "b"],
            cwd: null,
            env: null,
            out _);
        Assert.NotNull(split);

        var joined = ExecReusableCommandBinder.TryBind(
            [@"C:\Windows\System32\hostname.exe", "a\0b"],
            cwd: null,
            env: null,
            out _);
        Assert.Null(joined);
    }

    // D3: a durable rule names a path, not the bytes at that path. A path on a remote
    // share is controlled by whoever controls the share, so an approval granted once
    // could silently authorize replaced content later. Refuse those outright.
    [Theory]
    [InlineData(@"\\fileserver\tools\hostname.exe")]
    [InlineData(@"\\127.0.0.1\c$\Windows\System32\hostname.exe")]
    public void NetworkExecutable_DoesNotBind(string path)
    {
        ExecReusableCommandBinder.TryBind([path, "--version"], cwd: null, env: null, out var failure);
        Assert.Equal(ExecReusableCommandBinder.BindFailure.ExecutableOnNetworkPath, failure);
        Assert.Equal("executable-on-network-path", ExecReusableCommandBinder.DescribeFailure(failure));
    }

    // D4: a carrier we do not recognize character-for-character is not silently
    // unbindable. It reports why, so the operator sees a reason rather than an
    // allowlist that appears to be ignored.
    [Theory]
    [InlineData(new[] { "cmd.exe", "/c", "hostname.exe" }, "non-canonical-cmd-carrier")]
    [InlineData(new[] { "cmd.exe", "/d", "/s", "/c", "hostname.exe | findstr.exe h" }, "carrier-payload-not-static")]
    [InlineData(new[] { "cmd.exe", "/d", "/s", "/c", "echo hi" }, "carrier-payload-is-shell-builtin")]
    public void UnbindableCarrier_ReportsWhy(string[] command, string expected)
    {
        Assert.Null(ExecReusableCommandBinder.TryBind(command, cwd: null, env: null, out var failure));
        Assert.Equal(expected, ExecReusableCommandBinder.DescribeFailure(failure));
    }

    // A carrier that is merely named cmd.exe must never be looked through: the
    // authorization would describe the trusted inner program while the untrusted
    // outer image is what actually runs.
    [Fact]
    public void UntrustedCarrierImage_DoesNotBind()
    {
        var fake = Path.Combine(Path.GetTempPath(), "openclaw-fake-carrier", "cmd.exe");
        Assert.Null(ExecReusableCommandBinder.TryBind(
            [fake, "/d", "/s", "/c", "hostname.exe"],
            cwd: null,
            env: null,
            out var failure));
        Assert.Equal("untrusted-cmd-carrier-image", ExecReusableCommandBinder.DescribeFailure(failure));
    }

    // D5: the carrier is the transport, the inner executable is the identity. The two
    // must stay separable, and the transport must remain exactly what was received.
    [Fact]
    public void TrustedCarrier_KeepsTransportSeparateFromIdentity()
    {
        string[] command = ["cmd.exe", "/d", "/s", "/c", "hostname.exe"];
        var bound = ExecReusableCommandBinder.TryBind(command, cwd: null, env: ExecTestPath.SystemOnly);

        Assert.NotNull(bound);
        Assert.True(bound!.IsCarrierTransport);
        Assert.EndsWith("hostname.exe", bound.Argv[0], StringComparison.OrdinalIgnoreCase);

        // Both resolutions the carrier would otherwise perform at launch are pinned:
        // argv[0] to the resolved system cmd.exe, and the payload executable to its
        // resolved absolute path. The switches in between are verbatim.
        Assert.True(Path.IsPathFullyQualified(bound.ExecutionArgv[0]));
        Assert.EndsWith("cmd.exe", bound.ExecutionArgv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "/d", "/s", "/c" }, bound.ExecutionArgv.Skip(1).Take(3).ToArray());
        Assert.Equal(bound.Argv[0], bound.ExecutionArgv[4], ignoreCase: true);
        Assert.Equal(5, bound.ExecutionArgv.Count);
    }

    [Fact]
    public void DirectInvocation_ExecutesItselfWithNoTransportIndirection()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["hostname.exe"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.False(bound!.IsCarrierTransport);
        Assert.Equal(bound.Argv, bound.ExecutionArgv);
    }

    [Fact]
    public void CanonicalCmdHostname_BindsResolvedExecutable()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
            cwd: null,
            env: ExecTestPath.SystemOnly);

        Assert.NotNull(bound);
        Assert.True(Path.IsPathFullyQualified(bound!.Argv[0]));
        Assert.EndsWith("hostname.exe", bound.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bound.Resolution.ResolvedPath, bound.Pattern);
    }

    [Fact]
    public void CanonicalCmdQuotedLiteralArgument_DoesNotBind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe \"hello world\""],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("hostname.exe | findstr.exe host")]
    [InlineData("hostname.exe && whoami.exe")]
    [InlineData("hostname.exe > output.txt")]
    [InlineData("hostname%COMSPEC%.exe")]
    [InlineData("hostname.exe ^& whoami.exe")]
    [InlineData("(hostname.exe)")]
    [InlineData("hostname.exe \"unterminated")]
    public void DynamicOrAmbiguousCmdPayload_DoesNotBind(string payload)
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("echo hello")]
    public void CmdBuiltin_DoesNotBind(string payload)
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("cmd.exe", "/c", "hostname.exe")]
    [InlineData("cmd.exe", "/d", "/c", "hostname.exe")]
    public void NoncanonicalCmdCarrier_DoesNotBind(params string[] command)
        => Assert.Null(ExecReusableCommandBinder.TryBind(command, cwd: null, env: null));

    [Theory]
    [InlineData(" /d ", "/s", "/c")]
    [InlineData("/d", " /s ", "/c")]
    [InlineData("/d", "/s", " /c ")]
    public void PaddedCmdSwitch_DoesNotBind(string d, string s, string c)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", d, s, c, "hostname.exe"],
            cwd: null,
            env: null));

    [Fact]
    public void DirectInterpreter_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/c", "hostname.exe"],
            cwd: null,
            env: null));

    [Fact]
    public void NonexistentRelativeExecutable_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            [@".\future-tool-that-does-not-exist.exe"],
            cwd: Path.GetTempPath(),
            env: null));

    [Fact]
    public void TransparentEnvPayload_DoesNotBind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "env hostname.exe"],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("env FOO=bar hostname.exe")]
    [InlineData("env -i hostname.exe")]
    [InlineData("env --unknown hostname.exe")]
    public void ModifiedOrAmbiguousEnvPayload_DoesNotBind(string payload)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null));

    [Fact]
    public async Task AcceptedSpaceGrammar_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        var payload = $"{host} --echo-args alpha beta value=three";
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync(
            "cmd.exe",
            ["/d", "/s", "/c", payload]);
        var direct = await RunAndReadArgsAsync(
            bound!.Argv[0],
            bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta", "value=three"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    [Fact]
    public async Task AcceptedTabGrammar_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        var payload = $"{host}\t--echo-args\talpha\tbeta";
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync(
            "cmd.exe",
            ["/d", "/s", "/c", payload]);
        var direct = await RunAndReadArgsAsync(
            bound!.Argv[0],
            bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    [Fact]
    public void QuotedExecutableToken_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"\"{host}\" --echo-args alpha"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void TrailingBackslash_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"{host} --echo-args tail\\"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void NonCmdWhitespace_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"{host}\u00A0--echo-args"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void CmdEchoSuppressionPrefix_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "@hostname.exe"],
            cwd: null,
            env: null));

    // Executables that pick their payload out of their arguments used to be refused
    // durable approval by name. A name list is not a boundary: renaming the image
    // defeats it, and the list can never be complete. The boundary is now the
    // argument pattern, so approving one payload authorizes only that payload.
    [Theory]
    [InlineData("mshta.exe https://example.invalid/payload.hta", "mshta.exe")]
    [InlineData("regsvr32.exe /s payload.dll", "regsvr32.exe")]
    [InlineData("rundll32.exe payload.dll,EntryPoint", "rundll32.exe")]
    public void WindowsCodeHost_BindsOnlyToTheApprovedPayload(string payload, string image)
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.EndsWith(image, bound!.Pattern, StringComparison.OrdinalIgnoreCase);

        // The approved invocation matches its own pattern.
        Assert.True(ExecArgPattern.Matches(bound.ArgPattern, bound.Argv));

        // A different payload through the same host does not.
        Assert.False(ExecArgPattern.Matches(
            bound.ArgPattern,
            [bound.Argv[0], "https://example.invalid/attacker.hta"]));
    }

    [Fact]
    public void TabDelimitedLiteralArguments_Bind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe\thello"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.Equal("hello", bound!.Argv[1]);
    }

    // ── Multi-element carrier tails ───────────────────────────────────────────
    // Low-level callers and upstream approval fixtures may send command text
    // already tokenized across several argv elements, for example
    // ["cmd.exe","/d","/s","/c","echo","SAFE&&whoami"]. Supporting reconstructible
    // tails preserves compatibility for those callers without changing the live
    // gateway's single-element command shape.

    [Fact]
    public void MultiElementCarrierTail_Binds()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.EndsWith("where.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello", bound.Argv[1]);
    }

    [Fact]
    public void MultiElementCarrierTail_WithShellOperator_DoesNotBind()
    {
        // The upstream fixture shape. It must stay unbindable: the payload is a
        // compound command, and `echo` is a cmd builtin.
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("hello\tworld")]
    [InlineData("\"hello\"")]
    public void MultiElementCarrierTail_NonReconstructibleElement_DoesNotBind(string trailing)
    {
        // A space join cannot recover the original process-creation quoting, so the
        // binder must refuse rather than authorize a different command than the one
        // cmd.exe would run.
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", trailing],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Fact]
    public void AbsolutePathCmdCarrier_Binds()
    {
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        Assert.True(File.Exists(cmdPath));

        var bound = ExecReusableCommandBinder.TryBind(
            [cmdPath, "/d", "/s", "/c", "hostname.exe"],
            cwd: null,
            env: ExecTestPath.SystemOnly);

        Assert.NotNull(bound);
        Assert.EndsWith("hostname.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntrustedCmdCopyCarrier_DoesNotBindInnerExecutable()
    {
        // A cmd.exe copy in a writable directory can ignore its arguments and run
        // anything, so it must never be looked through: binding against the inner
        // executable would show the operator a trusted path while the untrusted
        // outer image is what an allow-once actually launches.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "openclaw-untrusted-cmd-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var rogueCmd = Path.Combine(dir, "cmd.exe");
            File.Copy(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                rogueCmd);

            var bound = ExecReusableCommandBinder.TryBind(
                [rogueCmd, "/d", "/s", "/c", "hostname.exe"],
                cwd: null,
                env: null);

            Assert.Null(bound);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UntrustedCmdCopy_IsNotTrustedCarrier_ButStillSerializesAsCmd()
    {
        var rogueCmd = Path.Combine(Path.GetTempPath(), "writable", "cmd.exe");

        // Trust side refuses to look through it.
        Assert.False(CanonicalCmdCarrier.IsTrustedCarrierExecutable(rogueCmd));
        Assert.False(CanonicalCmdCarrier.TryGetTrustedCanonicalPayload(
            [rogueCmd, "/d", "/s", "/c", "hostname.exe"], out _));

        // Serialization side still recognizes it, so the cmd-aware quoting is used.
        Assert.True(CanonicalCmdCarrier.IsCmdExecutable(rogueCmd));
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            [rogueCmd, "/d", "/s", "/c", "hostname.exe"], out var payload));
        Assert.Equal("hostname.exe", payload);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    public void BareCmdName_IsTrustedCarrier(string executable)
    {
        Assert.True(CanonicalCmdCarrier.IsTrustedCarrierExecutable(executable));
    }

    [Fact]
    public void SystemDirectoryCmd_IsTrustedCarrier()
    {
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        Assert.True(CanonicalCmdCarrier.IsTrustedCarrierExecutable(cmdPath));
        // The contributor's resolved-carrier fix (b9be5f40) asserted this through a
        // separate IsTrustedSystemCmdPath predicate. ResolveTrustedCarrierPath is the
        // surviving owner of that question: it answers it and yields the absolute image
        // that will be pinned into argv[0], so the check and the execution agree.
        Assert.Equal(cmdPath, CanonicalCmdCarrier.ResolveTrustedCarrierPath(cmdPath), ignoreCase: true);
    }

    [Fact]
    public async Task AcceptedMultiElementTail_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        string[] carrier = ["cmd.exe", "/d", "/s", "/c", host, "--echo-args", "alpha", "beta"];
        var bound = ExecReusableCommandBinder.TryBind(carrier, cwd: null, env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync("cmd.exe", carrier.Skip(1).ToArray());
        var direct = await RunAndReadArgsAsync(bound!.Argv[0], bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    // ── cmd delimiters the tokenizer does not model ───────────────────────────
    // cmd also delimits the command-name token on ',', ';' and '='. The binder
    // splits only on space and tab, so these must fail closed (bind nothing) rather
    // than bind a token that differs from what cmd would execute.

    [Theory]
    [InlineData("where.exe,hello")]
    [InlineData("where.exe;hello")]
    [InlineData("where.exe=hello")]
    public void UnmodeledCmdDelimiter_FailsClosed(string payload)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null));

    // ── PATHEXT targets that are not directly-executable images ───────────────

    [Theory]
    [InlineData(".js")]
    [InlineData(".vbs")]
    [InlineData(".wsf")]
    [InlineData(".msc")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".com")]
    public void NonExecutableExtensionTarget_DoesNotBind(string extension)
    {
        var directory = Directory.CreateTempSubdirectory("openclaw-binder-ext");
        try
        {
            var target = Path.Combine(directory.FullName, "probe" + extension);
            File.WriteAllText(target, "rem placeholder");

            Assert.Null(ExecReusableCommandBinder.TryBind([target], cwd: null, env: null));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExecutableExtensionTarget_Binds()
    {
        // Control for NonExecutableExtensionTarget_DoesNotBind: the same shape with
        // a .exe target must still bind, so the extension gate is what rejects.
        var directory = Directory.CreateTempSubdirectory("openclaw-binder-ext");
        try
        {
            var target = Path.Combine(directory.FullName, "probe.exe");
            File.Copy(FindTestHostExecutable(), target);

            var bound = ExecReusableCommandBinder.TryBind([target], cwd: null, env: null);

            Assert.NotNull(bound);
            Assert.EndsWith("probe.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(bound.Argv[0]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── Durable argument binding ──────────────────────────────────────────────
    //
    // These replace an earlier catalog of interpreter and script-host basenames.
    // The catalog tried to name every executable that selects its behavior from its
    // arguments and refuse durable approval for each. The binding below makes that
    // list unnecessary: every generated rule pins the arguments, so a rule for an
    // interpreter authorizes one script rather than the interpreter itself, and an
    // executable nobody thought to list is bound just as tightly as one that was.

    [Fact]
    public void BoundCommand_PinsItsArguments_SoADifferentInvocationIsNotAuthorized()
    {
        var directory = Directory.CreateTempSubdirectory("exec-argbind");
        try
        {
            var target = Path.Combine(directory.FullName, "interpreter.exe");
            File.Copy(FindTestHostExecutable(), target);

            var approved = ExecReusableCommandBinder.TryBind(
                [target, "trusted-script.py"], cwd: null, env: null);
            Assert.NotNull(approved);

            var entry = new ExecAllowlistEntry
            {
                Pattern = approved!.Pattern,
                ArgPattern = approved.ArgPattern,
                Source = "allow-always",
            };

            var sameCommand = ExecReusableCommandBinder.TryBind(
                [target, "trusted-script.py"], cwd: null, env: null);
            Assert.NotNull(sameCommand);
            Assert.NotNull(ExecAllowlistMatcher.Match(
                [entry], sameCommand!.Resolution, sameCommand.Argv));

            var differentScript = ExecReusableCommandBinder.TryBind(
                [target, "attacker-script.py"], cwd: null, env: null);
            Assert.NotNull(differentScript);
            Assert.Null(ExecAllowlistMatcher.Match(
                [entry], differentScript!.Resolution, differentScript.Argv));

            // An extra argument is also a different operation, even though the approved
            // arguments are still a prefix of it.
            var extraArgument = ExecReusableCommandBinder.TryBind(
                [target, "trusted-script.py", "--danger"], cwd: null, env: null);
            Assert.NotNull(extraArgument);
            Assert.Null(ExecAllowlistMatcher.Match(
                [entry], extraArgument!.Resolution, extraArgument.Argv));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RenamedCodeHost_IsStillBoundToItsArguments()
    {
        // A basename catalog was defeated by copying an interpreter to a new name.
        // Argument binding does not depend on the name, so the renamed copy gets the
        // same narrow rule the original would have.
        var directory = Directory.CreateTempSubdirectory("exec-renamed-host");
        try
        {
            var target = Path.Combine(directory.FullName, "totally-ordinary-tool.exe");
            File.Copy(FindTestHostExecutable(), target);

            var bound = ExecReusableCommandBinder.TryBind(
                [target, "payload.js"], cwd: null, env: null);

            Assert.NotNull(bound);
            Assert.True(ExecArgPattern.Matches(bound!.ArgPattern, bound.Argv));
            Assert.False(ExecArgPattern.Matches(bound.ArgPattern, [target, "other.js"]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string FindTestHostExecutable()    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !Directory.Exists(Path.Combine(current.FullName, "tests")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var path = Path.Combine(
            current!.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration!,
            "net10.0",
            "OpenClaw.Shared.TestHost.exe");
        Assert.True(File.Exists(path), $"Argument test host was not built: {path}");
        return path;
    }

    private static async Task<string[]> RunAndReadArgsAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = await process!.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Process failed with exit {process.ExitCode}: {stderr}");
        return JsonSerializer.Deserialize<string[]>(stdout.Trim())
            ?? throw new InvalidOperationException("Argument test host returned invalid JSON.");
    }
}
