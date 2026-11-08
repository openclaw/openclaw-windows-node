// Known message-send consent survives event/list parsing and exact immutable observation comparison.
// Run with --filter FullyQualifiedName~GatewayMessageSendScopeTests.
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayMessageSendScopeTests
{
    [Theory]
    [InlineData("", false, null)]
    [InlineData(",\"recipients\":[\" Reader@example.test \",\"second@example.test\"]", true, null)]
    [InlineData(",\"audience\":\"internal\"", false, "internal")]
    [InlineData(",\"recipients\":[\" Reader@example.test \",\"second@example.test\"],\"audience\":\"external\"", true, "external")]
    [InlineData(",\"recipients\":[]", true, null)]
    public void EventAndListRetainAbsentEachBothAndEmptyFields(string fields, bool recipientsSpecified, string? audience)
    {
        var payload = Payload(fields);
        Assert.True(GatewayPendingApprovalParser.TryGetEventKind("exec.approval.requested", out var kind, out var requested));
        Assert.True(requested);
        Assert.True(GatewayPendingApprovalParser.TryParse(kind, payload, out var approval));
        var listReply = JsonSerializer.SerializeToElement(new[] { payload });
        var listed = Assert.Single(GatewayPendingApprovalParser.ParseList(kind, listReply));
        Assert.True(GatewayPendingApprovalParser.TryParseCompleteList(kind, listReply, out var complete));
        Assert.True(approval.HasSameContentAs(Assert.Single(complete)));
        foreach (var parsed in new[] { approval, listed })
        {
            var scope = Assert.IsType<GatewayApprovalScope>(parsed.Scope);
            Assert.Equal(GatewayApprovalScopeKind.MessageSend, scope.Kind);
            Assert.Equal("email", scope.Target);
            Assert.Equal(2, scope.RecipientCount);
            Assert.Equal(audience, scope.Audience);
            Assert.Equal(recipientsSpecified, scope.Recipients is not null);
            if (scope.Recipients is { Count: > 0 })
                Assert.Equal([" Reader@example.test ", "second@example.test"], scope.Recipients);
            else if (recipientsSpecified)
                Assert.Empty(scope.Recipients!);
            Assert.Equal(["allow-once", "allow-always", "deny"], GatewayApprovalDecisions.Effective(parsed));
            Assert.Equal(["deny"], GatewayApprovalDecisions.EffectiveForChat(parsed));
        }
        Assert.True(approval.HasSameContentAs(listed));
        Assert.Equal(approval.Scope, listed.Scope);
        Assert.Equal(approval.Scope!.GetHashCode(), listed.Scope!.GetHashCode());
    }

    [Fact]
    public void PluginEventAndListAlsoRetainMessageSendDetails()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            id = "plugin-message", createdAtMs = 1, expiresAtMs = 60001,
            request = new { title = "Send report", description = "Send the report after review", scope = new
            { kind = "message-send", target = "email", recipientCount = 1, recipients = new[] { "reader@example.test" }, audience = "external" } },
        });
        Assert.True(GatewayPendingApprovalParser.TryGetEventKind("plugin.approval.requested", out var kind, out var requested));
        Assert.True(requested);
        Assert.True(GatewayPendingApprovalParser.TryParse(kind, payload, out var approval));
        var listed = Assert.Single(GatewayPendingApprovalParser.ParseList(kind, JsonSerializer.SerializeToElement(new[] { payload })));
        Assert.Equal(GatewayApprovalScopeKind.MessageSend, approval.Scope!.Kind);
        Assert.Equal(["reader@example.test"], approval.Scope.Recipients);
        Assert.Equal("external", approval.Scope.Audience);
        Assert.True(approval.HasSameContentAs(listed));
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void MaximumRecipientSampleAndUnicodeLengthRemainValid()
    {
        var recipients = Enumerable.Range(0, 5).Select(index => index == 4
            ? string.Concat(Enumerable.Repeat("😀", 128)) : new string((char)('a' + index), 128)).ToArray();
        var approval = Parse(",\"recipients\":" + JsonSerializer.Serialize(recipients) + ",\"audience\":\"external\"");
        Assert.Equal(GatewayApprovalScopeKind.MessageSend, approval.Scope!.Kind);
        Assert.Equal(recipients, approval.Scope.Recipients);
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
    }

    public static IEnumerable<object[]> MalformedDetails()
    {
        foreach (var fields in new[]
        {
            ",\"recipients\":null", ",\"recipients\":{}", ",\"recipients\":\"reader\"",
            ",\"recipients\":[null]", ",\"recipients\":[1]", ",\"recipients\":[\"\"]",
            ",\"recipients\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]",
            ",\"recipients\":[\"first\"],\"recipients\":[\"second\"]",
            ",\"audience\":null", ",\"audience\":1", ",\"audience\":\"\"",
            ",\"audience\":\"public\"", ",\"audience\":\"External\"", ",\"audience\":\" external \"",
            ",\"audience\":\"internal\",\"audience\":\"external\"",
            ",\"recipients\":[],\"futureConsent\":\"not disclosed\"",
            ",\"recipients\":[" + JsonSerializer.Serialize(new string('x', 129)) + "]",
            ",\"recipients\":[" + JsonSerializer.Serialize(string.Concat(Enumerable.Repeat("😀", 129))) + "]",
        }) yield return [fields];
    }

    [Theory]
    [MemberData(nameof(MalformedDetails))]
    public void MalformedDuplicateUnknownOrOutOfBoundsDetailsStayFailClosed(string fields)
    {
        var payload = Payload(fields);
        var approval = Parse(fields);
        var listed = Assert.Single(GatewayPendingApprovalParser.ParseList(GatewayApprovalKind.Exec,
            JsonSerializer.SerializeToElement(new { approvals = new[] { payload } })));
        foreach (var parsed in new[] { approval, listed })
        {
            Assert.Equal(GatewayApprovalScopeKind.Unreviewable, parsed.Scope!.Kind);
            Assert.False(GatewayApprovalDecisions.CanReviewInFull(parsed));
            Assert.Equal(["deny"], GatewayApprovalDecisions.Effective(parsed));
        }
    }

    [Theory]
    [InlineData(",\"recipients\":[]")]
    [InlineData(",\"audience\":\"internal\"")]
    public void AbsentOptionalDetailsAreNotEquivalentToPresentValues(string fields) =>
        Assert.False(Parse("").HasSameContentAs(Parse(fields)));

    [Theory]
    [InlineData("recipient")]
    [InlineData("case")]
    [InlineData("order")]
    [InlineData("audience")]
    public void ExactOrdinalRecipientOrderAndAudienceArePartOfEquality(string change)
    {
        var original = Parse(",\"recipients\":[\"Reader@example.test\",\"second@example.test\"],\"audience\":\"internal\"");
        string[] recipients = change switch
        {
            "recipient" => new[] { "Other@example.test", "second@example.test" },
            "case" => ["reader@example.test", "second@example.test"],
            "order" => ["second@example.test", "Reader@example.test"],
            _ => ["Reader@example.test", "second@example.test"],
        };
        var changed = Parse(",\"recipients\":" + JsonSerializer.Serialize(recipients) +
            ",\"audience\":\"" + (change == "audience" ? "external" : "internal") + "\"");
        Assert.NotEqual(original.Scope, changed.Scope);
        Assert.False(original.HasSameContentAs(changed));
    }

    [Fact]
    public void SingleLineRecipientReviewPreservesSharedBudgetsAndExistingMultilineMode()
    {
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeSingleLineForReviewWithStatus("A\r\nB\t\uE000");
        Assert.Equal("A\\u{000D}\\u{000A}B\\u{0009}\\u{E000}", status.Text);
        Assert.False(status.Truncated);
        Assert.Equal("A\nB\t\\u{E000}", ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReview("A\r\nB\t\uE000"));
        Assert.True(ExecApprovalCommandDisplaySanitizer.SanitizeSingleLineForReviewWithStatus(new string('x', 16 * 1024 + 1)).Truncated);
        Assert.True(ExecApprovalCommandDisplaySanitizer.SanitizeSingleLineForReviewWithStatus(new string('x', 256 * 1024 + 1)).Oversized);
    }

    [Fact]
    public void DuplicateRecipientValuesAreRetainedRatherThanDeduplicated()
    {
        var approval = Parse(",\"recipients\":[\"reader@example.test\",\"reader@example.test\"]");
        Assert.Equal(["reader@example.test", "reader@example.test"], approval.Scope!.Recipients);
        Assert.Contains("allow-once", GatewayApprovalDecisions.Effective(approval));
    }

    [Fact]
    public void RecipientCollectionOwnershipCannotMutateAnObservedScope()
    {
        var input = new[] { "first@example.test" };
        var scope = new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = input };
        var clone = scope with { Audience = "external" };
        input[0] = "replaced@example.test";
        Assert.Equal(["first@example.test"], scope.Recipients);
        Assert.Equal(["first@example.test"], clone.Recipients);
        var exposed = Assert.IsAssignableFrom<IList<string>>(scope.Recipients);
        Assert.Throws<NotSupportedException>(() => { exposed[0] = "injected@example.test"; });
        var replacement = new List<string> { "next@example.test" };
        var replaced = scope with { Recipients = replacement };
        replacement[0] = "mutated@example.test";
        Assert.Equal(["next@example.test"], replaced.Recipients);
        Assert.Equal(["first@example.test"], scope.Recipients);
        var equal = new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = ["first@example.test"] };
        Assert.True(scope == equal);
        Assert.Equal(scope.GetHashCode(), equal.GetHashCode());
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("automation")]
    [InlineData("command")]
    [InlineData("visibility")]
    [InlineData("days")]
    [InlineData("target")]
    [InlineData("count")]
    [InlineData("amount")]
    [InlineData("currency")]
    public void StructuralEqualityStillIncludesEveryPreexistingScopeField(string field)
    {
        var scope = new GatewayApprovalScope(GatewayApprovalScopeKind.MessageSend)
        { Target = "email", RecipientCount = 1, Recipients = ["first@example.test"], Audience = "internal" };
        var changed = field switch
        {
            "kind" => scope with { Kind = GatewayApprovalScopeKind.Payment },
            "automation" => scope with { Automation = "nightly" },
            "command" => scope with { Command = "echo hi" },
            "visibility" => scope with { Visibility = "public" },
            "days" => scope with { ExpiresInDays = 1 },
            "target" => scope with { Target = "EMAIL" },
            "count" => scope with { RecipientCount = 2 },
            "amount" => scope with { Amount = "10" },
            _ => scope with { Currency = "USD" },
        };
        Assert.NotEqual(scope, changed);
    }

    private static JsonElement Payload(string fields)
    {
        using var doc = JsonDocument.Parse(
            """{"id":"message","createdAtMs":1,"expiresAtMs":60001,"request":{"command":"echo preview","scope":{"kind":"message-send","target":"email","recipientCount":2""" + fields + "}}}");
        return doc.RootElement.Clone();
    }

    private static GatewayPendingApproval Parse(string fields)
    {
        Assert.True(GatewayPendingApprovalParser.TryParse(GatewayApprovalKind.Exec, Payload(fields), out var approval));
        return approval;
    }
}
