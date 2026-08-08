using System;
using System.IO;
using System.Linq;
using OpenClaw.Shared.Commands;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Ledger guard for `canonical-cmd-carrier`.
///
/// The Windows node originates `cmd.exe /d /s /c &lt;command&gt;` when it forwards a
/// shell command. Two layers act on that shape: the exec approvals binder decides
/// whether the inner command may be durably authorized, and the MXC command-line
/// builder decides how to render it for execution. When those two disagree about
/// which argv shapes are the carrier, one layer can authorize a shape the other
/// refuses to run. Both must therefore route through CanonicalCmdCarrier.
/// </summary>
public class CanonicalCmdCarrierTests
{
    public static TheoryData<string[], bool> CarrierShapes() => new()
    {
        { ["cmd.exe", "/d", "/s", "/c", "hostname.exe"], true },
        { ["cmd", "/d", "/s", "/c", "hostname.exe"], true },
        { ["CMD.EXE", "/D", "/S", "/C", "hostname.exe"], true },
        { [@"C:\Windows\System32\cmd.exe", "/d", "/s", "/c", "hostname.exe"], true },
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello"], true },
        // Not the canonical carrier.
        { ["cmd.exe", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/s", "/k", "hostname.exe"], false },
        { ["cmd.exe", " /d ", "/s", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/s", "/c"], false },
        { ["hostname.exe"], false },
        // Recognized prefix but a tail a space join cannot reconstruct.
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello world"], false },
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "\"quoted\""], false },
    };

    [Theory]
    [MemberData(nameof(CarrierShapes))]
    public void BinderAndMxcBuilder_AgreeOnCarrierRecognition(string[] argv, bool isCarrier)
    {
        Assert.Equal(isCarrier, CanonicalCmdCarrier.TryGetCanonicalPayload(argv, out _));

        // The MXC builder must not carry its own competing definition. It detects
        // cmd command mode and then defers to the shared helper for recognition and
        // payload extraction, so any argv the helper rejects is rejected there too.
        var mxcSource = ReadMxcConfigBuilderSource();
        Assert.Contains("CanonicalCmdCarrier.TryGetCanonicalPayload", mxcSource);
        Assert.Contains("CanonicalCmdCarrier.IsCmdExecutable", mxcSource);
        Assert.DoesNotContain("private static bool IsCmdExecutable", mxcSource);
    }

    [Theory]
    [MemberData(nameof(CarrierShapes))]
    public void BinderNeverBindsCarrierItself(string[] argv, bool isCarrier)
    {
        _ = isCarrier;
        var bound = ExecReusableCommandBinder.TryBind(argv, cwd: null, env: null);

        // Whatever the binder decides, it must never return cmd.exe as the durably
        // authorized executable: cmd selects the code it runs from its arguments.
        if (bound is not null)
        {
            Assert.False(
                CanonicalCmdCarrier.IsCmdExecutable(bound.Argv[0]),
                $"Binder durably authorized the cmd carrier itself: {bound.Argv[0]}");
        }
    }

    [Fact]
    public void MultiElementTail_ReconstructsSpaceJoinedPayload()
    {
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello", "there"],
            out var payload));
        Assert.Equal("where.exe hello there", payload);
    }

