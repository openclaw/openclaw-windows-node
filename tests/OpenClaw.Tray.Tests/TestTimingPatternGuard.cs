// Ratchet for the timing patterns behind recent CI flakes in repository test code.
// Each rule counts occurrences per test file. TestTimingPatternBaseline.json records the counts
// when the guard was introduced; growth fails, and shrunk counts are reported for tightening.
// Two entry points share this file:
//   - TestDeterminismGuardTests (Tray.Tests, part of the required local closeout), and
//   - scripts/validate-test-timing-patterns.ps1 (Add-Type in always-on CI fast validation, so a
//     UI-test-only or Shared-test-only pull request cannot bypass it).
// Usage:
//   var result = TestTimingPatternGuard.Evaluate(repositoryRoot, lowerBaseline: false);
//   // result.Failures must be empty; result.Shrunk lists entries that can be lowered.
// Keep this file free of test-framework dependencies and C# features newer than C# 11 so
// PowerShell's Add-Type can compile it.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

/// <summary>Detects literal short hang guards, fixed delays, and one-shot UIA lookups in test code.</summary>
public static class TestTimingPatternGuard
{
    public const string BaselineRelativePath = "tests/OpenClaw.Tray.Tests/TestTimingPatternBaseline.json";
    public const string LowerBaselineVariable = "OPENCLAW_UPDATE_TEST_TIMING_BASELINE";
    public const string ExceptionMarker = "test-timing-exception:";
    public const string GuideAnchor = "docs/TEST_COVERAGE.md#deterministic-native-and-timing-sensitive-tests";
    private const int MinimumExceptionReasonLength = 12;
    // Literal hang guards below this bound starved on loaded hosted runners (2-6 s step waits).
    private const double MinimumLiteralHangGuardSeconds = 10;
    private const string DurationPattern =
        @"(?:TimeSpan\.From(?<unit>Seconds|Milliseconds)\(\s*(?<value>\d[\d_]*(?:\.\d+)?)\s*\)|(?<ms>\d[\d_]*)\s*[,)])";

    public sealed record TimingRule(
        string Id, bool AllTestCode, string Remedy, IReadOnlyList<Regex> Patterns, Func<Match, bool>? Accept = null);

    public static readonly IReadOnlyList<TimingRule> Rules = new[]
    {
        new TimingRule(
            "short-literal-hang-guard",
            AllTestCode: true,
            Remedy: "Use a named, environment-overridable timeout with headroom, such as StepTimeout in " +
                "OpenClawGatewayClientTests or McpFileTransportTests, or TestSupport.RenderedConditionTimeout. " +
                "A literal wait under 10 s starves on loaded runners.",
            Patterns: new[]
            {
                new Regex(@"\.(?:WaitAsync|Wait|WaitOne|WaitForExit|CancelAfter)\(\s*" + DurationPattern, RegexOptions.CultureInvariant),
                new Regex(@"\bnew\s+CancellationTokenSource\(\s*" + DurationPattern, RegexOptions.CultureInvariant),
            },
            Accept: IsShortLiteralDuration),
        new TimingRule(
            "fixed-delay-readiness",
            AllTestCode: true,
            Remedy: "Wait for the observable condition instead: TestSupport.WaitForRenderedConditionAsync " +
                "(in-process WinUI), WaitUiAsync (Gateway fixture UI Automation), or a TaskCompletionSource " +
                "completed by the owner. Fixed delays and settle counts are not readiness.",
            Patterns: new[]
            {
                new Regex(@"\bThread\.Sleep\(", RegexOptions.CultureInvariant),
                new Regex(@"\bTask\.Delay\((?!\s*(?:System\.Threading\.)?Timeout\.Infinite)", RegexOptions.CultureInvariant),
                // Exclude helper declarations such as "Task SettleAsync(" or "ValueTask<bool> SettleAsync(".
                new Regex(@"(?<![\w])(?<!\b(?:Value)?Task(?:<[^<>;]*>)?\s+)SettleAsync\(", RegexOptions.CultureInvariant),
            }),
        new TimingRule(
            "one-shot-uia-lookup",
            AllTestCode: false,
            Remedy: "Acquire the element inside the wait and use that capture, for example " +
                "AutomationElement? x = null; await WaitUiAsync(run, () => (x = FindById(run, id)) is " +
                "{ Current.IsEnabled: true }, ...), or InvokeWhenEnabledAsync. A null-forgiving or throwing " +
                "lookup asserts presence without waiting.",
            Patterns: new[]
            {
                new Regex(@"\bFind\w*\([^;{}]*?\)!(?!=)", RegexOptions.CultureInvariant),
                new Regex(@"(?<![\w.])Find\(\s*\w+\s*,", RegexOptions.CultureInvariant),
            }),
    };

