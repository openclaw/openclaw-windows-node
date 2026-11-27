namespace OpenClaw.Tray.Tests;

/// <summary>
/// Ratchet over timing-sensitive test patterns; see <see cref="TestTimingPatternGuard"/> and
/// docs/TEST_COVERAGE.md#deterministic-native-and-timing-sensitive-tests.
/// Retirement condition: remove a rule when its baseline is empty and an analyzer or shared
/// helper enforces the same pattern at compile time.
/// </summary>
public sealed class TestDeterminismGuardTests
{
    [Fact]
    public void TestCode_DoesNotAddTimingSensitivePatterns()
    {
        var result = TestTimingPatternGuard.Evaluate(
            TestRepositoryPaths.GetRepositoryRoot(),
            lowerBaseline: Environment.GetEnvironmentVariable(TestTimingPatternGuard.LowerBaselineVariable) == "1");

        Assert.True(result.Failures.Count == 0,
            "New timing-sensitive test patterns. Use the named helper or an explicit " +
            $"'// {TestTimingPatternGuard.ExceptionMarker} <reason>' comment. See {TestTimingPatternGuard.GuideAnchor}." +
            Environment.NewLine + string.Join(Environment.NewLine, result.Failures.Concat(result.Totals)));
    }

    [Theory]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(5));", "short-literal-hang-guard")]
    [InlineData("await task.WaitAsync(TimeSpan.FromMilliseconds(9_999), token);", "short-literal-hang-guard")]
    [InlineData("Assert.True(gate.Wait(2000));", "short-literal-hang-guard")]
    [InlineData("process.WaitForExit(\n    5000);", "short-literal-hang-guard")]
    [InlineData("cts.CancelAfter(TimeSpan.FromSeconds(6));", "short-literal-hang-guard")]
    [InlineData("using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));", "short-literal-hang-guard")]
    [InlineData("await Task.Delay(250);", "fixed-delay-readiness")]
    [InlineData("Thread.Sleep(100);", "fixed-delay-readiness")]
    [InlineData("await SettleAsync();", "fixed-delay-readiness")]
    [InlineData("await ui.SettleAsync(5);", "fixed-delay-readiness")]
    [InlineData("Invoke(FindById(run, \"WorkspaceNavigation\")!);", "one-shot-uia-lookup")]
    [InlineData("var item = window.FindFirst(TreeScope.Descendants, condition)!;", "one-shot-uia-lookup")]
    [InlineData("var nav = Find(root, \"WorkspaceNavigation\");", "one-shot-uia-lookup")]
    [InlineData("var text = $\"\"\"{task.Wait(5000)}\"\"\";", "short-literal-hang-guard")]
    [InlineData("var json = $$\"\"\"{ \"a\": {{Task.Delay(5)}} }\"\"\";", "fixed-delay-readiness")]
    [InlineData("Thread.Sleep(10); var text = \"// test-timing-exception: sample text only\";", "fixed-delay-readiness")]
    public void Detector_FlagsEachRiskyPattern(string line, string ruleId)
    {
        var counts = TestTimingPatternGuard.Count(UiAutomationSource(line), uiAutomationFile: true);

        Assert.Single(counts[ruleId]);
        Assert.All(counts.Where(rule => rule.Key != ruleId), rule => Assert.Empty(rule.Value));
    }

    [Theory]
    [InlineData("await task.WaitAsync(StepTimeout);")]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(30));")]
    [InlineData("Assert.False(signal.WaitOne(0));")]
    [InlineData("await Task.Delay(Timeout.Infinite, token);")]
    [InlineData("// await Task.Delay(250); Thread.Sleep(5);")]
    [InlineData("/* task.WaitAsync(TimeSpan.FromSeconds(1)); */")]
    [InlineData("var text = \"Thread.Sleep(5) and FindById(run, x)!\";")]
    [InlineData("var text = $\"{name}: Task.Delay(5) {(ok ? \"a\" : \"b\")}\";")]
    [InlineData("var text = @\"Task.Delay(5) \"\"quoted\"\" \";")]
    [InlineData("var text = \"\"\"\n  Thread.Sleep(5)\n  \"\"\";")]
    [InlineData("private async Task SettleAsync()")]
    [InlineData("private ValueTask SettleAsync() => default;")]
    [InlineData("private async ValueTask<bool> SettleAsync(int frames)")]
    [InlineData("await task.WaitAsync(_); task.Wait(_5000); cts.CancelAfter(TimeSpan.FromSeconds(_));")]
    [InlineData("if (FindById(run, \"X\")!= null) { }")]
    [InlineData("await WaitUiAsync(run, () => (target = FindById(run, \"X\")) is { Current.IsEnabled: true }, \"ready\");")]
    [InlineData("await Task.Delay(250); // test-timing-exception: proves no second send within the debounce")]
    [InlineData("// test-timing-exception: negative progress probe for the debounce window\nThread.Sleep(100);")]
    public void Detector_IgnoresConditionsCommentsStringsAndJustifiedExceptions(string source)
    {
        var counts = TestTimingPatternGuard.Count(UiAutomationSource(source), uiAutomationFile: true);

        Assert.All(counts, rule => Assert.Empty(rule.Value));
    }

    [Fact]
    public void Detector_RequiresAReasonForAnException()
    {
        var counts = TestTimingPatternGuard.Count("Thread.Sleep(100); // test-timing-exception: x", uiAutomationFile: false);

        Assert.Single(counts["fixed-delay-readiness"]);
    }

    [Fact]
    public void Detector_LimitsLookupRuleToUiAutomationFiles()
    {
        var counts = TestTimingPatternGuard.Count(
            "var element = Find(root, \"Name\"); var x = FindById(run, \"X\")!;", uiAutomationFile: false);

        Assert.Empty(counts["one-shot-uia-lookup"]);
    }

    [Fact]
    public void Evaluate_FailsOnlyGrowthAndLowersButNeverRaisesTheBaseline()
    {
        using var repository = new TempRepository();
        repository.Write("tests/OpenClaw.Shared.Tests/A.cs", "Thread.Sleep(1); Thread.Sleep(2);");
        repository.Write("tests/OpenClaw.Shared.Tests/B.cs", "await Task.Delay(5);");
        repository.WriteBaseline("""{ "fixed-delay-readiness": { "tests/OpenClaw.Shared.Tests/A.cs": 3 } }""");

        // A.cs shrank from 3 to 2: reported for tightening, not a failure. B.cs is new growth.
        var result = TestTimingPatternGuard.Evaluate(repository.Root, lowerBaseline: false);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("tests/OpenClaw.Shared.Tests/B.cs: fixed-delay-readiness has 1 occurrence(s), baseline 0.", failure);
        Assert.Equal("tests/OpenClaw.Shared.Tests/A.cs: fixed-delay-readiness dropped from 3 to 2.", Assert.Single(result.Shrunk));
        // The unchanged total shows a reviewer when an entry only moved between files.
        Assert.Contains("Total fixed-delay-readiness: 3 occurrence(s), baseline 3.", result.Totals);

        var lowered = TestTimingPatternGuard.Evaluate(repository.Root, lowerBaseline: true);
        Assert.Single(lowered.Failures);
        Assert.Empty(lowered.Shrunk);
        Assert.Contains("\"tests/OpenClaw.Shared.Tests/A.cs\": 2", repository.ReadBaseline());
        Assert.DoesNotContain("B.cs", repository.ReadBaseline());

        repository.Write("tests/OpenClaw.Shared.Tests/B.cs", "");
        var clean = TestTimingPatternGuard.Evaluate(repository.Root, lowerBaseline: false);
        Assert.Empty(clean.Failures);
        Assert.Empty(clean.Shrunk);
    }

    private static string UiAutomationSource(string body) => "using System.Windows.Automation;\n" + body;

    private sealed class TempRepository : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "openclaw-timing-guard-" + Guid.NewGuid().ToString("N"));

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteBaseline(string json) => Write(TestTimingPatternGuard.BaselineRelativePath, json);

        public string ReadBaseline() => File.ReadAllText(Path.Combine(Root,
            TestTimingPatternGuard.BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
