using OpenClaw.Shared;

namespace OpenClaw.TestSupport;

/// <summary>
/// Fluent builder for <see cref="SettingsData"/> test data. Records the
/// requested overrides and replays them onto a fresh <see cref="SettingsData"/>
/// (starting from the production defaults) on every <see cref="Build"/> call, so
/// each built instance is independent — mutating the builder after a build never
/// changes an already-built result, and no mutable list state is shared between
/// builds. See <c>docs/ARCHITECTURE.md</c> (ledger id <c>test-settings-builder</c>).
/// </summary>
public sealed class SettingsDataBuilder
{
    private readonly List<Action<SettingsData>> _mutations = new();

    /// <summary>Applies an arbitrary mutation to the settings under construction.</summary>
    public SettingsDataBuilder With(Action<SettingsData> mutate)
    {
        _mutations.Add(mutate);
        return this;
    }

    public SettingsDataBuilder WithGatewayUrl(string? url) => With(d => d.GatewayUrl = url);
    public SettingsDataBuilder WithNodeMode(bool enabled) => With(d => d.EnableNodeMode = enabled);
    public SettingsDataBuilder WithAutoStart(bool enabled) => With(d => d.AutoStart = enabled);

    public SettingsData Build()
    {
        var data = new SettingsData();
        foreach (var mutate in _mutations)
        {
            mutate(data);
        }
        return data;
    }
}