    /// <summary>
    /// Compares repository test code with the checked-in baseline. Growth is a failure. A shrunk
    /// count is reported so the baseline can be tightened, but does not fail: pull requests that
    /// remove patterns stay green. With <paramref name="lowerBaseline"/>, shrunk counts are written
    /// back; counts are never raised. Totals give reviewers context for a moved baseline entry.
    /// </summary>
    public static EvaluationResult Evaluate(string repositoryRoot, bool lowerBaseline)
    {
        var baselinePath = Path.Combine(repositoryRoot, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var baseline = ReadBaseline(baselinePath);
        var actual = ScanRepository(repositoryRoot);
        var failures = new List<string>();

        foreach (var rule in Rules)
        {
            var expected = baseline.TryGetValue(rule.Id, out var entries) ? entries : new SortedDictionary<string, int>();
            foreach (var pair in actual[rule.Id])
            {
                expected.TryGetValue(pair.Key, out var allowed);
                if (pair.Value.Count > allowed)
                    failures.Add($"{pair.Key}: {rule.Id} has {pair.Value.Count} occurrence(s), baseline {allowed}. " +
                        $"Lines: {string.Join(", ", pair.Value)}. {rule.Remedy}");
            }
        }

        var shrunk = new List<string>();
        foreach (var rule in Rules)
        {
            if (!baseline.TryGetValue(rule.Id, out var entries)) continue;
            foreach (var pair in entries)
            {
                var count = actual[rule.Id].TryGetValue(pair.Key, out var lines) ? lines.Count : 0;
                if (count < pair.Value)
                    shrunk.Add($"{pair.Key}: {rule.Id} dropped from {pair.Value} to {count}.");
            }
        }
        if (shrunk.Count > 0 && lowerBaseline)
        {
            WriteLoweredBaseline(baselinePath, baseline, actual);
            shrunk.Clear();
        }

        var totals = new List<string>();
        foreach (var rule in Rules)
        {
            var observed = actual[rule.Id].Values.Sum(lines => lines.Count);
            var allowed = baseline.TryGetValue(rule.Id, out var entries) ? entries.Values.Sum() : 0;
            totals.Add($"Total {rule.Id}: {observed} occurrence(s), baseline {allowed}.");
        }
        return new EvaluationResult(failures, shrunk, totals);
    }

    /// <param name="Failures">Growth beyond the baseline; each one fails the guard.</param>
    /// <param name="Shrunk">Counts below the baseline; tighten with the lower-baseline command.</param>
    /// <param name="Totals">Per-rule totals, for reviewing an entry moved with renamed code.</param>
    public sealed record EvaluationResult(
        IReadOnlyList<string> Failures, IReadOnlyList<string> Shrunk, IReadOnlyList<string> Totals);

    public const string LowerBaselineCommand = @".\scripts\validate-test-timing-patterns.ps1 -LowerBaseline";

    public static Dictionary<string, SortedDictionary<string, List<int>>> ScanRepository(string repositoryRoot)
    {
        var result = Rules.ToDictionary(rule => rule.Id,
            _ => new SortedDictionary<string, List<int>>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                continue;
            var source = File.ReadAllText(path);
            var uiAutomation = relative.StartsWith("tests/OpenClaw.Tray.UITests/", StringComparison.Ordinal) &&
                source.Contains("System.Windows.Automation", StringComparison.Ordinal);
            foreach (var pair in Count(source, uiAutomation))
                if (pair.Value.Count > 0)
                    result[pair.Key][relative] = pair.Value;
        }
        return result;
    }

    /// <summary>Returns the 1-based lines of each rule's unexcused matches in one source file.</summary>
    public static Dictionary<string, List<int>> Count(string source, bool uiAutomationFile)
    {
        var masked = SourceMask.Apply(source, out var comment);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
            if (source[i] == '\n') lineStarts.Add(i + 1);

        var counts = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            var lines = new List<int>();
            if (rule.AllTestCode || uiAutomationFile)
            {
                foreach (var pattern in rule.Patterns)
                {
                    foreach (Match match in pattern.Matches(masked))
                    {
                        if (rule.Accept is not null && !rule.Accept(match)) continue;
                        var line = lineStarts.BinarySearch(match.Index);
                        line = line >= 0 ? line : ~line - 1;
                        if (!HasException(source, lineStarts, comment, line)) lines.Add(line + 1);
                    }
                }
            }
            lines.Sort();
            counts[rule.Id] = lines;
        }
        return counts;
    }

