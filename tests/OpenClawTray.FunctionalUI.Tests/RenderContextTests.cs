using OpenClawTray.FunctionalUI.Core;

namespace OpenClawTray.FunctionalUI.Tests;

public sealed class RenderContextTests
{
    [Fact]
    public void UseEffect_WithExplicitEmptyDependencies_RunsExactlyOnceOnFirstMount()
    {
        var ctx = new RenderContext();
        var ranCount = 0;

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, Array.Empty<object>()));
        Assert.Equal(1, ranCount);

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, Array.Empty<object>()));
        Assert.Equal(1, ranCount);
    }

    [Fact]
    public void UseEffect_WithOmittedDependencies_RunsExactlyOnceOnFirstMount()
    {
        var ctx = new RenderContext();
        var ranCount = 0;

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }));
        Assert.Equal(1, ranCount);

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }));
        Assert.Equal(1, ranCount);
    }

    [Fact]
    public void UseEffect_WithChangingDependencies_RunsOnEveryDependencyChange()
    {
        var ctx = new RenderContext();
        var ranCount = 0;

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, new object[] { 1 }));
        Assert.Equal(1, ranCount);

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, new object[] { 2 }));
        Assert.Equal(2, ranCount);

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, new object[] { 2 }));
        Assert.Equal(2, ranCount);
    }

    [Fact]
    public void UseEffect_WithStableDependencies_RunsOnceThenSkips()
    {
        var ctx = new RenderContext();
        var ranCount = 0;

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, new object[] { "x" }));
        Assert.Equal(1, ranCount);

        Render(ctx, () => ctx.UseEffect(() => { ranCount++; }, new object[] { "x" }));
        Assert.Equal(1, ranCount);
    }

    private static void Render(RenderContext ctx, Action render)
    {
        var effects = new List<Action>();
        ctx.BeginRender(requestRender: () => { }, afterRender: effects.Add);
        render();

        foreach (var effect in effects)
            effect();
    }
}
