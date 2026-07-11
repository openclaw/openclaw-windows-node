using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class WizardDefaultAnswerMatrixTests
{
    private static readonly string[] ProviderOptions =
    [
        "skip",
        "github-copilot",
        "lmstudio",
        "lm-studio",
        "openai",
        "azure-openai",
    ];

    private static readonly string[] ChannelOptions =
    [
        "__skip__",
        "teams",
        "telegram",
        "discord",
        "whatsapp",
    ];

    [Fact]
    public void DefaultConfig_CoversProviderChannelAndSearchWizardSteps()
    {
        var answers = LoadDefaultWizardAnswers();

        AssertConfiguredSelect(answers, "model-auth-provider", ProviderOptions);
        AssertConfiguredSelect(answers, "default-model", ["__keep__", "gpt-5.5", "gpt-5.4", "claude-sonnet-4.6"]);
        AssertConfiguredMultiselect(answers, "select-channel-quickstart", ChannelOptions);
        AssertConfiguredSelect(answers, "search-provider", ["__skip__", "tavily", "brave", "bing"]);
        AssertConfiguredSelect(answers, "how-do-you-want-to-hatch-your-agent", ["tui", "web", "later"]);
        AssertConfiguredSelect(answers, "how-do-you-want-to-hatch-your-bot", ["tui", "web", "later"]);

        Assert.True(answers.TryGetValue("configure-skills-now-recommended", out var configureSkills));
        Assert.False((bool)WizardAnswerBuilder.BuildWireValue("confirm", configureSkills, []));
    }

    [Fact]
    public void DesktopWizard_HatchChoiceDefersInsteadOfLaunchingTerminalUi()
    {
        var options = Options(["tui", "web", "later"]);

        Assert.Equal("later", SetupWizardRunner.InferOptionAnswer(options, "tui"));
        Assert.Equal("later", WizardSelection.PreferredDesktopSelectAnswer(options, "tui"));
        Assert.Equal("later", WizardSelection.DesktopAutoSelectAnswer(options));
    }

    [Fact]
    public void DesktopWizard_ServiceRuntimeChoiceSkipsAlreadyInstalledPrompt()
    {
        var options = Options(["restart", "reinstall", "skip"]);

        Assert.Equal(
            "skip",
            WizardSelection.PreferredDesktopSelectAnswer(
                options,
                initialValue: "restart",
                title: "Choose an option",
                message: "Gateway service already installed",
                stepId: "gateway-service-runtime"));
        Assert.Equal(
            "skip",
            WizardSelection.DesktopAutoSelectAnswer(
                options,
                title: "Choose an option",
                message: "Gateway service already installed",
                stepId: "gateway-service-runtime"));
    }

    [Theory]
    [MemberData(nameof(RepresentativeProviderChannelMatrix))]
    public void WizardAnswerBuilder_ValidatesRepresentativeProviderChannelCombinations(
        string provider,
        string channels)
    {
        var providerOptions = Options(ProviderOptions);
        var channelOptions = Options(ChannelOptions);

        Assert.True(WizardAnswerBuilder.TryFindOption(providerOptions, provider, out _));
        Assert.True(WizardAnswerBuilder.TryResolveOptions(channelOptions, channels, out var selectedChannels));
        Assert.NotEmpty(selectedChannels);

        var providerWire = WizardAnswerBuilder.BuildWireValue("select", provider, providerOptions);
        var channelsWire = WizardAnswerBuilder.BuildWireValue("multiselect", channels, channelOptions);

        Assert.Equal(JsonSerializer.Serialize(provider), JsonSerializer.Serialize(providerWire));
        Assert.StartsWith("[", JsonSerializer.Serialize(channelsWire), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> RepresentativeProviderChannelMatrix()
    {
        foreach (var provider in ProviderOptions)
        {
            yield return [provider, "__skip__"];
            yield return [provider, "teams"];
            yield return [provider, "telegram"];
            yield return [provider, "discord,whatsapp"];
        }
    }

    private static Dictionary<string, string> LoadDefaultWizardAnswers()
    {
        var configPath = Path.Combine(RepositoryRoot(), "src", "OpenClaw.SetupEngine", "default-config.json");
        var config = SetupConfig.LoadFromFile(configPath);
        return config.WizardAnswers ?? new Dictionary<string, string>();
    }

    private static void AssertConfiguredSelect(
        Dictionary<string, string> answers,
        string key,
        IReadOnlyList<string> values)
    {
        Assert.True(answers.TryGetValue(key, out var answer), $"Missing WizardAnswers['{key}']");
        Assert.True(WizardAnswerBuilder.TryFindOption(Options(values), answer, out _),
            $"WizardAnswers['{key}'] value '{answer}' was not present in representative options.");
    }

    private static void AssertConfiguredMultiselect(
        Dictionary<string, string> answers,
        string key,
        IReadOnlyList<string> values)
    {
        Assert.True(answers.TryGetValue(key, out var answer), $"Missing WizardAnswers['{key}']");
        Assert.True(WizardAnswerBuilder.TryResolveOptions(Options(values), answer, out var selected),
            $"WizardAnswers['{key}'] value '{answer}' could not be resolved from representative options.");
        Assert.NotEmpty(selected);
    }

    private static IReadOnlyList<WizardOptionValue> Options(IReadOnlyList<string> values)
    {
        var json = JsonSerializer.Serialize(new
        {
            options = values.Select(value => new { label = value, value })
        });
        using var document = JsonDocument.Parse(json);
        return WizardAnswerBuilder.ReadOptions(document.RootElement);
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "src", "OpenClaw.SetupEngine", "default-config.json")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root for default-config.json.");
    }
}
