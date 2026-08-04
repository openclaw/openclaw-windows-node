using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionQueryCoordinatorTests
{
    [Theory]
    [InlineData(1000, 10)]
    [InlineData(2000, 20)]
    public async Task RecentLoad_PagesInHundreds_ToBoundedMaximum(int total, int expectedPages)
    {
        var offsets = new List<int>();
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            var offset = request.Offset ?? 0;
            offsets.Add(offset);
            var count = Math.Min(SessionQueryCoordinator.PageSize, total - offset);
            return Task.FromResult(Page(
                Enumerable.Range(offset, count).Select(Session),
                offset,
                offset + count < total ? offset + count : null,
                offset + count < total));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(total, snapshot.Sessions.Count);
        Assert.Equal(expectedPages, snapshot.PagesRead);
        Assert.Equal(Enumerable.Range(0, expectedPages).Select(i => i * 100), offsets);
    }

    [Fact]
    public async Task Paging_DeduplicatesKeys_AndKeepsLaterMutation()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            if (request.Offset == 0)
            {
                return Task.FromResult(Page(
                    Enumerable.Range(0, 100).Select(Session),
                    0, 100, true));
            }
            var rows = Enumerable.Range(100, 99).Select(Session).Prepend(
                new SessionInfo { Key = "agent:main:0", Label = "mutated" });
            return Task.FromResult(Page(rows, 100, null, false));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(199, snapshot.Sessions.Count);
        Assert.Equal("mutated", Assert.Single(snapshot.Sessions, s => s.Key == "agent:main:0").Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Paging_StopsOnMalformedOrStalledNextOffset(int nextOffset)
    {
        var calls = 0;
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            calls++;
            return Task.FromResult(Page(
                Enumerable.Range(0, 100).Select(Session),
                request.Offset ?? 0,
                nextOffset,
                true));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, snapshot.PagesRead);
    }

    [Fact]
    public async Task Paging_StopsWhenNextOffsetRepeatsSeenCursor()
    {
        var calls = 0;
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            calls++;
            var offset = request.Offset ?? 0;
            return Task.FromResult(Page(
                Enumerable.Range(offset, 100).Select(Session),
                offset,
                offset == 0 ? 100 : 0,
                true));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(2, calls);
        Assert.Equal(2, snapshot.PagesRead);
    }

    [Fact]
    public async Task HiddenOnlyPage_DoesNotStopRawPaging()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            if (request.Offset == 0)
            {
                var hidden = Enumerable.Range(0, 100).Select(i => new SessionInfo
                {
                    Key = $"agent:main:subagent:{i}",
                    Presentation = new SessionPresentationInfo
                    {
                        Title = "Background",
                        Family = "subagent",
                        IsBackground = true,
                    },
                });
                return Task.FromResult(Page(hidden, 0, 100, true));
            }
            return Task.FromResult(Page([new SessionInfo { Key = "agent:main:visible" }], 100, null, false));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(2, snapshot.PagesRead);
        Assert.Equal("agent:main:visible", Assert.Single(snapshot.Sessions).Key);
    }

    [Fact]
    public async Task AdvanceConnectionGeneration_CancelsInFlightAndRejectsLateResponse()
    {
        var response = new TaskCompletionSource<SessionListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new SessionQueryCoordinator((_, _) => response.Task, TimeSpan.Zero);
        var query = coordinator.LoadRecentAsync();

        coordinator.AdvanceConnectionGeneration();
        response.TrySetResult(Page([Session(1)], 0, null, false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Empty(coordinator.ClearSearch().Sessions);
    }

    [Fact]
    public async Task AdvanceConnectionGeneration_RejectsCompletedSnapshotPublication()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);
        var snapshot = await coordinator.LoadRecentAsync();
        var applied = false;

        coordinator.AdvanceConnectionGeneration();
        var accepted = coordinator.TryApplyCurrentRecentSnapshot(
            snapshot,
            _ => applied = true);

        Assert.False(accepted);
        Assert.False(applied);
    }

    [Fact]
    public async Task ConcurrentRecentLoads_LatestIdentityWins()
    {
        var firstResponse = new TaskCompletionSource<SessionListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        using var coordinator = new SessionQueryCoordinator((_, _) =>
        {
            call++;
            return call == 1
                ? firstResponse.Task
                : Task.FromResult(Page([new SessionInfo { Key = "agent:main:latest" }], 0, null, false));
        }, TimeSpan.Zero);
        var first = coordinator.LoadRecentAsync();
        var latest = await coordinator.LoadRecentAsync();
        firstResponse.TrySetResult(Page([new SessionInfo { Key = "agent:main:stale" }], 0, null, false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("agent:main:latest", Assert.Single(latest.Sessions).Key);
        Assert.Equal(
            latest.Sessions.Select(session => session.Key),
            coordinator.ClearSearch().Sessions.Select(session => session.Key));
    }

    [Fact]
    public async Task ConcurrentRecentCompletionAndSupersession_DoesNotRaceCtsDisposal()
    {
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var firstResponse = new TaskCompletionSource<SessionListResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = 0;
            using var coordinator = new SessionQueryCoordinator((_, _) =>
            {
                return Interlocked.Increment(ref call) == 1
                    ? firstResponse.Task
                    : Task.FromResult(Page([Session(2)], 0, null, false));
            }, TimeSpan.Zero);
            var first = coordinator.LoadRecentAsync();

            var complete = Task.Run(async () =>
            {
                await start.Task;
                firstResponse.TrySetResult(Page([Session(1)], 0, null, false));
            });
            var supersede = Task.Run(async () =>
            {
                await start.Task;
                return await coordinator.LoadRecentAsync();
            });

            start.TrySetResult();
            await complete;
            _ = await supersede;
            var firstException = await Record.ExceptionAsync(() => first);
            Assert.True(
                firstException is null or OperationCanceledException,
                $"Unexpected supersession exception: {firstException}");
        }
    }

    [Fact]
    public async Task Dispose_CancelsClientOwnedQuery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SessionQueryCoordinator(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Page([], 0, null, false);
        }, TimeSpan.Zero);
        var query = coordinator.LoadRecentAsync();
        await started.Task;

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
    }

    [Fact]
    public async Task Search_FindsOnlyRowReturnedOnNinthPage()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            var offset = request.Offset ?? 0;
            var rows = offset == 800
                ? new[] { new SessionInfo { Key = "agent:main:900", Label = "needle" } }
                : Array.Empty<SessionInfo>();
            return Task.FromResult(Page(rows, offset, offset < 800 ? offset + 100 : null, offset < 800));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery { Search = "needle" });

        Assert.Equal(9, snapshot.PagesRead);
        Assert.Equal("agent:main:900", Assert.Single(snapshot.Sessions).Key);
    }

    [Fact]
    public async Task Search_DebounceCancelsOldQuery_AndLatestWins()
    {
        var requests = new List<string?>();
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            lock (requests) requests.Add(request.Search);
            return Task.FromResult(Page(
                [new SessionInfo { Key = $"agent:main:{request.Search}" }],
                0, null, false));
        }, TimeSpan.FromMilliseconds(50));

        var old = coordinator.SearchAsync(new SessionQuery { Search = "old" });
        var latest = coordinator.SearchAsync(new SessionQuery { Search = "latest" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        var snapshot = await latest;
        Assert.Equal("agent:main:latest", Assert.Single(snapshot.Sessions).Key);
        Assert.Equal(["latest"], requests);
    }

    [Fact]
    public async Task Search_PinsSelectedLocalSession_WhenServerOmitsIt()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "one",
            IncludeBackground = true,
            PinnedSessions = [new SessionInfo { Key = "agent:main:selected", Label = "Selected" }],
        });

        Assert.Contains(snapshot.Sessions, s => s.Key == "agent:main:selected");
    }

    [Fact]
    public async Task ClearSearch_RestoresCoherentRecentSnapshotMetadata()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);
        var recent = await coordinator.LoadRecentAsync();
        _ = await coordinator.SearchAsync(new SessionQuery { Search = "other" });

        var restored = coordinator.ClearSearch();

        Assert.NotSame(recent, restored);
        Assert.Equal(recent.Sessions.Select(session => session.Key), restored.Sessions.Select(session => session.Key));
        Assert.Equal(recent.PagesRead, restored.PagesRead);
        Assert.Equal(recent.IsLegacyResponse, restored.IsLegacyResponse);
    }

    [Fact]
    public async Task ClearSearch_DoesNotReuseRecentAcrossRestoreIdentityChanges()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
            Task.FromResult(Page(
                [new SessionInfo
                {
                    Key = $"agent:{request.AgentId}:recent",
                    Label = request.ConfiguredAgentsOnly == true ? "Configured" : "All",
                }],
                0, null, false)),
            TimeSpan.Zero);
        var pinned = new SessionInfo { Key = "agent:a:pinned", Label = "Pinned" };
        var recentQuery = new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            IncludeBackground = true,
            PinnedSessions = [pinned],
        };
        var recent = await coordinator.LoadRecentAsync(recentQuery);
        _ = await coordinator.SearchAsync(new SessionQuery
        {
            AgentId = "a",
            Search = "other",
            ConfiguredAgentsOnly = true,
            IncludeBackground = true,
            PinnedSessions = [pinned],
        });

        Assert.Equal(
            recent.Sessions.Select(session => session.Key),
            coordinator.ClearSearch(recentQuery).Sessions.Select(session => session.Key));
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "b",
            ConfiguredAgentsOnly = true,
            IncludeBackground = true,
            PinnedSessions = [],
        }).Sessions);
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = false,
            IncludeBackground = true,
            PinnedSessions = [],
        }).Sessions);
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            IncludeBackground = false,
            PinnedSessions = [],
        }).Sessions);
        var withoutPin = coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            IncludeBackground = true,
            PinnedSessions = [],
        });
        Assert.Equal("agent:a:recent", Assert.Single(withoutPin.Sessions).Key);
        var replacementPin = new SessionInfo { Key = "agent:a:replacement", Label = "Replacement" };
        var withReplacementPin = coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            IncludeBackground = true,
            PinnedSessions = [replacementPin],
        });
        Assert.Equal(
            ["agent:a:recent", replacementPin.Key],
            withReplacementPin.Sessions.Select(session => session.Key));
    }

    [Fact]
    public async Task ClearSearch_ReprojectsChangedSameKeyCurrentPin()
    {
        var selectedKey = "agent:main:selected";
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page(
                [
                    new SessionInfo { Key = "agent:main:before", Label = "Before" },
                    new SessionInfo
                    {
                        Key = selectedKey,
                        Label = "Stale server label",
                        CurrentActivity = "Stale server activity",
                        Presentation = new SessionPresentationInfo { Title = "Stale server title" },
                    },
                    new SessionInfo { Key = "agent:main:after", Label = "After" },
                ],
                0,
                null,
                false)),
            TimeSpan.Zero);
        var oldPin = new SessionInfo
        {
            Key = selectedKey,
            Label = "Old label",
            CurrentActivity = "Old activity",
            Presentation = new SessionPresentationInfo { Title = "Old title" },
        };
        _ = await coordinator.LoadRecentAsync(new SessionQuery
        {
            IncludeBackground = true,
            PinnedSessions = [oldPin],
        });
        var currentPin = new SessionInfo
        {
            Key = oldPin.Key,
            Label = "Current label",
            CurrentActivity = "Current activity",
            Presentation = new SessionPresentationInfo { Title = "Current title" },
        };

        var restored = coordinator.ClearSearch(new SessionQuery
        {
            IncludeBackground = true,
            PinnedSessions = [currentPin],
        });

        var selected = Assert.Single(restored.Sessions, session => session.Key == currentPin.Key);
        Assert.Equal(
            ["agent:main:before", selectedKey, "agent:main:after"],
            restored.Sessions.Select(session => session.Key));
        Assert.Equal("Current label", selected.Label);
        Assert.Equal("Current activity", selected.CurrentActivity);
        Assert.Equal("Current title", selected.Presentation?.Title);
    }

    [Fact]
    public async Task LegacySearch_FiltersSafeDisplayFieldsWithoutMatchingRawKeys()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions =
                [
                    new SessionInfo
                    {
                        Key = "agent:main:telegram:main:direct:needle",
                        Label = "Unrelated",
                    },
                    new SessionInfo { Key = "agent:main:label", Label = "Project Needle" },
                    new SessionInfo
                    {
                        Key = "agent:main:presentation",
                        Presentation = new SessionPresentationInfo { Title = "Needle discussion" },
                    },
                    new SessionInfo { Key = "agent:main:miss", DisplayName = "Different" },
                ],
                IsLegacyResponse = true,
            }),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "  needle ",
            IncludeBackground = true,
        });

        Assert.Equal(SessionSearchExecutionMode.LegacyLocal, snapshot.SearchExecutionMode);
        Assert.Equal(
            ["agent:main:label", "agent:main:presentation"],
            snapshot.Sessions.Select(session => session.Key));
        Assert.DoesNotContain(
            snapshot.Sessions,
            session => session.Key == "agent:main:telegram:main:direct:needle");
    }

    [Fact]
    public async Task LegacySearch_UsesResolvedVisiblePresentationWithoutUnsafeOrOpaqueFields()
    {
        const string opaqueId = "01234567-89ab-cdef-0123-456789abcdef";
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions =
                [
                    new SessionInfo
                    {
                        Key = $"agent:main:tui-{opaqueId}",
                        DisplayName = $"Terminal:{opaqueId}",
                    },
                    new SessionInfo { Key = "global" },
                    new SessionInfo { Key = "agent:ops:tui-safe", ExecNode = "buildbox" },
                    new SessionInfo
                    {
                        Key = "agent:main:explicit:safe",
                        SessionId = "secretneedle",
                        ParentSessionKey = "secretneedle",
                    },
                    new SessionInfo
                    {
                        Key = "agent:main:tui-duplicate",
                        Label = "Terminal session",
                    },
                    new SessionInfo
                    {
                        Key = "agent:main:explicit:visible",
                        Label = "Visible title",
                        Subject = "hiddenneedle",
                        Room = "hiddenneedle",
                        Space = "hiddenneedle",
                        OriginLabel = "hiddenneedle",
                    },
                ],
                IsLegacyResponse = true,
            }),
            TimeSpan.Zero);

        var terminal = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "terminal session",
            IncludeBackground = true,
        });
        var global = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "global session",
            IncludeBackground = true,
        });
        var subtitle = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "node buildbox",
            IncludeBackground = true,
        });
        var opaque = await coordinator.SearchAsync(new SessionQuery
        {
            Search = opaqueId,
            IncludeBackground = true,
        });
        var unsafeFields = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "secretneedle",
            IncludeBackground = true,
        });
        var hiddenRawFields = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "hiddenneedle",
            IncludeBackground = true,
        });

        Assert.Equal(3, terminal.Sessions.Count);
        Assert.Equal(
            terminal.Sessions.Count,
            terminal.Sessions.Select(session => session.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("global", Assert.Single(global.Sessions).Key);
        Assert.Equal("agent:ops:tui-safe", Assert.Single(subtitle.Sessions).Key);
        Assert.Empty(opaque.Sessions);
        Assert.Empty(unsafeFields.Sessions);
        Assert.Empty(hiddenRawFields.Sessions);
    }

    [Fact]
    public async Task LegacySearch_IsCappedPinsSelectionAndClearRestoresRecent()
    {
        var rows = Enumerable.Range(0, 2500)
            .Select(index => new SessionInfo
            {
                Key = $"agent:main:{index}",
                DisplayName = index % 2 == 0 ? "Needle" : "Other",
            })
            .ToArray();
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions = rows,
                IsLegacyResponse = true,
            }),
            TimeSpan.Zero);
        var recentQuery = new SessionQuery { IncludeBackground = true };
        var recent = await coordinator.LoadRecentAsync(recentQuery);
        var pinned = new SessionInfo { Key = "agent:main:selected", Label = "Selected" };

        var search = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "needle",
            IncludeBackground = true,
            PinnedSessions = [pinned],
        });
        var restored = coordinator.ClearSearch(recentQuery);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, recent.Sessions.Count);
        Assert.Equal(1001, search.Sessions.Count);
        Assert.Contains(search.Sessions, session => session.Key == pinned.Key);
        Assert.Equal(
            recent.Sessions.Select(session => session.Key),
            restored.Sessions.Select(session => session.Key));
        Assert.Equal(SessionSearchExecutionMode.None, restored.SearchExecutionMode);
    }

    [Fact]
    public async Task ServerSearch_ReportsServerExecutionMode()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery { Search = "one" });

        Assert.Equal(SessionSearchExecutionMode.Server, snapshot.SearchExecutionMode);
    }

    [Fact]
    public async Task LegacyUnboundedResponse_IsCappedDefensively()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions = Enumerable.Range(0, 2500).Select(Session).ToArray(),
                IsLegacyResponse = true,
            }),
            TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, snapshot.Sessions.Count);
        Assert.Equal(1, snapshot.PagesRead);
        Assert.True(snapshot.IsLegacyResponse);
    }

    private static SessionInfo Session(int index) => new() { Key = $"agent:main:{index}" };

    private static SessionListResult Page(
        IEnumerable<SessionInfo> sessions,
        int offset,
        int? nextOffset,
        bool hasMore)
    {
        var rows = sessions.ToArray();
        return new SessionListResult
        {
            Sessions = rows,
            Count = rows.Length,
            TotalCount = 2000,
            LimitApplied = 100,
            Offset = offset,
            NextOffset = nextOffset,
            HasMore = hasMore,
        };
    }
}