    [Fact]
    public void SingleElementTail_IsUsedVerbatim()
    {
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            ["cmd.exe", "/d", "/s", "/c", "where.exe   hello"],
            out var payload));
        Assert.Equal("where.exe   hello", payload);
    }

    // ── Pinned carrier reconstruction (D7) ────────────────────────────────────

    // The switches are copied from the request, not re-emitted from a template, so a
    // request that used uppercase keeps uppercase and the executed command line is
    // byte-identical to the approved one apart from the two pinned tokens.
    [Fact]
    public void PinnedCarrier_PreservesTheRequestsOwnSwitchSpelling()
    {
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            ["CMD.EXE", "/D", "/S", "/C", "tool.exe --flag"],
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\tools\tool.exe",
            out var pinned));

        Assert.Equal("/D", pinned[1]);
        Assert.Equal("/S", pinned[2]);
        Assert.Equal("/C", pinned[3]);
        Assert.Equal(@"C:\tools\tool.exe --flag", pinned[4]);
    }

    // A relative carrier path is never accepted as the thing to execute: Windows would
    // re-resolve it at launch, which is the resolution pinning exists to remove.
    [Fact]
    public void PinnedCarrier_RequiresAFullyQualifiedCarrierAndPayload()
    {
        Assert.False(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            ["cmd.exe", "/d", "/s", "/c", "tool.exe"],
            "cmd.exe",
            @"C:\tools\tool.exe",
            out _));

        Assert.False(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            ["cmd.exe", "/d", "/s", "/c", "tool.exe"],
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "tool.exe",
            out _));
    }

    [Fact]
    public void PinnedCarrier_MatchesTheRequestItWasBuiltFrom()
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", "tool.exe --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\tools\tool.exe",
            out var pinned));

        Assert.True(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(pinned, request));
    }

    // Anything beyond the two permitted differences is drift and must be refused at
    // execution time even if it was somehow produced at bind time.
    [Theory]
    [InlineData(4, @"C:\tools\other.exe --flag")]
    [InlineData(4, @"C:\tools\tool.exe --other")]
    [InlineData(4, @"C:\tools\tool.exe --flag && whoami.exe")]
    [InlineData(4, @"tool.exe --flag")]
    [InlineData(3, "/k")]
    public void PinnedCarrier_RejectsAnyOtherDifferenceFromTheRequest(int index, string replacement)
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", "tool.exe --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\tools\tool.exe",
            out var pinned));

        var tampered = pinned.ToArray();
        tampered[index] = replacement;

        Assert.False(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(tampered, request));
    }

    // Interior spacing is part of the approved command line, not incidental
    // formatting. Every tamper below leaves the token values identical, so a check
    // that compared only tokens would accept them. The payload is verified by
    // reconstructing it from the request instead, so byte-level respacing is refused.
    [Theory]
    [InlineData(@"C:\tools\tool.exe  --flag")]
    [InlineData("C:\\tools\\tool.exe\t--flag")]
    [InlineData(@" C:\tools\tool.exe --flag")]
    [InlineData(@"C:\tools\tool.exe --flag ")]
    public void PinnedCarrier_RejectsInteriorSpacingChangesThatPreserveTokens(string respaced)
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", "tool.exe --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\tools\tool.exe",
            out var pinned));

        var tampered = pinned.ToArray();
        tampered[4] = respaced;

        // Guard the premise: the tamper really does preserve the token values, so the
        // rejection below is attributable to spacing and nothing else.
        Assert.True(CmdPayloadTokenizer.TryTokenize(pinned[4], out var originalTokens, out _));
        Assert.True(CmdPayloadTokenizer.TryTokenize(respaced, out var respacedTokens, out _));
        Assert.Equal(originalTokens.ToArray(), respacedTokens.ToArray());
        Assert.False(string.Equals(pinned[4], respaced, StringComparison.Ordinal));

        Assert.False(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(tampered, request));
    }

    // A bare payload name legitimately gains its PATHEXT extension when resolved, so
    // "tool" pinned to "...\tool.exe" is the same identity spelled completely.
    [Fact]
    public void PinnedCarrier_AcceptsTheExtensionABareNameGainsWhenResolved()
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", "tool --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            @"C:\tools\tool.exe",
            out var pinned));

        Assert.True(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(pinned, request));
    }

    // Regression: Path.HasExtension is true for any dotted name, so a versioned tool
    // whose whole name is the PATHEXT stem ("python3.11" -> "python3.11.exe") used to
    // bind successfully and then be rejected here, failing the run with an internal
    // error instead of running the command the operator approved.
    [Theory]
    [InlineData("python3.11", @"C:\tools\python3.11.exe")]
    [InlineData("clang-15.0", @"C:\tools\clang-15.0.exe")]
    [InlineData("tool.v2", @"C:\tools\tool.v2.exe")]
    public void PinnedCarrier_AcceptsAVersionedNameThatGainedItsResolvedExtension(
        string requestName,
        string resolvedPath)
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", $"{requestName} --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            resolvedPath,
            out var pinned));

        Assert.True(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(pinned, request));
    }

    // Relaxing the extension rule must not let pinning swap one program for another:
    // only an appended extension is equivalent, never a different stem.
    [Theory]
    [InlineData("python3.11", @"C:\tools\python3.12.exe")]
    [InlineData("tool", @"C:\tools\other.exe")]
    [InlineData("tool.v2", @"C:\tools\tool.exe")]
    public void PinnedCarrier_RejectsAPinnedNameWithADifferentStem(
        string requestName,
        string resolvedPath)
    {
        string[] request = ["cmd.exe", "/d", "/s", "/c", $"{requestName} --flag"];
        Assert.True(CanonicalCmdCarrier.TryBuildPinnedCarrier(
            request,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            resolvedPath,
            out var pinned));

        Assert.False(CanonicalCmdCarrier.PinnedCarrierMatchesRequest(pinned, request));
    }

    private static string ReadMxcConfigBuilderSource()
    {
        var path = Path.Combine(
            ProductionSourceFiles.FindRepoRoot(),
            "src",
            "OpenClaw.Shared",
            "Mxc",
            "MxcConfigBuilder.cs");
        Assert.True(File.Exists(path), $"MxcConfigBuilder source not found: {path}");
        return File.ReadAllText(path);
    }
}
