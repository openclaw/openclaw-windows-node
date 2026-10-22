namespace OpenClaw.Tray.Tests;

public sealed class ChatInputLockContractTests
{
    [Theory]
    [InlineData("OpenClawGatewayClient.cs", "_sessionsLock")]
    [InlineData("PendingRequestRegistry.cs", "_gate")]
    public void ReceiveStateLocksCannotInvokeSubscribersCompleteWaitersOrDispatch(string file, string gate)
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Shared", file));
        var matches = System.Text.RegularExpressions.Regex.Matches(source,
            $@"lock\s*\({System.Text.RegularExpressions.Regex.Escape(gate)}\)\s*\{{");
        Assert.NotEmpty(matches);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var start = match.Index + match.Length;
            var end = start;
            var depth = 1;
            while (end < source.Length && depth > 0)
            {
                if (source[end] == '{') depth++;
                if (source[end] == '}') depth--;
                end++;
            }
            Assert.Equal(0, depth);
            var block = source[start..(end - 1)];
            foreach (var forbidden in new[]
            {
                "await ", "SendRawAsync(", "SendTrackedRequestAsync(", "SendResponseRequestAsync(",
                "SendWizardRequestAsync(", ".Wait(", ".Result", "GetAwaiter(", ".Invoke(",
                ".TrySetResult(", ".TrySetException(", "FaultDrainedEntries(",
            })
                Assert.DoesNotContain(forbidden, block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConversationTransactionsCannotDispatchOrBlockOnNetworkUnderStateLock()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ChatConversationState.cs"));
        foreach (var forbidden in new[]
        {
            "async ", "await ", ".Wait(", ".Result", "GetAwaiter(",
            "IChatGatewayBridge", "IOperatorGatewayClient", "SendRawAsync", "ExternalOrigin.IsCurrent",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAbortPathsCannotUseUnscopedTransport()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "OpenClawChatDataProvider.cs"));
        Assert.DoesNotContain(".SendChatAbortAsync(", source, StringComparison.Ordinal);
    }
}
