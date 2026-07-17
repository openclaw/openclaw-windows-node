using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProgramArgumentTests : IDisposable
{
    private static readonly string[] s_valueOptionNames =
    [
        "--config",
        "--log-path",
        "--json-output",
        "--data-dir",
        "--local-data-dir",
        "--distro-name",
        "--gateway-port",
        "--autostart-name",
        "--startup-task-name",
    ];

    private readonly string _tempDir;

    public ProgramArgumentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"program-argument-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    public static TheoryData<string> ValueOptionNames
        => new(s_valueOptionNames);

    public static TheoryData<string> EmptyConfigContents
        => new("", " \t\r\n");

    public static TheoryData<string, string> WhitespaceValueCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var optionName in s_valueOptionNames)
            {
                foreach (var value in new[] { " ", "\t", "\r\n" })
                    cases.Add(optionName, value);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseValueArguments_RejectsMissingValue(string optionName)
    {
        var parsed = Program.TryParseValueArguments([optionName], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseValueArguments_RejectsAnotherOptionAsValue(string optionName)
    {
        var parsed = Program.TryParseValueArguments(
            [optionName, "--headless"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseValueArguments_RejectsEmptyValue(string optionName)
    {
        var parsed = Program.TryParseValueArguments([optionName, ""], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(WhitespaceValueCases))]
    public void TryParseValueArguments_RejectsWhitespaceValue(string optionName, string value)
    {
        var parsed = Program.TryParseValueArguments([optionName, value], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Fact]
    public void TryParseValueArguments_RejectsDuplicateOption()
    {
        var parsed = Program.TryParseValueArguments(
            ["--data-dir", "first", "--data-dir", "second"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("--data-dir may only be specified once.", error);
    }

    [Fact]
    public void TryParseValueArguments_ParsesEveryValueOption()
    {
        var args = s_valueOptionNames
            .SelectMany((option, index) => new[] { option, $"value-{index}" })
            .ToArray();

        var parsed = Program.TryParseValueArguments(args, out var values, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(s_valueOptionNames.Length, values.Count);
        for (var i = 0; i < s_valueOptionNames.Length; i++)
            Assert.Equal($"value-{i}", values[s_valueOptionNames[i]]);
    }

    [Fact]
    public async Task Main_RejectsConfigFollowedByFlagBeforeLoadingBundledDefault()
    {
        var exitCode = await Program.Main(["--config", "--headless"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitMissingConfig()
    {
        var configPath = Path.Combine(_tempDir, "missing.json");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitMalformedConfig()
    {
        var configPath = Path.Combine(_tempDir, "malformed.json");
        await File.WriteAllTextAsync(configPath, "{ not-json");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Theory]
    [MemberData(nameof(EmptyConfigContents))]
    public async Task Main_RejectsExplicitEmptyConfig(string contents)
    {
        var configPath = Path.Combine(_tempDir, $"empty-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, contents);

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitNullConfig()
    {
        var configPath = Path.Combine(_tempDir, "null.json");
        await File.WriteAllTextAsync(configPath, "null");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitUnreadableConfig()
    {
        var configPath = Path.Combine(_tempDir, "locked.json");
        await File.WriteAllTextAsync(configPath, "{}");
        await using var configLock = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitConfigWithInvalidPathSyntax()
    {
        var exitCode = await Program.Main(["--config", "invalid\0path.json", "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsExplicitValidConfig()
    {
        var configPath = Path.Combine(_tempDir, "valid.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(0, exitCode);
    }
}
