using OpenClawTray.Pages;

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

    [Fact]
    public void CoalesceDuplicatePatterns_PreservesFirstEffectiveAction()
    {
        var rules = new List<ExecPolicyRule>
        {
            new() { Pattern = "cat *", Action = "allow" },
            new() { Pattern = "del *", Action = "deny" },
            new() { Pattern = "rm *", Action = "deny" },
            new() { Pattern = "DEL *", Action = "prompt" },
            new() { Pattern = " del * ", Action = "allow" }
        };

        var changed = ExecPolicyRuleList.CoalesceDuplicatePatterns(rules);

        Assert.True(changed);
        Assert.Collection(
            rules,
            rule => Assert.Equal("cat *", rule.Pattern),
            rule =>
            {
                Assert.Equal("del *", rule.Pattern);
                Assert.Equal("deny", rule.Action);
            },
            rule => Assert.Equal("rm *", rule.Pattern));
    }

    [Fact]
    public void CoalesceDuplicatePatterns_ReturnsFalseWithoutDuplicatePatterns()
    {
        var rules = new List<ExecPolicyRule>
        {
            new() { Pattern = "cat *", Action = "allow" },
            new() { Pattern = "del *", Action = "deny" }
        };

        var changed = ExecPolicyRuleList.CoalesceDuplicatePatterns(rules);

        Assert.False(changed);
        Assert.Equal(2, rules.Count);
    }
}
