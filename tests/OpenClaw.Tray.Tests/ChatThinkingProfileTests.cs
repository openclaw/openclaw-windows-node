using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class ChatThinkingProfileTests
{
    private static ThinkingContext Context(string? provider = "p", string? model = "m", string? runtime = "r",
        ThinkingProfile? profile = null) => new(new(provider, model, runtime), profile);
    private static readonly ThinkingProfile Off = new([new("off", "Off")], "off");
    private static readonly ThinkingProfile Mandatory = new([new("high", "Required"), new("future/Exact", "Provider choice")]);
    private static ChatModelChoice Choice(ThinkingContext context, bool? reasoning = null) =>
        new(context.Identity.Model!, "Model", context.Identity.Provider, Reasoning: reasoning, ThinkingContext: context);
    private static ChatThread Thread(ThinkingContext? context = null, ThinkingContext? defaults = null) =>
        new() { Id = "s", Title = "Session", ThinkingContext = context, ThinkingDefaults = defaults };

    [Fact]
    public void Priority_FirstCandidateWithAnyMetadataOwnsEvenEmptyOrDefaultOnly()
    {
        var catalog = new[] { Choice(Context(profile: Off)) };
        Assert.Same(Mandatory, ChatThinkingProfile.Resolve(Thread(Context(profile: Mandatory), Context(profile: Off)), catalog));
        Assert.Same(Mandatory, ChatThinkingProfile.Resolve(Thread(Context(), Context(profile: Mandatory)), catalog));
        Assert.Same(Off, ChatThinkingProfile.Resolve(Thread(Context()), catalog));
        var empty = new ThinkingProfile([]);
        Assert.Same(empty, ChatThinkingProfile.Resolve(Thread(Context(profile: empty), Context(profile: Mandatory)), catalog));
        var defaultOnly = new ThinkingProfile(Default: "future");
        Assert.Same(defaultOnly, ChatThinkingProfile.Resolve(Thread(Context(profile: defaultOnly)), catalog));
        Assert.Same(empty, ChatThinkingProfile.Resolve(Thread(Context(), Context(profile: empty)), catalog));
    }

    [Fact]
    public void Catalog_ExactProviderModelAndCompatibleRuntimeOnly()
    {
        var catalog = new[]
        {
            Choice(Context(provider: "other", profile: Mandatory)),
            Choice(Context(runtime: "other", profile: Mandatory)),
            Choice(Context(profile: Off)),
        };
        Assert.Same(Off, ChatThinkingProfile.Resolve(Thread(Context()), catalog));
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context(provider: "P")), catalog));
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context(model: "M")), catalog));
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context(runtime: "missing")), catalog));
        Assert.Same(Mandatory, ChatThinkingProfile.Resolve(Thread(Context(runtime: null)), catalog));
    }

    [Theory]
    [InlineData(null, "m")]
    [InlineData("p", null)]
    public void PartialIdentity_CannotBorrowItsMissingHalfForCatalog(string? provider, string? model)
    {
        var defaultsWithoutProfile = Context();
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context(provider, model), defaultsWithoutProfile),
            [Choice(Context(profile: Off))]));
        Assert.Same(Off, ChatThinkingProfile.Resolve(Thread(Context(null, null), defaultsWithoutProfile),
            [Choice(Context(profile: Off))]));
    }

    [Theory]
    [InlineData("other", "m", "r")]
    [InlineData("p", "other", "r")]
    [InlineData("p", "m", "other")]
    public void IncompatibleDefaults_DoNotSupplyProfile(string provider, string model, string runtime) =>
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context(provider, model, runtime), Context(profile: Off)), []));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void ReasoningBoolean_NeitherInventsNorVetoesAdvertisedChoices(bool? reasoning)
    {
        Assert.Same(Mandatory, ChatThinkingProfile.Resolve(Thread(Context()), [Choice(Context(profile: Mandatory), reasoning)]));
        Assert.Null(ChatThinkingProfile.Resolve(Thread(Context()), [Choice(Context(), reasoning)]));
        Assert.Same(Off, ChatThinkingProfile.Resolve(Thread(Context()), [Choice(Context(profile: Off), reasoning)]));
    }

    [Fact]
    public void ModelChoiceProjection_PreservesImmutableProfileAndUnknownStrings()
    {
        var context = Context(profile: Mandatory);
        var choice = Assert.Single(ChatModelChoice.FromModelsList(new ModelsListInfo
        {
            Models = [new ModelInfo { Id = "m", Provider = "p", Reasoning = false, ThinkingContext = context }],
        }));
        Assert.Same(context, choice.ThinkingContext);
        Assert.False(choice.Reasoning);
        Assert.Equal("future/Exact", choice.ThinkingContext!.Profile!.Levels!.Value[1].Id);
    }
}
