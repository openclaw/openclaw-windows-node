using OpenClawTray.Pages;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class ExecPolicyRuleListTests
{
    [Fact]
    public void UpsertByPattern_AppendsNewPattern()
    {
        var rules = new List<ExecPolicyRule>
        {
            new() { Pattern = "cat *", Action = "allow" }
        };

        ExecPolicyRuleList.UpsertByPattern(rules, "del *", "prompt");

        Assert.Collection(
            rules,
            rule =>
            {
                Assert.Equal("cat *", rule.Pattern);
                Assert.Equal("allow", rule.Action);
            },
            rule =>
            {
                Assert.Equal("del *", rule.Pattern);
                Assert.Equal("prompt", rule.Action);
            });
    }

    [Fact]
    public void UpsertByPattern_ReplacesExistingPatternInPlace()
    {
        var rules = new List<ExecPolicyRule>
        {
            new() { Pattern = "cat *", Action = "allow" },
            new() { Pattern = "del *", Action = "deny" },
            new() { Pattern = "rm *", Action = "deny" }
        };

        ExecPolicyRuleList.UpsertByPattern(rules, "del *", "prompt");

        Assert.Equal(3, rules.Count);
        Assert.Equal("del *", rules[1].Pattern);
        Assert.Equal("prompt", rules[1].Action);
        Assert.Equal("rm *", rules[2].Pattern);
    }

    [Fact]
    public void UpsertByPattern_RemovesLaterDuplicatePatterns()
    {
        var rules = new List<ExecPolicyRule>
        {
            new() { Pattern = "cat *", Action = "allow" },
            new() { Pattern = "DEL *", Action = "deny" },
            new() { Pattern = "rm *", Action = "deny" },
            new() { Pattern = " del * ", Action = "prompt" }
        };

        ExecPolicyRuleList.UpsertByPattern(rules, "del *", "allow");

        Assert.Collection(
            rules,
            rule => Assert.Equal("cat *", rule.Pattern),
            rule =>
            {
                Assert.Equal("del *", rule.Pattern);
                Assert.Equal("allow", rule.Action);
            },
            rule => Assert.Equal("rm *", rule.Pattern));
    }

    [Theory]
    [InlineData("\"allow\"", "allow")]
    [InlineData("\"ask\"", "prompt")]
    [InlineData("\"prompt\"", "prompt")]
    [InlineData("\"deny\"", "deny")]
    [InlineData("0", "allow")]
    [InlineData("1", "deny")]
    [InlineData("2", "prompt")]
    [InlineData("99", "deny")]
    public void NormalizeExecPolicyAction_AcceptsLegacyStringAndNumericValues(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, ExecPolicyRuleList.NormalizeAction(doc.RootElement));
    }

    [Fact]
    public void TryGetExecPolicyActionCaseInsensitive_ReadsNumericAction()
    {
        using var doc = JsonDocument.Parse("""{"Action":2}""");

        Assert.Equal("prompt", ExecPolicyRuleList.TryGetActionCaseInsensitive(doc.RootElement, "action", "Action"));
    }

    [Fact]
    public void PersistedEnabled_OmitsDefaultTrueButPreservesFalse()
    {
        Assert.Null(ExecPolicyRuleList.PersistedEnabled(true));
        Assert.False(ExecPolicyRuleList.PersistedEnabled(false));
    }
}
