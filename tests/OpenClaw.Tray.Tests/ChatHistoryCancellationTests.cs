using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Real loader/projection cancellation: the checks live INSIDE the ordering and the projection
/// enumeration (not only around the caller's ToArray), and the token is cancelled partway through a
/// bounded barrier so the OperationCanceledException is deterministic, not timing based.
/// </summary>
public class ChatHistoryCancellationTests
{
    private static List<ChatMessageInfo> Messages(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ChatMessageInfo { Text = "m" + i, OpenClawSeq = i })
            .ToList();

    [Fact]
    public void Project_CancelledDuringEnumeration_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var source = Messages(600);

        IEnumerable<ChatMessageInfo> CancellingInput()
        {
            var n = 0;
            foreach (var message in source)
            {
                if (++n == 300)
                    cts.Cancel(); // bounded barrier: cancel mid-enumeration
                yield return message;
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryReplayProjection.Project(CancellingInput(), cts.Token).ToList());
    }

    [Fact]
    public void OrderHistoryMessages_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryLoader.OrderHistoryMessages(Messages(10), cts.Token));
    }

    [Fact]
    public void OrderHistoryMessages_PreservesSeqOrderingWithIndexTiebreaker()
    {
        var messages = new List<ChatMessageInfo>
        {
            new() { Text = "b", OpenClawSeq = 2 },
            new() { Text = "a", OpenClawSeq = 1 },
            new() { Text = "c", OpenClawSeq = 3 },
        };

        var ordered = ChatHistoryLoader.OrderHistoryMessages(messages, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(m => m.Text));
    }

    [Fact]
    public void OrderHistoryMessages_EqualSeqValues_KeepIndexTiebreakerStability()
    {
        // All sequenced, two share the same seq: the original index tiebreaker must keep input order.
        var messages = new List<ChatMessageInfo>
        {
            new() { Text = "first", OpenClawSeq = 7 },
            new() { Text = "second", OpenClawSeq = 7 },
            new() { Text = "third", OpenClawSeq = 8 },
        };

        var ordered = ChatHistoryLoader.OrderHistoryMessages(messages, CancellationToken.None);

        Assert.Equal(new[] { "first", "second", "third" }, ordered.Select(m => m.Text));
    }

    [Fact]
    public void OrderHistoryMessages_NoneSeq_EqualTimestamps_KeepInsertionOrder()
    {
        // No seq at all and equal timestamps: timestamp ordering is a tie, so insertion order holds.
        var messages = new List<ChatMessageInfo>
        {
            new() { Text = "alpha", Ts = 5000 },
            new() { Text = "beta", Ts = 5000 },
            new() { Text = "gamma", Ts = 4000 },
        };

        var ordered = ChatHistoryLoader.OrderHistoryMessages(messages, CancellationToken.None);

        Assert.Equal(new[] { "gamma", "alpha", "beta" }, ordered.Select(m => m.Text));
    }

    [Fact]
    public void OrderHistoryMessages_MixedSequencing_PreservesInsertionOrder()
    {
        var messages = new List<ChatMessageInfo>
        {
            new() { Text = "first", OpenClawSeq = 5 },
            new() { Text = "second" },
            new() { Text = "third", OpenClawSeq = 1 },
        };

        var ordered = ChatHistoryLoader.OrderHistoryMessages(messages, CancellationToken.None);

        Assert.Equal(new[] { "first", "second", "third" }, ordered.Select(m => m.Text));
    }
}
