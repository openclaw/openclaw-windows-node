using System;
using System.Diagnostics;
using System.Linq;
using OpenClaw.Shared.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MarkdownParserTimingCollection
{
    public const string Name = "Markdown parser timing";
}

// #5 fuzz probe: the node parses untrusted gateway chat markdown via ChatMarkdownAstBuilder.
// Pathological (not merely large) inputs must not crash or blow up super-linearly (CPU DoS).
[Collection(MarkdownParserTimingCollection.Name)]
public class MarkdownParserFuzzTests
{
    private readonly ITestOutputHelper _out;
    public MarkdownParserFuzzTests(ITestOutputHelper o) => _out = o;

    public static TheoryData<string, string> Pathological()
    {
        const int n = 100_000;
        return new TheoryData<string, string>
        {
            { "many-asterisks", new string('*', n) },
            { "emphasis-backtrack", string.Concat(Enumerable.Repeat("*a", n / 2)) },   // *a*a*a...
            { "open-brackets", new string('[', n) },
            { "nested-link", string.Concat(Enumerable.Repeat("[", n / 2)) + string.Concat(Enumerable.Repeat("]", n / 2)) },
            { "image-bang-brackets", string.Concat(Enumerable.Repeat("![", n / 2)) },
            { "deep-blockquote", string.Concat(Enumerable.Repeat("> ", n / 2)) },
            { "deep-list", string.Concat(Enumerable.Repeat("  - x\n", n / 6)) },
            { "backticks", new string('`', n) },
            { "underscores", new string('_', n) },
            { "mixed-markers", string.Concat(Enumerable.Repeat("*_`[~", n / 5)) },
            { "table-pipes", "|" + string.Concat(Enumerable.Repeat("a|", n / 2)) },
        };
    }

    [Fact]
    public void Build_ManyListItems_ScalingCurve()
    {
        using var process = Process.GetCurrentProcess();
        string ListMd(int items) => string.Concat(Enumerable.Repeat("  - x\n", items));
        long Time(int items)
        {
            var md = ListMd(items);
            new ChatMarkdownAstBuilder().Build(md); // warm
            var cpuBefore = process.TotalProcessorTime;
            var gcPauseBefore = GC.GetTotalPauseDuration();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            var document = new ChatMarkdownAstBuilder().Build(md);
            sw.Stop();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var gcPause = GC.GetTotalPauseDuration() - gcPauseBefore;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            _out.WriteLine(
                $"{items} items: wall={sw.Elapsed.TotalMilliseconds:F1}ms cpu={cpu.TotalMilliseconds:F1}ms "
                + $"gcPause={gcPause.TotalMilliseconds:F1}ms allocated={allocated} bytes");
            var list = Assert.IsType<MdList>(Assert.Single(document.Blocks));
            Assert.Equal(items, list.Items.Count);
            return sw.ElapsedMilliseconds;
        }
        const int largestItemCount = 30_000;
        Assert.True(ListMd(largestItemCount).Length < ChatMarkdownAstBuilder.MaxInputBytes);

        long t1 = Time(10_000), t2 = Time(20_000), t3 = Time(largestItemCount);
        _out.WriteLine($"list items 10k={t1}ms 20k={t2}ms 30k={t3}ms");
        // Regression guard: a 30k-item flat list parsed in seconds with the O(n^2) bug and tens of milliseconds after
        // the O(1) line-start fix. Assert it stays well under 500ms so the quadratic can't silently return.
        Assert.True(t3 < 500, $"30k flat list items must parse near-linearly (<500ms). Curve: 10k={t1} 20k={t2} 30k={t3}ms");
    }

    [Theory]
    [MemberData(nameof(Pathological))]
    public void Build_PathologicalInput_IsBoundedAndDoesNotCrash(string label, string markdown)
    {
        var sw = Stopwatch.StartNew();
        var doc = new ChatMarkdownAstBuilder().Build(markdown);   // must not throw
        sw.Stop();
        _out.WriteLine($"{label}: {markdown.Length} chars parsed in {sw.ElapsedMilliseconds} ms");

        Assert.NotNull(doc);
        // A 100 KB message parsing must be near-linear; >3s indicates super-linear (quadratic) blowup — DoS.
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{label} took {sw.ElapsedMilliseconds} ms (super-linear blowup?)");
    }
}
