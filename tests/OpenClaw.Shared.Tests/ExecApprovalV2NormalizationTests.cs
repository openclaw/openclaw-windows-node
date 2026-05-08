using System.Collections.Generic;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for PR3: normalization, executable resolution, and canonical identity.
/// Covers rail 18 steps 2-4: detect shell wrappers, resolve executable, build canonical identity.
/// Tests are UI-free (rail 10) and cover the cases required by rail 13.
/// </summary>
public class ExecApprovalV2NormalizationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ValidatedRunRequest Req(
        string[] argv,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? agentId = null,
        string? sessionKey = null) =>
        new(argv, shell: null, cwd, timeoutMs: 30_000, env, agentId, sessionKey);

    // ── ExecShellWrapperNormalizer ────────────────────────────────────────────

    [Fact]
    public void Normalizer_DirectExec_IsNotWrapper()
    {
        var r = ExecShellWrapperNormalizer.Extract(["echo", "hello"]);
        Assert.False(r.IsWrapper);
    }

    [Fact] public void Normalizer_BashWrapper() => AssertWrapper(["bash", "-c", "echo hello"], "echo hello");
    [Fact] public void Normalizer_ShWrapper()   => AssertWrapper(["sh",   "-c", "echo hello"], "echo hello");
    [Fact] public void Normalizer_ZshWrapper()  => AssertWrapper(["zsh",  "-c", "echo hello"], "echo hello");

    [Fact] public void Normalizer_CmdWrapper()    => AssertWrapper(["cmd",     "/c", "dir"], "dir");
    [Fact] public void Normalizer_CmdExeWrapper() => AssertWrapper(["cmd.exe", "/c", "dir"], "dir");

    [Fact] public void Normalizer_PowerShellCapital()  => AssertWrapper(["powershell",     "-Command", "Get-Date"], "Get-Date");
    [Fact] public void Normalizer_PwshLowerC()         => AssertWrapper(["pwsh",           "-c",       "Get-Date"], "Get-Date");
    [Fact] public void Normalizer_PowerShellExeLower() => AssertWrapper(["powershell.exe", "-command", "Get-Date"], "Get-Date");

    private static void AssertWrapper(string[] argv, string expectedPayload)
    {
        var r = ExecShellWrapperNormalizer.Extract(argv);
        Assert.True(r.IsWrapper);
        Assert.Equal(expectedPayload, r.InlineCommand);
    }

    [Fact]
    public void Normalizer_BashWithMissingPayloadToken_IsNotWrapper()
    {
        // ["bash", "-c"] has the flag but no payload token → payload is null → NotWrapper.
        // This matches the reference (windows-app): null payload → return NotWrapper, not IsWrapper=true.
        var r = ExecShellWrapperNormalizer.Extract(["bash", "-c"]);
        Assert.False(r.IsWrapper);
    }

    [Fact]
    public void Normalizer_UnknownExecutable_IsNotWrapper()
    {
        var r = ExecShellWrapperNormalizer.Extract(["node", "script.js"]);
        Assert.False(r.IsWrapper);
    }

    // ── ExecEnvInvocationUnwrapper ────────────────────────────────────────────

    [Fact]
    public void EnvUnwrapper_TransparentEnv_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "echo", "hello"]);
        Assert.NotNull(result);
        Assert.Equal(["echo", "hello"], result);
    }

    [Fact]
    public void EnvUnwrapper_EnvWithAssignment_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "FOO=bar", "echo"]);
        Assert.NotNull(result);
        Assert.Equal(["echo"], result);
    }

    [Fact]
    public void EnvUnwrapper_UnknownFlag_ReturnsNull()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "--unknown-flag", "echo"]);
        Assert.Null(result);
    }

    [Fact]
    public void EnvUnwrapper_DashDash_SkipsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "--", "echo", "hi"]);
        Assert.NotNull(result);
        Assert.Equal(["echo", "hi"], result);
    }

    [Fact]
    public void Normalizer_EnvBashWrapper_DetectsShellAfterEnv()
    {
        // env bash -c "echo hi" → IsWrapper=true (env unwrapped, then bash detected)
        var r = ExecShellWrapperNormalizer.Extract(["env", "bash", "-c", "echo hi"]);
        Assert.True(r.IsWrapper);
        Assert.Equal("echo hi", r.InlineCommand);
    }

    // ── ExecCommandResolver — singular ────────────────────────────────────────

    [Fact]
    public void Resolver_AbsolutePath_ResolvesToSelf()
    {
        var sysDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var cmd32 = System.IO.Path.Combine(sysDir, "cmd.exe");
        var res = ExecCommandResolver.Resolve([cmd32], cwd: null, env: null);
        Assert.NotNull(res);
        Assert.Equal(cmd32, res!.Value.RawExecutable);
        Assert.NotNull(res.Value.ResolvedPath);
    }

    [Fact]
    public void Resolver_UnknownBasename_ResolvesWithNullPath()
    {
        var res = ExecCommandResolver.Resolve(["totally-nonexistent-binary-xyz"], cwd: null, env: null);
        Assert.NotNull(res);
        Assert.Null(res!.Value.ResolvedPath);
        Assert.Equal("totally-nonexistent-binary-xyz", res.Value.RawExecutable);
    }

    [Fact]
    public void Resolver_EmptyArgv_ReturnsNull()
    {
        var res = ExecCommandResolver.Resolve([], cwd: null, env: null);
        Assert.Null(res);
    }

    // ── ExecCommandResolver — ResolveForAllowlist ─────────────────────────────

    [Fact]
    public void ResolveForAllowlist_DirectExec_ReturnsSingleResolution()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["echo", "hello"], evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_WrapperWithChain_ReturnsTwoResolutions()
    {
        // bash -c "echo foo && echo bar" → two segments → two resolutions
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo && echo bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
        Assert.All(resolutions, r => Assert.Equal("echo", r.ExecutableName.ToLowerInvariant()
            .Replace(".exe", "")));
    }

    [Fact]
    public void ResolveForAllowlist_BashMissingPayload_ResolvesAsBashDirectExec()
    {
        // ["bash", "-c"] → NotWrapper (no payload token) → treated as direct exec of bash.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c"], evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
        Assert.Contains("bash", resolutions[0].ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveForAllowlist_CommandSubstitution_ReturnsEmpty()
    {
        // Fail-closed: $(...) in shell payload
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo $(cat /etc/passwd)"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_Backtick_ReturnsEmpty()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo `id`"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_PowerShellEncodedCommand_ReturnsEmpty()
    {
        // Research doc 04 S1: -EncodedCommand payload is opaque — fail-closed for allowlist.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "powershell -enc dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_PowerShellRegularCommand_NotFailClosed()
    {
        // `powershell -c 'Get-Date'` is NOT -EncodedCommand — must NOT be fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "powershell -c Get-Date"],
            evaluationRawCommand: null, cwd: null, env: null);
        // powershell itself resolves (path may or may not be found, but not empty due to -enc)
        Assert.Single(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvFlagBeforeShellWrapper_ReturnsEmpty()
    {
        // env -u HOME bash -c "echo hi" → env manipulation before shell wrapper → fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "-u", "HOME", "bash", "-c", "echo hi"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvAssignmentBeforeShellWrapper_ReturnsEmpty()
    {
        // env FOO=bar bash -c "echo hi" → VAR=val before shell wrapper → fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "FOO=bar", "bash", "-c", "echo hi"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvDashDashBeforeShellWrapper_NotFailClosed()
    {
        // env -- bash -c "echo hi" → -- ends options without modifying env → transparent → not fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "--", "bash", "-c", "echo hi"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.NotEmpty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvFlagBeforeDirectExec_ReturnsEmpty()
    {
        // env -u HOME echo hello — env has modifiers → fail-closed regardless of what follows.
        // The allowlist cannot verify which executable runs under a modified environment.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "-u", "HOME", "echo", "hello"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    // ── ExecApprovalV2Normalizer — full pipeline ──────────────────────────────

    [Fact]
    public void Normalize_SimpleCommand_ProducesIdentity()
    {
        var req = Req(["echo", "hello"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        var id = outcome.Identity!;
        Assert.Equal(["echo", "hello"], id.Command);
        Assert.Contains("echo", id.DisplayCommand);
        Assert.Null(id.EvaluationRawCommand);
    }

    [Fact]
    public void Normalize_ArgvPreservedExactly_NoCodingContractViolation()
    {
        // Coding contract process-argv-semantics: no trimming of argv elements.
        var req = Req(["  echo  ", "  value  "]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        Assert.Equal(["  echo  ", "  value  "], outcome.Identity!.Command);
    }

    [Fact]
    public void Normalize_ShellWrapper_ProducesIdentityWithBothResolutions()
    {
        var req = Req(["bash", "-c", "echo foo && echo bar"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        var id = outcome.Identity!;
        // Singular resolution resolves the wrapper itself (bash) not the inner command.
        Assert.NotNull(id.Resolution);
        // Allowlist resolutions resolve the inner commands.
        Assert.Equal(2, id.AllowlistResolutions.Count);
    }

    [Fact]
    public void Normalize_BashMissingPayload_ProducesIdentityForBashDirectExec()
    {
        // ["bash", "-c"] → NotWrapper → treated as direct exec of bash → identity produced.
        var req = Req(["bash", "-c"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        Assert.Contains("bash", outcome.Identity!.DisplayCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_CommandSubstitution_AllowlistResolutionsEmpty_IdentityStillProduced()
    {
        // Command substitution causes empty AllowlistResolutions (fail-closed for allowlist)
        // but singular Resolution may still succeed — identity is produced.
        var req = Req(["bash", "-c", "echo $(id)"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        // bash itself resolves; the allowlist resolutions are empty (fail-closed inner chain)
        // → singular resolution is non-null → identity is produced
        Assert.True(outcome.IsResolved);
        Assert.Empty(outcome.Identity!.AllowlistResolutions);
    }

    [Fact]
    public void Normalize_DisplayCommand_AlwaysFromArgv_NeverRawCommand()
    {
        // DisplayCommand must be generated from argv, not rawCommand (research doc 05 decision 2).
        var req = Req(["bash", "-c", "echo hello"], agentId: "agent-1");
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        // DisplayCommand contains the full argv representation.
        Assert.Contains("bash", outcome.Identity!.DisplayCommand);
        Assert.Contains("-c", outcome.Identity!.DisplayCommand);
    }

    [Fact]
    public void Normalize_ContextFieldsCarriedThrough()
    {
        var env = new Dictionary<string, string> { ["FOO"] = "bar" };
        var req = Req(["echo"], cwd: @"C:\tmp", env: env, agentId: "a1", sessionKey: "s1");
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        var id = outcome.Identity!;
        Assert.Equal(@"C:\tmp", id.Cwd);
        Assert.Equal("a1", id.AgentId);
        Assert.Equal("s1", id.SessionKey);
        Assert.Equal(30_000, id.TimeoutMs);
        Assert.Equal("bar", id.Env!["FOO"]);
    }

    [Fact]
    public void Normalize_EvaluationRawCommand_AlwaysNullInV1()
    {
        // rawCommand is not in system.run protocol in Windows v1 (research doc 05 OQ-V4).
        var req = Req(["echo", "hello"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.True(outcome.IsResolved);
        Assert.Null(outcome.Identity!.EvaluationRawCommand);
    }

    // ── Rail compliance ───────────────────────────────────────────────────────

    [Fact]
    public void Normalize_ResolutionFailed_CarriesStableCode()
    {
        // Rail 7: every deny carries a stable code.
        // An entirely unresolvable command: empty argv is caught by PR2 upstream,
        // so we force a resolution failure with a command argv that has no resolvable executable.
        // The Normalizer denies when both singular resolution and allowlist resolutions are empty.
        // Use a path with an invalid ADS colon to force ResolveExecutable to return null.
        var req = Req(["C:\\bad:stream:path\\tool.exe"]);
        var outcome = ExecApprovalV2Normalizer.Normalize(req);

        Assert.False(outcome.IsResolved);
        Assert.Equal(ExecApprovalV2Code.ResolutionFailed, outcome.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error.Reason));
    }

    [Fact]
    public void Normalize_LegacyPath_Unaffected()
    {
        // Rail 19: legacy path must be unaffected by new-path changes.
        // The normalizer is only called from the V2 path; it does not exist in the legacy path.
        // Verify the legacy ExecShellWrapperParser type still compiles and is independent.
        // (Structural test — if this compiles, the legacy type is not modified.)
        _ = typeof(OpenClaw.Shared.ExecShellWrapperParser);
        _ = typeof(OpenClaw.Shared.ExecShellParseResult);
    }

    // ── SplitShellCommandChain (via ResolveForAllowlist) ──────────────────────

    [Fact]
    public void SplitChain_Pipe_ReturnsTwoResolutions()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo | cat"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
    }

    [Fact]
    public void SplitChain_Semicolon_ReturnsTwoResolutions()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo; echo bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
    }

    [Fact]
    public void SplitChain_Newline_ReturnsTwoResolutions()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo\necho bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
    }

    [Fact]
    public void SplitChain_BackgroundOperator_ReturnsTwoResolutions()
    {
        // `&` (background, not &&) is a delimiter.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo & echo bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
    }

    [Fact]
    public void SplitChain_PipeOr_ReturnsTwoResolutions()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo foo || echo bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Equal(2, resolutions.Count);
    }

    [Fact]
    public void SplitChain_ProcessSubstitutionLt_ReturnsEmpty()
    {
        // <(...) is process substitution — fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "cat <(echo foo)"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void SplitChain_UnclosedSingleQuote_ReturnsEmpty()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo 'unclosed"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void SplitChain_UnclosedDoubleQuote_ReturnsEmpty()
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo \"unclosed"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void SplitChain_QuotedSemicolon_NotSplit()
    {
        // Semicolon inside single quotes is not a delimiter.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "echo 'hello;world'"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
    }

    [Fact]
    public void SplitChain_BackslashEscapedSemicolon_NotSplit()
    {
        // Backslash-escaped semicolon is not a delimiter.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", @"echo foo\;bar"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
    }

    // ── ExecEnvInvocationUnwrapper — flag variants ────────────────────────────

    [Fact]
    public void EnvUnwrapper_IgnoreEnvironmentShortFlag_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "-i", "echo", "hello"]);
        Assert.NotNull(result);
        Assert.Equal(["echo", "hello"], result);
    }

    [Fact]
    public void EnvUnwrapper_IgnoreEnvironmentLongFlag_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "--ignore-environment", "echo"]);
        Assert.NotNull(result);
        Assert.Equal(["echo"], result);
    }

    [Fact]
    public void EnvUnwrapper_ChDirInlineEquals_UnwrapsToCommand()
    {
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "--chdir=/tmp", "echo"]);
        Assert.NotNull(result);
        Assert.Equal(["echo"], result);
    }

    [Fact]
    public void EnvUnwrapper_UnsetInlineForm_UnwrapsToCommand()
    {
        // -uFOO is the inline form of --unset FOO.
        var result = ExecEnvInvocationUnwrapper.Unwrap(["env", "-uFOO", "echo"]);
        Assert.NotNull(result);
        Assert.Equal(["echo"], result);
    }

    [Fact]
    public void EnvUnwrapper_NestedEnv_UnwrapsToInnerCommand()
    {
        // UnwrapForResolution handles multiple levels of env prefix.
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(
            ["env", "env", "echo", "hello"]);
        Assert.NotEmpty(result);
        Assert.Equal("echo", result[0]);
    }

    [Fact]
    public void EnvUnwrapForResolution_UnknownFlag_ReturnsEnvItself()
    {
        // Unknown flag → Unwrap returns null → UnwrapForResolution stops, returns original argv.
        var result = ExecEnvInvocationUnwrapper.UnwrapForResolution(
            ["env", "--unknown-flag", "echo"]);
        Assert.NotEmpty(result);
        Assert.Equal("env", result[0]);
    }

    // ── ExecCommandResolver.ResolveAllowAlwaysPatterns ────────────────────────

    [Fact]
    public void AllowAlwaysPatterns_DirectExec_ReturnsExecutablePattern()
    {
        var patterns = ExecCommandResolver.ResolveAllowAlwaysPatterns(
            ["echo", "hello"], cwd: null, env: null);
        Assert.Single(patterns);
        Assert.Contains("echo", patterns[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowAlwaysPatterns_ShellWrapper_ReturnsInnerPatterns()
    {
        // echo deduplicates → 1 pattern.
        var patterns = ExecCommandResolver.ResolveAllowAlwaysPatterns(
            ["bash", "-c", "echo foo && echo bar"], cwd: null, env: null);
        Assert.Single(patterns);
        Assert.Contains("echo", patterns[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowAlwaysPatterns_CommandSubstitution_ReturnsEmpty()
    {
        // SplitShellCommandChain returns null → CollectPatterns bails out → empty.
        var patterns = ExecCommandResolver.ResolveAllowAlwaysPatterns(
            ["bash", "-c", "echo $(id)"], cwd: null, env: null);
        Assert.Empty(patterns);
    }

    [Fact]
    public void AllowAlwaysPatterns_EncodedCommand_NotFailClosed()
    {
        // Unlike ResolveForAllowlist, AllowAlwaysPatterns is UX-only and does not
        // fail-closed on -enc: it resolves the first token (powershell) as the pattern.
        var patterns = ExecCommandResolver.ResolveAllowAlwaysPatterns(
            ["bash", "-c", "powershell -enc dABlAHMAdAA="], cwd: null, env: null);
        Assert.NotEmpty(patterns);
        Assert.Contains("powershell", patterns[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecCommandResolver — path resolution edge cases ─────────────────────

    [Fact]
    public void Resolver_TildeExpanded_RawExecutableHasNoTilde()
    {
        var res = ExecCommandResolver.Resolve(
            ["~/bin/nonexistent-tool-xyz"], cwd: null, env: null);
        Assert.NotNull(res);
        Assert.False(res!.Value.RawExecutable.StartsWith('~'));
    }

    [Fact]
    public void Resolver_RelativePath_ResolvedToAbsoluteWithCwd()
    {
        var res = ExecCommandResolver.Resolve(
            ["./nonexistent-tool-xyz"], cwd: @"C:\tmp", env: null);
        Assert.NotNull(res);
        Assert.NotNull(res!.Value.ResolvedPath);
        Assert.True(System.IO.Path.IsPathFullyQualified(res.Value.ResolvedPath!));
        Assert.StartsWith(@"C:\tmp", res.Value.ResolvedPath, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_CustomPathEnv_FindsExecutableInCustomDir()
    {
        // Provide System32 explicitly via env PATH — cmd.exe must be found.
        var sysDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var env = new Dictionary<string, string> { ["PATH"] = sysDir };
        var res = ExecCommandResolver.Resolve(["cmd.exe"], cwd: null, env: env);
        Assert.NotNull(res);
        Assert.NotNull(res!.Value.ResolvedPath);
        Assert.Contains("System32", res.Value.ResolvedPath, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── Finding #3: path/token parsing hardening (Hanselman review) ──────────

    [Fact]
    public void Resolver_NonLetterDriveColon_ReturnsNull()
    {
        // '1' at the drive position is not an ASCII letter — HasNonStandardColon must reject it.
        // Previously colonIdx==1 was accepted without checking char.IsAsciiLetter.
        var res = ExecCommandResolver.Resolve([@"1:\tool.exe"], cwd: null, env: null);
        Assert.Null(res);
    }

    [Fact]
    public void Resolver_ExtendedLengthPath_NotRejectedByColonCheck()
    {
        // \\?\C:\... is a valid extended-length path; HasNonStandardColon must not reject it.
        var sysDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var extended = @"\\?\" + System.IO.Path.Combine(sysDir, "cmd.exe");
        var res = ExecCommandResolver.Resolve([extended], cwd: null, env: null);
        Assert.NotNull(res); // colon check must not block \\?\C:\ paths
    }

    [Fact]
    public void ResolveForAllowlist_ParseFirstToken_UnclosedQuote_FailClosed()
    {
        // Unclosed quote in shell payload — ParseFirstToken must return null (fail-closed).
        // Previously the old code returned rest.ToString() on end<0, silently swallowing the token.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "\"unclosed arg"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_QuotedTokenWithExtensionSuffix_SuffixPreserved()
    {
        // "git".exe in a shell segment — inner="git", suffix=".exe" → token="git.exe".
        // Previously ParseFirstToken lost the suffix, producing RawExecutable="git" instead.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "\"git\".exe --version"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
        Assert.EndsWith(".exe", resolutions[0].RawExecutable, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── Finding #2: env modifiers fail-closed (Hanselman review) ─────────────

    [Fact]
    public void ResolveForAllowlist_EnvAssignmentBeforeDirectExec_ReturnsEmpty()
    {
        // env PATH=/evil wget — VAR=val modifier changes which executable resolves at runtime.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "PATH=/evil", "wget"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvUnknownFlagBeforeShellWrapper_ReturnsEmpty()
    {
        // env --bogus bash -c "..." — Hanselman called this out explicitly.
        // Unknown flag → HasModifiers=true (starts with '-') → fail-closed.
        // Must NOT degrade to "resolve env itself as the executable".
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "--bogus", "bash", "-c", "echo hi"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    // ── Finding #1: -EncodedCommand detection (Hanselman review) ─────────────

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellEncodedCommand_ReturnsEmpty()
    {
        // Direct top-level ["powershell", "-EncodedCommand", "..."] — payload is opaque.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "-EncodedCommand", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPwshEcAlias_ReturnsEmpty()
    {
        // -ec is an official alias for -EncodedCommand.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["pwsh", "-ec", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellEncAbbreviation_ReturnsEmpty()
    {
        // -enco is an unambiguous prefix abbreviation of -EncodedCommand.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "-enco", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Theory]
    [InlineData("powershell", "-en")]
    [InlineData("pwsh", "-en")]
    [InlineData("powershell", "-en:dABlAHMAdAA=")]
    [InlineData("powershell", "-en=dABlAHMAdAA=")]
    public void ResolveForAllowlist_DirectPowerShellEnAbbreviation_ReturnsEmpty(string shell, string flag)
    {
        // -en is also an unambiguous prefix abbreviation of -EncodedCommand.
        var command = flag.Contains('=') || flag.Contains(':')
            ? new[] { shell, flag }
            : new[] { shell, flag, "dABlAHMAdAA=" };

        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            command,
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellExeEnc_ReturnsEmpty()
    {
        // powershell.exe (with .exe suffix) must also be caught.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell.exe", "-enc", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvTransparentPwshEnc_ReturnsEmpty()
    {
        // ["env", "pwsh", "-enc", "..."] — transparent env prefix, no modifiers, but inner
        // command is powershell with -EncodedCommand → DirectExecUsesEncodedCommand catches it.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "pwsh", "-enc", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_EnvTransparentPwshEnAbbreviation_ReturnsEmpty()
    {
        // Transparent env prefix must still fail-closed when inner pwsh uses -en.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["env", "pwsh", "-en", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_SegmentPowerShellQuotedEncFlag_ReturnsEmpty()
    {
        // bash -c 'powershell "-enc" base64' — quoted -enc in shell segment → fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "powershell \"-enc\" dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Theory]
    [InlineData("powershell -en dABlAHMAdAA=")]
    [InlineData("powershell \"-en\" dABlAHMAdAA=")]
    public void ResolveForAllowlist_SegmentPowerShellEnAbbreviation_ReturnsEmpty(string payload)
    {
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", payload],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_SegmentPowerShellColonForm_ReturnsEmpty()
    {
        // -EncodedCommand:payload (colon separator) — must be fail-closed.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "powershell -EncodedCommand:dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_WrapperPowerShellCommandPayload_NotFailClosed()
    {
        // powershell -Command <payload> is a shell wrapper invocation (not direct exec).
        // The wrapper path must not fail-closed when the payload contains no -EncodedCommand.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "-Command", "Get-Date"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellScriptFile_NotFailClosed()
    {
        // Direct exec path: ["powershell", "script.ps1"] — no inline flag, no -EncodedCommand.
        // DirectExecUsesEncodedCommand must not trigger; must resolve as a single resolution.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "script.ps1"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
        Assert.Contains("powershell", resolutions[0].ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellEncEqualsForm_ReturnsEmpty()
    {
        // -enc=payload (equals separator) — Hanselman listed this form explicitly.
        // IsEncodedCommandFlag strips the =payload part before comparing.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "-enc=dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPowerShellEAlias_ReturnsEmpty()
    {
        // Windows PowerShell accepts -e as a short alias for -EncodedCommand.
        // Hanselman review: this was the missing gap in detection.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["powershell", "-e", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_DirectPwshEAlias_ReturnsEmpty()
    {
        // pwsh also accepts -e as short for -EncodedCommand.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["pwsh", "-e", "dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_SegmentPowerShellEAlias_ReturnsEmpty()
    {
        // Shell-wrapper segment: bash -c "powershell -e base64" — segment scanner must catch -e.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "powershell -e dABlAHMAdAA="],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Empty(resolutions);
    }

    [Fact]
    public void ResolveForAllowlist_QuotedPathWithSpacesAndSuffix_SuffixPreserved()
    {
        // Hanselman's specific example: "C:\Program Files\Git\bin\git".exe
        // Quoted path with spaces inside + bare suffix after the closing quote.
        // ParseFirstToken must produce the full path with .exe appended.
        var resolutions = ExecCommandResolver.ResolveForAllowlist(
            ["bash", "-c", "\"C:\\Program Files\\Git\\bin\\git\".exe --version"],
            evaluationRawCommand: null, cwd: null, env: null);
        Assert.Single(resolutions);
        Assert.EndsWith(".exe", resolutions[0].RawExecutable, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Program Files", resolutions[0].RawExecutable, System.StringComparison.OrdinalIgnoreCase);
    }
}
