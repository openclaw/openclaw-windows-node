using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Deterministic in-work cancellation for the sort COMPARISON phase and for a large single-message
/// multi-part projection. The comparator hook is narrow and optional (null in production); the
/// projection test counts actual enumerated parts, proving the inner loop is bounded by parts, not
/// by the outer message count.
/// </summary>
public class ChatHistoryInnerCancellationTests
{
    [Fact]
    public void OrderHistoryMessages_ComparatorPhaseCancellation_ThrowsOperationCanceled()
    {
        var messages = Enumerable.Range(0, 8)
            .Select(i => new ChatMessageInfo { Text = "m" + i, OpenClawSeq = 8 - i }) // distinct seq -> mode 0
            .ToList();
        using var cts = new CancellationTokenSource();

        // Narrow optional hook: cancel during the comparison phase (not before it).
        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryLoader.OrderHistoryMessages(
                messages,
                cts.Token,
                comparisonObserver: n => { if (n == 3) cts.Cancel(); },
                comparisonInterval: 1));
    }

    private sealed class CountingParts(IReadOnlyList<ChatMessageContentPartInfo> inner)
        : IReadOnlyList<ChatMessageContentPartInfo>
    {
        public int Yielded;
        public int Count => inner.Count;
        public ChatMessageContentPartInfo this[int index] => inner[index];
        public IEnumerator<ChatMessageContentPartInfo> GetEnumerator()
        {
            foreach (var part in inner)
            {
                Yielded++;
                yield return part;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Project_LargeSingleMessage_MultiPartCancellation_StopsWithinFiniteParts()
    {
        var parts = new CountingParts(
            Enumerable.Range(0, 600)
                .Select(i => new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Text,
                    Text = "part-" + i,
                })
                .ToList());
        var message = new ChatMessageInfo { SessionKey = "main", Role = "assistant", ContentParts = parts };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled up-front: the FINITE per-part check must stop enumeration partway

        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryReplayProjection.Project(new[] { message }, cts.Token).ToList());

        // Bounded by the finite per-part interval, NOT all 600 parts.
        Assert.InRange(parts.Yielded, 1, 599);
    }
}