    // The marker counts only inside a real comment on the line itself, or on a preceding comment-only line.
    private static bool HasException(string source, List<int> lineStarts, bool[] comment, int line)
    {
        if (HasExceptionMarker(source, lineStarts, comment, line)) return true;
        if (line == 0) return false;
        var start = lineStarts[line - 1];
        while (start < source.Length && source[start] is ' ' or '\t') start++;
        return start < source.Length && comment[start] && HasExceptionMarker(source, lineStarts, comment, line - 1);
    }

    private static bool HasExceptionMarker(string source, List<int> lineStarts, bool[] comment, int line)
    {
        var start = lineStarts[line];
        var end = line + 1 < lineStarts.Count ? lineStarts[line + 1] : source.Length;
        var marker = "// " + ExceptionMarker;
        for (var index = source.IndexOf(marker, start, end - start, StringComparison.Ordinal);
            index >= 0;
            index = source.IndexOf(marker, index + 1, end - index - 1, StringComparison.Ordinal))
        {
            var reasonStart = index + marker.Length;
            if (comment[index] && source.Substring(reasonStart, end - reasonStart).Trim().Length >= MinimumExceptionReasonLength)
                return true;
        }
        return false;
    }

    private static bool IsShortLiteralDuration(Match match)
    {
        double seconds;
        if (match.Groups["ms"].Success)
        {
            seconds = double.Parse(match.Groups["ms"].Value.Replace("_", ""), CultureInfo.InvariantCulture) / 1000;
        }
        else
        {
            var value = double.Parse(match.Groups["value"].Value.Replace("_", ""), CultureInfo.InvariantCulture);
            seconds = match.Groups["unit"].Value == "Seconds" ? value : value / 1000;
        }
        // Zero is a non-blocking signal check, not a hang guard.
        return seconds > 0 && seconds < MinimumLiteralHangGuardSeconds;
    }

