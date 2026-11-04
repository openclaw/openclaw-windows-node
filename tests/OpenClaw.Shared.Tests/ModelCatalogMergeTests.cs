using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class ModelCatalogMergeTests
{
    [Fact]
    public void MergeModelCatalog_KeepsConfiguredFirstAndRetainsUnavailableCatalogModels()
    {
        var configured = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo
                {
                    Id = "openai/gpt-5.6-sol",
                    Name = "GPT-5.6 Sol",
                    IsAvailable = true
                }
            }
        };
        var catalog = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo
                {
                    Id = "gpt-5.6-sol",
                    Name = "GPT-5.6 Sol",
                    Provider = "openai",
                    ContextWindow = 1_050_000,
                    IsDefault = true,
                    IsAvailable = true
                },
                new ModelInfo
                {
                    Id = "gpt-5.5",
                    Name = "GPT-5.5",
                    Provider = "openai",
                    IsAvailable = true
                },
                new ModelInfo
                {
                    Id = "claude-opus-4-8",
                    Name = "Claude Opus 4.8",
                    Provider = "anthropic",
                    IsAvailable = false
                }
            }
        };

        var merged = OpenClawGatewayClient.MergeModelCatalog(configured, catalog);

        Assert.Collection(
            merged.Models,
            model =>
            {
                Assert.Equal("openai/gpt-5.6-sol", model.Id);
                Assert.True(model.IsConfigured);
                Assert.True(model.HasConfiguredFlag);
                Assert.True(model.IsAvailable);
                Assert.Equal(1_050_000, model.ContextWindow);
                Assert.True(model.IsDefault);
            },
            model =>
            {
                Assert.Equal("gpt-5.5", model.Id);
                Assert.Equal("openai", model.Provider);
                Assert.False(model.IsConfigured);
                Assert.True(model.HasConfiguredFlag);
                Assert.True(model.IsAvailable);
            },
            model =>
            {
                Assert.Equal("claude-opus-4-8", model.Id);
                Assert.Equal("anthropic", model.Provider);
                Assert.False(model.IsConfigured);
                Assert.True(model.HasConfiguredFlag);
                Assert.False(model.IsAvailable);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergeModelCatalog_PreservesExplicitConfiguredReadinessWithoutMutatingSnapshots(bool configuredFlag)
    {
        var source = new ModelInfo
        {
            Id = "restricted",
            Provider = "fixture",
            HasConfiguredFlag = true,
            IsConfigured = configuredFlag,
            IsAvailable = false,
            ManualSelectionAllowed = false,
        };
        var configured = new ModelsListInfo { Models = { source } };
        var catalogSource = new ModelInfo
        {
            Id = "restricted",
            Provider = "fixture",
            ContextWindow = 64_000,
            IsAvailable = true,
            ManualSelectionAllowed = true,
        };
        var catalog = new ModelsListInfo { Models = { catalogSource } };

        var merged = OpenClawGatewayClient.MergeModelCatalog(configured, catalog);

        var model = Assert.Single(merged.Models);
        Assert.NotSame(source, model);
        Assert.True(model.HasConfiguredFlag);
        Assert.Equal(configuredFlag, model.IsConfigured);
        Assert.False(model.IsAvailable);
        Assert.False(model.ManualSelectionAllowed);
        Assert.Equal(64_000, model.ContextWindow);
        Assert.Equal(configuredFlag, source.IsConfigured);
        Assert.Null(source.ContextWindow);
        Assert.True(catalogSource.IsAvailable);
        Assert.True(catalogSource.ManualSelectionAllowed);
    }

    [Fact]
    public void MergeModelCatalog_DedupesProviderQualifiedIdentities()
    {
        var configured = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo { Id = "openai/gpt-5.4" }
            }
        };
        var catalog = new ModelsListInfo
        {
            Models =
            {
                new ModelInfo
                {
                    Id = "gpt-5.4",
                    Provider = "openai",
                    ContextWindow = 1_000_000,
                    IsAvailable = true
                }
            }
        };

        var merged = OpenClawGatewayClient.MergeModelCatalog(configured, catalog);

        var model = Assert.Single(merged.Models);
        Assert.Equal("openai/gpt-5.4", model.Id);
        Assert.Equal(1_000_000, model.ContextWindow);
    }
}