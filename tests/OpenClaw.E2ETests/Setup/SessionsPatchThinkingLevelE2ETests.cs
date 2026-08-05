using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

[CollectionDefinition("Reasoning Gateway E2E", DisableParallelization = true)]
public sealed class ReasoningGatewayE2ECollection : ICollectionFixture<E2ESetupFixture> { }

[Collection("Reasoning Gateway E2E")]
public sealed class SessionsPatchThinkingLevelE2ETests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private readonly E2ESetupFixture _fixture;

    public SessionsPatchThinkingLevelE2ETests(E2ESetupFixture fixture)
    {
        _fixture = fixture;

        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public async Task RealGateway_ThinkingLevelOffThenDefault_PersistsCanonicalNull()
    {
        var gateway = _fixture.ReadActiveGatewayRecord();
        var credentials = _fixture.ReadActiveGatewayCredentialState();
        Assert.False(string.IsNullOrWhiteSpace(gateway.SharedGatewayToken));

        using var client = new OpenClawGatewayClient(
            gateway.GatewayUrl,
            gateway.SharedGatewayToken!,
            identityPath: credentials.IdentityDir);

        string? sessionKey = null;
        SessionInfo? original = null;
        var mutated = false;
        Exception? testFailure = null;
        var cleanupFailures = new List<Exception>();

        try
        {
            await client.ConnectAsync();
            await WaitForAsync(
                () => client.HasHandshakeSnapshot && !string.IsNullOrWhiteSpace(client.MainSessionKey),
                "typed Gateway client handshake");

            sessionKey = client.MainSessionKey!;
            original = (await client.RequestSessionsSnapshotAsync()).SingleOrDefault(
                candidate => string.Equals(candidate.Key, sessionKey, StringComparison.Ordinal));

            mutated = true;
            var offResult = await client.PatchSessionDetailedAsync(
                sessionKey,
                new SessionPatch { ThinkingLevel = "off" });
            Assert.True(offResult.Ok, offResult.Error);

            var offSnapshot = await ReadSessionAsync(client, sessionKey);
            Assert.Equal("off", offSnapshot.ThinkingLevel);
            await WaitForTrayThinkingLevelAsync(sessionKey, "off");

            var clearResult = await client.PatchSessionDetailedAsync(
                sessionKey,
                new SessionPatch { ThinkingLevel = SessionPatch.Clear });
            Assert.True(clearResult.Ok, clearResult.Error);

            var defaultSnapshot = await ReadSessionAsync(client, sessionKey);
            Assert.Null(defaultSnapshot.ThinkingLevel);

            using (var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.reconnect"))
            {
                Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
            }
            await _fixture.WaitForConnectionReady();
            await WaitForTrayThinkingLevelAsync(sessionKey, expected: null);

            Console.WriteLine(
                "[E2E] Real WSL Gateway persisted off -> null (Default), and the isolated Tray " +
                "confirmed canonical null after a current-generation reconnect snapshot.");
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            if (mutated && sessionKey is not null)
            {
                try
                {
                    var restorePatch = new SessionPatch
                    {
                        ThinkingLevel = original?.ThinkingLevel is null
                            ? SessionPatch.Clear
                            : original.ThinkingLevel
                    };
                    var restoreResult = await client.PatchSessionDetailedAsync(sessionKey, restorePatch);
                    if (!restoreResult.Ok)
                    {
                        throw new InvalidOperationException(
                            $"Failed to restore the original thinking-level state: {restoreResult.Error}");
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }

            try
            {
                await client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(ex);
            }
        }

        if (testFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, testFailure);
                throw new AggregateException("The E2E proof and its cleanup both failed.", cleanupFailures);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testFailure).Throw();
        }

        if (cleanupFailures.Count > 0)
            throw new AggregateException("The E2E proof cleanup failed.", cleanupFailures);
    }

    private static async Task<SessionInfo> ReadSessionAsync(
        OpenClawGatewayClient client,
        string sessionKey)
    {
        var sessions = await client.RequestSessionsSnapshotAsync();
        var session = sessions.SingleOrDefault(
            candidate => string.Equals(candidate.Key, sessionKey, StringComparison.Ordinal));
        return Assert.IsType<SessionInfo>(session);
    }

    private async Task WaitForTrayThinkingLevelAsync(string sessionKey, string? expected)
    {
        string? lastObserved = "<session missing>";
        var deadline = DateTime.UtcNow.Add(s_timeout);

        while (DateTime.UtcNow < deadline)
        {
            using var snapshot = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.chat.snapshot",
                new { sessionKey });
            var root = snapshot.RootElement;
            if (!TryGetPropertyIgnoreCase(root, "connectionStatus", out var connectionStatus) ||
                connectionStatus.GetString() is not ("Connected" or "Ready") ||
                !TryGetPropertyIgnoreCase(root, "composeTarget", out var composeTarget) ||
                !TryGetPropertyIgnoreCase(composeTarget, "isReady", out var isReady) ||
                !isReady.GetBoolean() ||
                !TryGetPropertyIgnoreCase(composeTarget, "sessionKey", out var composeSessionKey) ||
                !string.Equals(composeSessionKey.GetString(), sessionKey, StringComparison.Ordinal) ||
                !TryGetPropertyIgnoreCase(root, "threads", out var threads) ||
                threads.ValueKind != JsonValueKind.Array)
            {
                lastObserved = "<snapshot not ready>";
                await Task.Delay(250);
                continue;
            }

            var thread = threads.EnumerateArray().FirstOrDefault(
                candidate =>
                    TryGetPropertyIgnoreCase(candidate, "id", out var id) &&
                    string.Equals(id.GetString(), sessionKey, StringComparison.Ordinal));

            if (thread.ValueKind != JsonValueKind.Undefined &&
                TryGetPropertyIgnoreCase(thread, "thinkingLevel", out var thinkingLevel))
            {
                lastObserved = thinkingLevel.ValueKind == JsonValueKind.Null
                    ? null
                    : thinkingLevel.GetString();
                if (string.Equals(lastObserved, expected, StringComparison.Ordinal))
                    return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Tray did not converge to thinkingLevel '{expected ?? "<null>"}'. " +
            $"Last observed: '{lastObserved ?? "<null>"}'.");
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.Add(s_timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
