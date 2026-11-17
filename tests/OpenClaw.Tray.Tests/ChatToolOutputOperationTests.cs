using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolOutputOperationTests
{
    private static ChatToolOutputRequest Target =>
        new("main", 1, 2, 3, "entry", "run", "call", "session", "result", new(1, true));

    [Fact]
    public async Task Read_IsExplicitDeduplicatedAndCloseRetiresAnIgnoringCompletion()
    {
        using var operation = new ChatToolOutputOperation();
        var response = new TaskCompletionSource<ChatToolOutputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        Task<ChatToolOutputResult> Read(ChatToolOutputRequest _, CancellationToken cancellation)
        {
            calls++;
            token = cancellation;
            return response.Task;
        }
        operation.SetTarget(Target, Read);
        Assert.Equal(0, calls);
        var pending = operation.LoadAsync(Target, Read);
        await operation.LoadAsync(Target, Read);
        Assert.Equal(1, calls);
        Assert.True(operation.Snapshot(Target, Read, Target.Profile).Loading);
        operation.Close();
        Assert.True(token.IsCancellationRequested);
        response.SetResult(new(ChatToolOutputStatus.Loaded, "Must not appear"));
        await pending;
        Assert.Null(operation.Snapshot(Target, Read, Target.Profile).Result);
        Assert.False(operation.Snapshot(Target, Read, Target.Profile).Loading);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("history")]
    [InlineData("connection")]
    [InlineData("entry")]
    [InlineData("run")]
    [InlineData("call")]
    [InlineData("result")]
    [InlineData("provider")]
    [InlineData("profile")]
    [InlineData("unknown-profile")]
    public async Task ScopeChange_RetiresTheOldSlot(string changed)
    {
        using var operation = new ChatToolOutputOperation();
        var response = new TaskCompletionSource<ChatToolOutputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ChatToolOutputResult> Read(ChatToolOutputRequest _, CancellationToken token) => response.Task;
        operation.SetTarget(Target, Read);
        var pending = operation.LoadAsync(Target, Read);
        var next = changed switch
        {
            "session" => Target with { SessionKey = "other", SessionId = "other-session" },
            "generation" => Target with { TimelineGeneration = 2 },
            "history" => Target with { HistoryRevision = 3 },
            "connection" => Target with { ConnectionEpoch = 4 },
            "entry" => Target with { EntryId = "other" },
            "run" => Target with { RunId = "other" },
            "call" => Target with { ToolCallId = "other" },
            "result" => Target with { ResultMessageId = "other" },
            "profile" => Target with { Profile = new(2, true) },
            "unknown-profile" => Target with { Profile = default },
            _ => Target,
        };
        Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>> loader = changed == "provider"
            ? (_, _) => Task.FromResult(new ChatToolOutputResult(ChatToolOutputStatus.Loaded, "New provider"))
            : Read;
        operation.SetTarget(next, loader);
        response.SetResult(new(ChatToolOutputStatus.Loaded, "Old private slot"));
        await pending;
        Assert.Null(operation.Snapshot(next, loader, next.Profile).Result);
    }

    [Fact]
    public async Task CachedOutput_CannotSurviveAReverifiedProfileRevision()
    {
        using var operation = new ChatToolOutputOperation();
        var calls = 0;
        Task<ChatToolOutputResult> Read(ChatToolOutputRequest _, CancellationToken token)
        {
            calls++;
            return Task.FromResult(new ChatToolOutputResult(ChatToolOutputStatus.Loaded, "Private result"));
        }
        await operation.LoadAsync(Target, Read);
        Assert.NotNull(operation.Snapshot(Target, Read, Target.Profile).Result);
        var verifiedAgain = Target with { Profile = new(2, true) };
        Assert.Null(operation.Snapshot(Target, Read, verifiedAgain.Profile).Result);
        Assert.Null(operation.Snapshot(Target, Read, default).Result);
        Assert.Null(operation.Snapshot(verifiedAgain, Read, verifiedAgain.Profile).Result);
        operation.SetTarget(verifiedAgain, Read);
        Assert.Null(operation.Snapshot(verifiedAgain, Read, verifiedAgain.Profile).Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failure_IsExplicitAndRetryIsAnotherReaderAction()
    {
        using var operation = new ChatToolOutputOperation();
        var calls = 0;
        Task<ChatToolOutputResult> Read(ChatToolOutputRequest _, CancellationToken token) =>
            ++calls == 1 ? Task.FromException<ChatToolOutputResult>(new TimeoutException())
                : Task.FromResult(new ChatToolOutputResult(ChatToolOutputStatus.Loaded, "Recovered"));
        operation.SetTarget(Target, Read);
        await operation.LoadAsync(Target, Read);
        Assert.Equal(ChatToolOutputStatus.Error, operation.Snapshot(Target, Read, Target.Profile).Result!.Status);
        Assert.Equal(1, calls);
        await operation.LoadAsync(Target, Read);
        Assert.Equal("Recovered", operation.Snapshot(Target, Read, Target.Profile).Result!.Output);
        Assert.Equal(2, calls);
    }
}
