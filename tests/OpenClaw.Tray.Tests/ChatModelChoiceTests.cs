using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class ChatModelChoiceTests
{
    // ── FromModelsList mapping ───────────────────────────────────────────

    [Fact]
    public void FromModelsList_MapsAllFields()
    {
        var info = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo
                {
                    Id = "claude-opus-4.8",
                    Name = "Claude Opus 4.8",
                    Provider = "Anthropic",
                    ContextWindow = 200000,
                    IsConfigured = true,
                    IsDefault = true,
                    IsAvailable = true,
                    RequiresAuth = false,
                },
            }
        };

        var choices = ChatModelChoice.FromModelsList(info);

        var c = Assert.Single(choices);
        Assert.Equal("claude-opus-4.8", c.Id);
        Assert.Equal("Anthropic/claude-opus-4.8", c.SelectionId);
        Assert.Equal("Claude Opus 4.8", c.DisplayName);
        Assert.Equal("Anthropic", c.Provider);
        Assert.Equal(200000, c.ContextWindow);
        Assert.True(c.IsConfigured);
        Assert.True(c.IsDefault);
        Assert.True(c.IsAvailable);
        Assert.True(c.IsSelectable);
    }

    [Fact]
    public void FromModelsList_DedupesBySelectionId_FirstWins_SkipsEmptyIds()
    {
        var info = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo { Id = "gpt-5.4", Name = "GPT-5.4", Provider = "openai" },
                new ModelInfo { Id = "gpt-5.4", Name = "GPT-5.4 via OpenRouter", Provider = "openrouter" },
                new ModelInfo { Id = "gpt-5.4", Name = "dupe", Provider = "openai" },
                new ModelInfo { Id = "", Name = "blank" },
            }
        };

        var choices = ChatModelChoice.FromModelsList(info);

        Assert.Equal(2, choices.Count);
        Assert.Equal("GPT-5.4", choices[0].DisplayName);
        Assert.Equal("openai/gpt-5.4", choices[0].SelectionId);
        Assert.Equal("GPT-5.4 via OpenRouter", choices[1].DisplayName);
        Assert.Equal("openrouter/gpt-5.4", choices[1].SelectionId);
    }

    [Fact]
    public void FromModelsList_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(ChatModelChoice.FromModelsList(null));
        Assert.Empty(ChatModelChoice.FromModelsList(new ModelsListInfo()));
    }

    [Fact]
    public void FromModelsList_FallsBackToIdWhenNameMissing()
    {
        var info = new ModelsListInfo { Models = { new ModelInfo { Id = "ollama-x" } } };
        Assert.Equal("ollama-x", ChatModelChoice.FromModelsList(info)[0].DisplayName);
    }

    [Fact]
    public void FromModelsList_HidesExplicitlyUnconfiguredModels()
    {
        var info = new ModelsListInfo
        {
            Models =
            {
                // Provider explicitly reported as not configured with no auth path → hidden.
                new ModelInfo { Id = "unconfigured", HasConfiguredFlag = true, IsConfigured = false },
                // Auth-needed rows stay visible so users can choose the provider-auth path.
                new ModelInfo { Id = "needs-key", HasConfiguredFlag = true, IsConfigured = false, RequiresAuth = true },
                // Configured → kept.
                new ModelInfo { Id = "ready", HasConfiguredFlag = true, IsConfigured = true },
                // Flag omitted entirely → kept (we don't know, so don't hide).
                new ModelInfo { Id = "unknown" },
            }
        };

        var choices = ChatModelChoice.FromModelsList(info);
        Assert.Equal(new[] { "needs-key", "ready", "unknown" }, choices.Select(c => c.Id).ToArray());
        Assert.True(choices[0].RequiresAuth);
        Assert.True(choices[0].IsSelectable);
    }

    // ── Selectability ────────────────────────────────────────────────────

    [Fact]
    public void IsSelectable_FalseOnlyWhenUnavailable()
    {
        Assert.True(new ChatModelChoice("x", "X").IsSelectable);
        // Auth-needed stays selectable (routes to provider auth).
        Assert.True(new ChatModelChoice("x", "X", RequiresAuth: true).IsSelectable);
        Assert.False(new ChatModelChoice("x", "X", IsAvailable: false).IsSelectable);
    }

    [Theory]
    [InlineData("gpt-5.4", "openai", "openai/gpt-5.4")]
    [InlineData("openai/gpt-5.4", "openai", "openai/gpt-5.4")]
    [InlineData("openai/gpt-5.4", "vercel-ai-gateway", "vercel-ai-gateway/openai/gpt-5.4")]
    [InlineData("custom-model", null, "custom-model")]
    public void SelectionId_ProviderQualifiesRawModelIds(string modelId, string? provider, string expected)
    {
        var c = new ChatModelChoice(modelId, modelId, Provider: provider);
        Assert.Equal(expected, c.SelectionId);
    }

    [Fact]
    public void ResolveSelectionId_UsesProviderToDisambiguateDuplicateRawModelIds()
    {
        var choices = new[]
        {
            new ChatModelChoice("gpt-5.4", "GPT-5.4", Provider: "openai"),
            new ChatModelChoice("gpt-5.4", "GPT-5.4", Provider: "openrouter"),
        };

        Assert.Equal(
            "openrouter/gpt-5.4",
            ChatModelChoice.ResolveSelectionId("gpt-5.4", "openrouter", choices));
        Assert.Equal("gpt-5.4", ChatModelChoice.ResolveSelectionId("gpt-5.4", null, choices));
    }

    [Fact]
    public void ResolveSelectionId_MatchesProviderCaseInsensitively()
    {
        var choices = new[]
        {
            new ChatModelChoice("gpt-5.4", "GPT-5.4", Provider: "Anthropic"),
        };

        Assert.Equal(
            "Anthropic/gpt-5.4",
            ChatModelChoice.ResolveSelectionId("gpt-5.4", "anthropic", choices));
    }

    [Fact]
    public void ResolveSelectionId_UsesBareCachedChoiceWhenProviderRichChoiceIsUnavailable()
    {
        var choices = new[]
        {
            new ChatModelChoice("gpt-5.4", "GPT-5.4"),
        };

        Assert.Equal("gpt-5.4", ChatModelChoice.ResolveSelectionId("gpt-5.4", "openrouter", choices));
    }

    // ── Tracking-default predicate ───────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("gpt-5.5", false)]
    public void IsTrackingDefault_DetectsEmptyOrNull(string? id, bool expected) =>
        Assert.Equal(expected, ChatModelLabels.IsTrackingDefault(id));

    // ── Context-window formatting ────────────────────────────────────────

    [Theory]
    [InlineData(272000, "272K")]
    [InlineData(200000, "200K")]
    [InlineData(128000, "128K")]
    [InlineData(1000000, "1M")]
    [InlineData(2000000, "2M")]
    [InlineData(1500000, "1.5M")]
    [InlineData(8000, "8K")]
    [InlineData(500, "500")]
    [InlineData(0, "")]
    public void FormatContextWindow_FormatsCompactly(int contextWindow, string expected) =>
        Assert.Equal(expected, ChatModelLabels.FormatContextWindow(contextWindow));

    // ── Meta segment ─────────────────────────────────────────────────────

    [Fact]
    public void BuildMetaSegment_CombinesProviderAndContext()
    {
        var c = new ChatModelChoice("x", "X", Provider: "OpenAI", ContextWindow: 272000);
        Assert.Equal("OpenAI · 272K", ChatModelLabels.BuildMetaSegment(c));
    }

    [Fact]
    public void BuildMetaSegment_ProviderOnly()
    {
        var c = new ChatModelChoice("x", "X", Provider: "OpenAI");
        Assert.Equal("OpenAI", ChatModelLabels.BuildMetaSegment(c));
    }

    [Fact]
    public void BuildMetaSegment_ContextOnly()
    {
        var c = new ChatModelChoice("x", "X", ContextWindow: 200000);
        Assert.Equal("200K", ChatModelLabels.BuildMetaSegment(c));
    }

    [Fact]
    public void BuildMetaSegment_NeitherKnown_ReturnsEmpty() =>
        Assert.Equal("", ChatModelLabels.BuildMetaSegment(new ChatModelChoice("x", "X")));

    // ── State markers ────────────────────────────────────────────────────

    [Fact]
    public void BuildStateMarker_Unavailable_TakesPrecedence()
    {
        var c = new ChatModelChoice("x", "X", IsAvailable: false, RequiresAuth: true, IsDefault: true);
        Assert.Equal("unavailable", ChatModelLabels.BuildStateMarker(c));
    }

    [Fact]
    public void BuildStateMarker_AuthNeeded_BeforeDefault()
    {
        var c = new ChatModelChoice("x", "X", RequiresAuth: true, IsDefault: true);
        Assert.Equal("auth needed", ChatModelLabels.BuildStateMarker(c));
    }

    [Fact]
    public void BuildStateMarker_Default()
    {
        var c = new ChatModelChoice("x", "X", IsDefault: true);
        Assert.Equal("default", ChatModelLabels.BuildStateMarker(c));
    }

    [Fact]
    public void BuildStateMarker_MissingConfiguredFlag_IsNotAuthNeeded()
    {
        // Gateway's "configured" view often omits the flag; absence must not be
        // mistaken for an auth requirement.
        var c = new ChatModelChoice("x", "X", IsConfigured: false);
        Assert.Equal("", ChatModelLabels.BuildStateMarker(c));
    }

    // ── Full menu label ──────────────────────────────────────────────────

    [Fact]
    public void BuildMenuLabel_Full()
    {
        var c = new ChatModelChoice("claude-opus-4.8", "Claude Opus 4.8", Provider: "Anthropic", ContextWindow: 200000, IsDefault: true);
        Assert.Equal("Claude Opus 4.8 · Anthropic · 200K · default", ChatModelLabels.BuildMenuLabel(c));
    }

    [Fact]
    public void BuildMenuLabel_AuthNeeded()
    {
        var c = new ChatModelChoice("gemini-3.1-pro", "Gemini 3.1 Pro", Provider: "Google", ContextWindow: 1000000, RequiresAuth: true);
        Assert.Equal("Gemini 3.1 Pro · Google · 1M · auth needed", ChatModelLabels.BuildMenuLabel(c));
    }

    [Fact]
    public void BuildMenuLabel_BareModel()
    {
        var c = new ChatModelChoice("custom-id", "custom-id");
        Assert.Equal("custom-id", ChatModelLabels.BuildMenuLabel(c));
    }

    // ── Default (clear-to-default) entry label ───────────────────────────

    [Fact]
    public void BuildDefaultEntryLabel_NamesDefaultModelWhenKnown()
    {
        var def = new ChatModelChoice("claude-opus-4.8", "Claude Opus 4.8", IsDefault: true);
        Assert.Equal("Default (Claude Opus 4.8)", ChatModelLabels.BuildDefaultEntryLabel(def));
    }

    [Fact]
    public void BuildDefaultEntryLabel_PlainWhenDefaultUnknown() =>
        Assert.Equal("Default", ChatModelLabels.BuildDefaultEntryLabel(null));
}
