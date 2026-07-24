using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class ModelCatalogMergeTests
{
    [Fact]
    public void MergeModelCatalog_KeepsConfiguredFirstAndAddsAvailableCatalogModels()
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
            });
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