    private static Dictionary<string, SortedDictionary<string, int>> ReadBaseline(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Missing test timing baseline.", path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The test timing baseline is empty.");
        foreach (var id in parsed.Keys)
            if (!Rules.Any(rule => rule.Id == id))
                throw new InvalidDataException($"Unknown rule '{id}' in the test timing baseline.");
        return parsed.ToDictionary(entry => entry.Key,
            entry => new SortedDictionary<string, int>(entry.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static void WriteLoweredBaseline(
        string path,
        Dictionary<string, SortedDictionary<string, int>> baseline,
        Dictionary<string, SortedDictionary<string, List<int>>> actual)
    {
        var lowered = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            var entries = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (baseline.TryGetValue(rule.Id, out var allowed))
            {
                foreach (var pair in allowed)
                {
                    // Never raise a count: new occurrences still fail the comparison.
                    var count = Math.Min(pair.Value, actual[rule.Id].TryGetValue(pair.Key, out var lines) ? lines.Count : 0);
                    if (count > 0) entries[pair.Key] = count;
                }
            }
            lowered[rule.Id] = entries;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(lowered, new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n") + "\n");
    }

    /// <summary>Blanks comments and string/char contents, preserving offsets and newlines.</summary>
    private sealed class SourceMask
    {
        private readonly string _source;
        private readonly char[] _output;
        private readonly bool[] _comment;
        private int _index;

        private SourceMask(string source)
        {
            _source = source;
            _output = source.ToCharArray();
            _comment = new bool[source.Length];
        }

        /// <summary>Returns the masked source; <paramref name="comment"/> marks offsets inside comments.</summary>
        public static string Apply(string source, out bool[] comment)
        {
            var mask = new SourceMask(source);
            mask.Code(stopAtClosingBrace: false);
            comment = mask._comment;
            return new string(mask._output);
        }

        private void BlankComment()
        {
            _comment[_index] = true;
            Blank();
        }

        private char Peek(int offset) => _index + offset < _source.Length ? _source[_index + offset] : '\0';

        private void Blank()
        {
            if (_source[_index] != '\n' && _source[_index] != '\r') _output[_index] = ' ';
            _index++;
        }

        private void Code(bool stopAtClosingBrace)
        {
            var depth = 0;
            while (_index < _source.Length)
            {
                var c = _source[_index];
                if (c == '/' && Peek(1) == '/')
                {
                    while (_index < _source.Length && _source[_index] != '\n') BlankComment();
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    BlankComment(); BlankComment();
                    while (_index < _source.Length && !(_source[_index] == '*' && Peek(1) == '/')) BlankComment();
                    if (_index < _source.Length) { BlankComment(); BlankComment(); }
                }
                else if (c == '\'')
                {
                    CharLiteral();
                }
                else if (StringStartsHere())
                {
                    StringLiteral();
                }
                else
                {
                    if (stopAtClosingBrace)
                    {
                        if (c == '{') depth++;
                        else if (c == '}' && depth-- == 0) return;
                    }
                    _index++;
                }
            }
        }

        private bool StringStartsHere()
        {
            var offset = 0;
            while (Peek(offset) == '$' || Peek(offset) == '@') offset++;
            return Peek(offset) == '"';
        }

        private void CharLiteral()
        {
            _index++;
            while (_index < _source.Length && _source[_index] != '\'' && _source[_index] != '\n')
            {
                if (_source[_index] == '\\') Blank();
                if (_index < _source.Length) Blank();
            }
            if (_index < _source.Length && _source[_index] == '\'') _index++;
        }

        private void StringLiteral()
        {
            var dollars = 0;
            var verbatim = false;
            while (_source[_index] == '$' || _source[_index] == '@')
            {
                if (_source[_index] == '$') dollars++; else verbatim = true;
                _index++;
            }
            var interpolated = dollars > 0;
            var quotes = 0;
            while (Peek(quotes) == '"') quotes++;
            if (quotes >= 3 && !verbatim)
            {
                RawString(quotes, dollars);
                return;
            }

            _index++;
            while (_index < _source.Length)
            {
                var c = _source[_index];
                if (verbatim && c == '"')
                {
                    if (Peek(1) == '"') { Blank(); Blank(); continue; }
                    _index++;
                    return;
                }
                if (!verbatim)
                {
                    if (c == '\\') { Blank(); if (_index < _source.Length) Blank(); continue; }
                    if (c == '"') { _index++; return; }
                    if (c == '\n') return;
                }
                if (interpolated && c == '{')
                {
                    if (Peek(1) == '{') { Blank(); Blank(); continue; }
                    _index++;
                    Code(stopAtClosingBrace: true);
                    if (_index < _source.Length) _index++;
                    continue;
                }
                if (interpolated && c == '}' && Peek(1) == '}') { Blank(); Blank(); continue; }
                Blank();
            }
        }

        // In a raw string with N dollar signs, N consecutive braces open an interpolation hole.
        private void RawString(int delimiter, int dollars)
        {
            _index += delimiter;
            while (_index < _source.Length)
            {
                if (_source[_index] == '"')
                {
                    var run = 0;
                    while (Peek(run) == '"') run++;
                    if (run >= delimiter) { _index += run; return; }
                    for (var i = 0; i < run; i++) Blank();
                    continue;
                }
                if (dollars > 0 && _source[_index] == '{')
                {
                    var braces = 0;
                    while (Peek(braces) == '{') braces++;
                    if (braces >= dollars)
                    {
                        // Extra leading braces are literal content.
                        for (var i = 0; i < braces - dollars; i++) Blank();
                        _index += dollars;
                        Code(stopAtClosingBrace: true);
                        for (var i = 0; i < dollars && _index < _source.Length && _source[_index] == '}'; i++) _index++;
                        continue;
                    }
                    for (var i = 0; i < braces; i++) Blank();
                    continue;
                }
                Blank();
            }
        }
    }
}
