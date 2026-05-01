using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for PR2: input validation phase of the V2 exec approval pipeline.
/// Covers structural validation only: allow, deny, malformed input.
/// No shell wrapper detection, executable resolution, or evaluation.
/// </summary>
public class ExecApprovalV2InputValidationTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static NodeInvokeRequest Req(string argsJson)
        => new() { Id = "r1", Command = "system.run", Args = Parse(argsJson) };

    // -------------------------------------------------------------------------
    // Allow paths
    // -------------------------------------------------------------------------

    [Fact]
    public void Valid_ArrayCommand_ReturnsOk()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":["echo","hello"]}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["echo", "hello"], outcome.Request!.Argv);
    }

    [Fact]
    public void Valid_StringCommand_ReturnsOk()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":"echo"}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["echo"], outcome.Request!.Argv);
    }

    [Fact]
    public void Valid_StringCommandWithSeparateArgs_MergesArgv()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","args":["hello","world"]}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["echo", "hello", "world"], outcome.Request!.Argv);
    }

    [Fact]
    public void Valid_AllOptionalFields_Parsed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""
            {
                "command": ["git", "status"],
                "cwd": "C:\\repo",
                "shell": "pwsh",
                "timeoutMs": 5000,
                "env": {"MY_VAR": "value"},
                "agentId": "agent-1",
                "sessionKey": "sess-abc"
            }
            """));

        Assert.True(outcome.IsValid);
        var r = outcome.Request!;
        Assert.Equal(["git", "status"], r.Argv);
        Assert.Equal("C:\\repo", r.Cwd);
        Assert.Equal("pwsh", r.Shell);
        Assert.Equal(5000, r.TimeoutMs);
        Assert.Equal("value", r.Env!["MY_VAR"]);
        Assert.Equal("agent-1", r.AgentId);
        Assert.Equal("sess-abc", r.SessionKey);
    }

    [Fact]
    public void Valid_DefaultTimeout_Is30000()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":"echo"}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(30_000, outcome.Request!.TimeoutMs);
    }

    [Fact]
    public void Valid_LegacyTimeoutKey_Accepted()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeout":10000}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(10_000, outcome.Request!.TimeoutMs);
    }

    [Fact]
    public void Valid_EmptyEnvObject_ReturnsOk()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":{}}"""));

        Assert.True(outcome.IsValid);
        Assert.NotNull(outcome.Request!.Env);
        Assert.Empty(outcome.Request.Env!);
    }

    // -------------------------------------------------------------------------
    // Deny paths
    // -------------------------------------------------------------------------

    [Fact]
    public void MissingCommand_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"cwd":"/tmp"}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, outcome.Error!.Code);
        Assert.Equal("missing-command", outcome.Error.Reason);
    }

    [Fact]
    public void EmptyArrayCommand_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":[]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, outcome.Error!.Code);
        Assert.Equal("missing-command", outcome.Error.Reason);
    }

    [Fact]
    public void WhitespaceCommand_ValidationFailed()
    {
        // Whitespace-only string command → IsNullOrWhiteSpace → TryParseArgv returns null → missing-command
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":"   "}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, outcome.Error!.Code);
        Assert.Equal("missing-command", outcome.Error.Reason);
    }

    [Fact]
    public void WhitespaceFirstArgvElement_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":["  ","arg"]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("empty-command", outcome.Error!.Reason);
    }

    [Fact]
    public void EmptyCwd_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","cwd":""}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("empty-cwd", outcome.Error!.Reason);
    }

    [Fact]
    public void WhitespaceCwd_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","cwd":"   "}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("empty-cwd", outcome.Error!.Reason);
    }

    [Fact]
    public void EnvNotObject_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":"bad"}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-env", outcome.Error!.Reason);
    }

    [Fact]
    public void EnvArray_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":["a","b"]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-env", outcome.Error!.Reason);
    }

    [Fact]
    public void NegativeTimeout_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeoutMs":-1}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid-timeout", outcome.Error!.Reason);
    }

    [Fact]
    public void ZeroTimeout_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeoutMs":0}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid-timeout", outcome.Error!.Reason);
    }

    [Fact]
    public void NegativeLegacyTimeout_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeout":-5000}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid-timeout", outcome.Error!.Reason);
    }

    // -------------------------------------------------------------------------
    // Malformed / edge-case inputs
    // -------------------------------------------------------------------------

    [Fact]
    public void CommandIsNumber_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":42}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    [Fact]
    public void CommandIsNull_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":null}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    [Fact]
    public void EnvWithNonStringValues_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":{"A":"ok","B":42,"C":true}}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-env", outcome.Error!.Reason);
    }

    [Fact]
    public void AbsentOptionalFields_AreNull()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":"echo"}"""));

        Assert.True(outcome.IsValid);
        var r = outcome.Request!;
        Assert.Null(r.Shell);
        Assert.Null(r.Cwd);
        Assert.Null(r.Env);
        Assert.Null(r.AgentId);
        Assert.Null(r.SessionKey);
    }

    // -------------------------------------------------------------------------
    // Error outcome shape
    // -------------------------------------------------------------------------

    [Fact]
    public void FailOutcome_ErrorIsNotNull_RequestIsNull()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{}"""));

        Assert.False(outcome.IsValid);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Request);
    }

    [Fact]
    public void OkOutcome_RequestIsNotNull_ErrorIsNull()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("""{"command":"echo"}"""));

        Assert.True(outcome.IsValid);
        Assert.NotNull(outcome.Request);
        Assert.Null(outcome.Error);
    }

    // -------------------------------------------------------------------------
    // 9. Argv trim
    // -------------------------------------------------------------------------

    [Fact]
    public void ArrayCommand_ElementsPreservedExactly()
    {
        // argv elements are not trimmed; spaces are meaningful in arguments
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":["  echo  ","  hello  "]}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["  echo  ", "  hello  "], outcome.Request!.Argv);
    }

    [Fact]
    public void StringCommand_PreservedExactly()
    {
        // argv[0] from a string command is stored as-is; whitespace check uses IsNullOrWhiteSpace
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"  echo  "}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["  echo  "], outcome.Request!.Argv);
    }

    [Fact]
    public void SeparateArgs_PreservedExactly()
    {
        // argv elements (including separate args) are not trimmed
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"  echo  ","args":["  hello  ","  world  "]}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["  echo  ", "  hello  ", "  world  "], outcome.Request!.Argv);
    }

    [Fact]
    public void ArrayCommand_WhitespaceOnlyFirstElement_EmptyCommand()
    {
        // argv[0] is whitespace-only → IsNullOrWhiteSpace check → empty-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":["  ","arg"]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("empty-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArrayCommand_WhitespaceOnlyNonFirstElement_PreservedAsIs()
    {
        // argv[1+] are not trimmed and not checked for whitespace — "  " is a valid argument
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":["echo","  "]}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(["echo", "  "], outcome.Request!.Argv);
    }

    [Fact]
    public void StringCommand_WhitespaceOnly_MissingCommand()
    {
        // Whitespace-only string command → IsNullOrWhiteSpace → TryParseArgv returns null → missing-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"  "}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    // -------------------------------------------------------------------------
    // 10. Command array — mixed element types
    // -------------------------------------------------------------------------

    [Fact]
    public void ArrayCommand_NonStringElement_ValidationFailed()
    {
        // 42 is not JsonValueKind.String → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":["echo",42,true]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArrayCommand_NullElement_ValidationFailed()
    {
        // null (JsonValueKind.Null) is not String → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":[null,"x"]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArrayCommand_OnlyNonStringElements_ValidationFailed()
    {
        // All elements are non-string → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":[42,true]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArrayCommand_SingleNull_ValidationFailed()
    {
        // [null] is non-string → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":[null]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    // -------------------------------------------------------------------------
    // 11. Separate args array — mixed element types
    // -------------------------------------------------------------------------

    [Fact]
    public void SeparateArgs_NonStringElement_ValidationFailed()
    {
        // 1, null, true are not String → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","args":["ok",1,null,true]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void SeparateArgs_NoStringElements_ValidationFailed()
    {
        // args contains only non-string elements → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","args":[1,null,true]}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void SeparateArgs_NotAnArray_ValidationFailed()
    {
        // args present but not an array → protocol violation → malformed-command
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","args":"hello"}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    [Fact]
    public void SeparateArgs_NotAnArray_ObjectValue_ValidationFailed()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","args":{"x":1}}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-command", outcome.Error!.Reason);
    }

    // -------------------------------------------------------------------------
    // 12. Timeout field precedence and upper-bound deferral
    // -------------------------------------------------------------------------

    [Fact]
    public void Timeout_TimeoutMsWinsWhenBothPresent()
    {
        // timeoutMs is checked first (if branch); timeout is the else-branch and is never read
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeoutMs":5000,"timeout":9999}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(5000, outcome.Request!.TimeoutMs);
    }

    [Fact]
    public void Timeout_InvalidTimeoutMs_DeniesEvenIfTimeoutIsValid()
    {
        // timeoutMs is invalid → deny; timeout (valid) is never reached (else-branch)
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeoutMs":-1,"timeout":5000}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("invalid-timeout", outcome.Error!.Reason);
    }

    [Fact]
    public void LargeTimeout_PassesStructuralValidation()
    {
        // Upper-bound clamping (legacy safety limit) is enforced in the execution/policy phase.
        // Structural validation accepts any positive integer.
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","timeoutMs":86400000}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal(86_400_000, outcome.Request!.TimeoutMs);
    }

    // -------------------------------------------------------------------------
    // 13. Optional string fields — non-string JSON values treated as absent
    // -------------------------------------------------------------------------

    [Fact]
    public void CwdWrongType_ValidationFailed()
    {
        // cwd affects execution semantics → wrong type is a protocol violation → malformed-cwd
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","cwd":123}"""));

        Assert.False(outcome.IsValid);
        Assert.Equal("malformed-cwd", outcome.Error!.Reason);
    }

    [Fact]
    public void ShellWrongType_TreatedAsAbsent()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","shell":true}"""));

        Assert.True(outcome.IsValid);
        Assert.Null(outcome.Request!.Shell);
    }

    [Fact]
    public void AgentIdWrongType_TreatedAsAbsent()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","agentId":{}}"""));

        Assert.True(outcome.IsValid);
        Assert.Null(outcome.Request!.AgentId);
    }

    [Fact]
    public void SessionKeyWrongType_TreatedAsAbsent()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","sessionKey":[]}"""));

        Assert.True(outcome.IsValid);
        Assert.Null(outcome.Request!.SessionKey);
    }

    // -------------------------------------------------------------------------
    // 14. Env — less-trivial combinations
    // -------------------------------------------------------------------------

    [Fact]
    public void Env_EmptyStringValue_Accepted()
    {
        // GetString() ?? "" → empty string is a valid value, not skipped
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":{"A":""}}"""));

        Assert.True(outcome.IsValid);
        Assert.Equal("", outcome.Request!.Env!["A"]);
    }

    [Fact]
    public void Env_DuplicateKeysByCasing_LastValueWins()
    {
        // StringComparer.OrdinalIgnoreCase: second assignment updates the same slot
        var outcome = ExecApprovalV2InputValidator.Validate(
            Req("""{"command":"echo","env":{"PATH":"/usr/bin","path":"/sbin"}}"""));

        Assert.True(outcome.IsValid);
        Assert.Single(outcome.Request!.Env!);
        Assert.Equal("/sbin", outcome.Request!.Env!["PATH"]);
    }

    // -------------------------------------------------------------------------
    // 15. Args root is not a JSON object
    // -------------------------------------------------------------------------

    [Fact]
    public void ArgsRootIsArray_MissingCommand()
    {
        // TryGetProperty("command") returns false on a JSON array → argv null → missing-command
        var outcome = ExecApprovalV2InputValidator.Validate(Req("[]"));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArgsRootIsString_MissingCommand()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("\"hello\""));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArgsRootIsNumber_MissingCommand()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("42"));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    [Fact]
    public void ArgsRootIsNull_MissingCommand()
    {
        var outcome = ExecApprovalV2InputValidator.Validate(Req("null"));

        Assert.False(outcome.IsValid);
        Assert.Equal("missing-command", outcome.Error!.Reason);
    }

    // -------------------------------------------------------------------------
    // 16. Validation code invariant
    // -------------------------------------------------------------------------

    [Fact]
    public void EveryFailure_ProducesValidationFailedCode()
    {
        // The Deny() helper in this validator always wraps ValidationFailed.
        // No failure path produces SecurityDeny, ResolutionFailed, or any other code.
        var cases = new[]
        {
            Req("""{}"""),                                          // missing-command
            Req("""{"command":[]}"""),                              // missing-command (empty array)
            Req("""{"command":"   "}"""),                           // missing-command (trims to empty)
            Req("""{"command":["echo",42]}"""),                     // malformed-command
            Req("""{"command":"echo","args":[1]}"""),               // malformed-command (non-string element)
            Req("""{"command":"echo","args":"hello"}"""),           // malformed-command (args not an array)
            Req("""{"command":"echo","cwd":""}"""),                 // empty-cwd
            Req("""{"command":"echo","cwd":123}"""),                // malformed-cwd
            Req("""{"command":"echo","env":"bad"}"""),              // malformed-env
            Req("""{"command":"echo","env":{"A":42}}"""),           // malformed-env (non-string value)
            Req("""{"command":"echo","timeoutMs":-1}"""),           // invalid-timeout
        };

        foreach (var req in cases)
        {
            var outcome = ExecApprovalV2InputValidator.Validate(req);
            Assert.False(outcome.IsValid);
            Assert.Equal(ExecApprovalV2Code.ValidationFailed, outcome.Error!.Code);
        }
    }
}
