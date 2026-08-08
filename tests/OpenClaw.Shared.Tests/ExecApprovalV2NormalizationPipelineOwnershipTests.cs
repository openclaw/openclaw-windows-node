using System;
using System.Collections.Generic;
using System.IO;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Ledger guard for `exec-multi-segment-allowlist-closed`.
///
/// Durable allowlist authorization has exactly one source of truth:
/// ExecReusableCommandBinder.TryBind. ExecCommandResolver.ResolveForAllowlist and
/// ResolveAllowAlwaysPatterns are retired from the security path. They still
/// compile, and their historical tests still pass, which is precisely why a guard
/// is needed: a passing ResolveForAllowlist test says nothing about the safety of
/// the pipeline, because the pipeline no longer calls it.
/// </summary>
public class ExecApprovalV2NormalizationPipelineOwnershipTests
{
    [Fact]
    public void Normalizer_DerivesDurableIdentity_OnlyFromReusableBinder()
    {
        var source = ReadNormalizerSource();

        Assert.DoesNotContain("ResolveForAllowlist", source);
        Assert.DoesNotContain("ResolveAllowAlwaysPatterns", source);
        Assert.Contains("ExecReusableCommandBinder.TryBind", source);
    }

    [Fact]
    public void RetiredResolverMethods_HaveNoProductionCallers()
    {
        var offenders = new List<string>();
        var sourceRoot = Path.Combine(ProductionSourceFiles.FindRepoRoot(), "src");

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // The declaring file and its own doc comment are the only allowed mentions.
            if (file.EndsWith("ExecCommandResolution.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            if (text.Contains("ResolveForAllowlist", StringComparison.Ordinal)
                || text.Contains("ResolveAllowAlwaysPatterns", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(sourceRoot, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Retired multi-segment resolver methods were wired back into production code: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void UnbindableCommand_YieldsNoDurableIdentity()
    {
        // Behavioral counterpart to the source-shape assertions: a shell-syntax
        // payload must produce neither an allowlist resolution nor an Allow Always
        // pattern, regardless of what the retired resolvers would have returned.
        var outcome = ExecApprovalV2Normalizer.Normalize(
            MakeRequest(["cmd.exe", "/d", "/s", "/c", "hostname.exe | findstr.exe host"]));

        Assert.True(outcome.IsResolved);
        Assert.Null(outcome.Identity!.ReusableCommand);
        Assert.Empty(outcome.Identity.AllowlistResolutions);
        Assert.Empty(outcome.Identity.AllowAlwaysPatterns);
        // The carrier still resolves, so the prompt has a path to display.
        Assert.NotNull(outcome.Identity.Resolution);
    }

    [Fact]
    public void BindableCommand_YieldsExactlyOneDurableIdentity()
    {
        var outcome = ExecApprovalV2Normalizer.Normalize(
            MakeRequest(
                ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
                ExecTestPath.SystemOnly));

        Assert.True(outcome.IsResolved);
        Assert.NotNull(outcome.Identity!.ReusableCommand);
        Assert.Single(outcome.Identity.AllowlistResolutions);
        Assert.Single(outcome.Identity.AllowAlwaysPatterns);
        Assert.Equal(
            outcome.Identity.ReusableCommand!.Pattern,
            outcome.Identity.AllowAlwaysPatterns[0]);
    }

    private static ValidatedRunRequest MakeRequest(
        string[] argv,
        IReadOnlyDictionary<string, string>? env = null) =>
        new(argv, cwd: null, timeoutMs: 30_000, env: env, agentId: null, sessionKey: null);

    private static string ReadNormalizerSource()
    {
        var path = Path.Combine(
            ProductionSourceFiles.FindRepoRoot(),
            "src",
            "OpenClaw.Shared",
            "ExecApprovals",
            "ExecApprovalV2NormalizationStep.cs");
        Assert.True(File.Exists(path), $"Normalizer source not found: {path}");
        return File.ReadAllText(path);
    }
}
