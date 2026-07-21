using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

// ─────────────────────────────────────────────────────────────────────────────
// Gateway protocol client APIs.
//
// Typed client methods for the richer gateway protocol, matching the canonical
// openclaw/openclaw schemas exactly:
//   • commands.list             — command catalog
//   • sessions.create           — distinct session creation
//   • sessions.patch            — extended per-session field set
//   • sessions.files.list/get   — workspace file rail + browser (param: sessionKey)
//   • sessions.compaction.*     — compaction checkpoints (param: key, checkpointId)
//
// Backwards compatibility: read/list/get methods detect an "unknown method"
// error from an older gateway and return a typed result with IsSupported = false
// (logged at warn) instead of throwing. Genuine protocol errors are NOT
// swallowed: read methods propagate the gateway error (e.g. not-found / too-large
// on sessions.files.get), and mutation methods surface it in the result's Error
// field. Lifecycle RPCs convert request timeouts into action-specific results.
//
// Parsing is factored into internal static methods so the JSON contract is unit
// testable directly (InternalsVisibleTo OpenClaw.Shared.Tests), reusing the
// private static JsonElement helpers declared in OpenClawGatewayClient.cs.
// ─────────────────────────────────────────────────────────────────────────────
public partial class OpenClawGatewayClient
{
    // ── sessions.reset ──

    public async Task<SessionResetResult> ResetSessionDetailedAsync(
        string key,
        int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new SessionResetResult
            {
                Ok = false,
                Error = "Session key is required"
            };
        }
        if (!IsConnected)
        {
            return new SessionResetResult
            {
                Ok = false,
                Error = "Gateway connection is not open"
            };
        }

        try
        {
            var payload = await SendWizardRequestAsync(
                "sessions.reset",
                new { key },
                timeoutMs).ConfigureAwait(false);
            return ParseSessionResetResult(payload);
        }
        catch (TimeoutException ex)
        {
            _logger.Warn($"sessions.reset timed out: {ex.Message}");
            return CreateSessionResetTimeoutResult();
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"sessions.reset failed: {ex.Message}");
            return new SessionResetResult
            {
                Ok = false,
                Error = ex.Message
            };
        }
    }

    internal static SessionResetResult CreateSessionResetTimeoutResult() => new()
    {
        Ok = false,
        Error = "The gateway did not respond before the reset timed out. Refresh the session before trying again."
    };

    internal static SessionResetResult ParseSessionResetResult(JsonElement payload)
    {
        var ok = !payload.TryGetProperty("ok", out var okElement) ||
                 okElement.ValueKind == JsonValueKind.True;
        var reason = GetString(payload, "reason");
        return new SessionResetResult
        {
            Ok = ok,
            Key = GetString(payload, "key"),
            Reason = reason,
            Error = ok ? null : reason ?? "The gateway could not reset the session."
        };
    }

    // ── sessions.compact ──

    public async Task<SessionCompactResult> CompactSessionDetailedAsync(
        string key,
        int timeoutMs = 120000)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new SessionCompactResult
            {
                Ok = false,
                Error = "Session key is required"
            };
        }
        if (!IsConnected)
        {
            return new SessionCompactResult
            {
                Ok = false,
                Error = "Gateway connection is not open"
            };
        }

        try
        {
            var payload = await SendWizardRequestAsync(
                "sessions.compact",
                new { key },
                timeoutMs).ConfigureAwait(false);
            return ParseSessionCompactResult(payload);
        }
        catch (TimeoutException ex)
        {
            _logger.Warn($"sessions.compact timed out: {ex.Message}");
            return CreateSessionCompactTimeoutResult();
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"sessions.compact failed: {ex.Message}");
            return new SessionCompactResult
            {
                Ok = false,
                Error = ex.Message
            };
        }
    }

    internal static SessionCompactResult CreateSessionCompactTimeoutResult() => new()
    {
        Ok = false,
        Error = "The gateway did not respond before compaction timed out. Refresh the conversation to check whether compaction completed."
    };

    internal static SessionCompactResult ParseSessionCompactResult(JsonElement payload)
    {
        var ok = !payload.TryGetProperty("ok", out var okElement) ||
                 okElement.ValueKind == JsonValueKind.True;
        var compacted = payload.TryGetProperty("compacted", out var compactedElement) &&
                        compactedElement.ValueKind == JsonValueKind.True;
        long? tokensBefore = null;
        long? tokensAfter = null;
        if (payload.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("tokensBefore", out var before) &&
                before.TryGetInt64(out var beforeValue))
            {
                tokensBefore = beforeValue;
            }
            if (result.TryGetProperty("tokensAfter", out var after) &&
                after.TryGetInt64(out var afterValue))
            {
                tokensAfter = afterValue;
            }
        }

        var reason = GetString(payload, "reason");
        return new SessionCompactResult
        {
            Ok = ok,
            Key = GetString(payload, "key"),
            Compacted = compacted,
            Reason = reason,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            Error = ok ? null : reason ?? "The gateway could not compact the session."
        };
    }

    // ── sessions.create ──

    /// <summary>
    /// Creates a distinct gateway session. Unsupported gateways return a typed
    /// result with <see cref="SessionCreateResult.IsSupported"/> = false.
    /// Other gateway errors are surfaced in <see cref="SessionCreateResult.Error"/>.
    /// </summary>
    public async Task<SessionCreateResult> CreateSessionAsync(
        SessionCreateRequest request,
        int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConnected)
        {
            return new SessionCreateResult
            {
                Ok = false,
                Error = "Gateway connection is not open"
            };
        }

        try
        {
            JsonElement payload;
            try
            {
                payload = await SendWizardRequestAsync(
                    "sessions.create",
                    BuildSessionCreateParameters(request),
                    timeoutMs).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (
                request.SucceedsParent.HasValue &&
                IsLegacySucceedsParentError(ex.Message))
            {
                _logger.Warn("sessions.create retrying with legacy lifecycle parameters");
                payload = await SendWizardRequestAsync(
                    "sessions.create",
                    BuildSessionCreateParameters(request, legacyLifecycleFallback: true),
                    timeoutMs).ConfigureAwait(false);
            }

            return ParseSessionCreateResult(payload);
        }
        catch (TimeoutException ex)
        {
            _logger.Warn($"sessions.create timed out: {ex.Message}");
            return CreateSessionCreationTimeoutResult();
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            _logger.Warn("sessions.create unsupported on gateway");
            return new SessionCreateResult
            {
                Ok = false,
                IsSupported = false,
                Error = ex.Message
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"sessions.create failed: {ex.Message}");
            return new SessionCreateResult
            {
                Ok = false,
                Error = ex.Message
            };
        }
    }

    internal static SessionCreateResult CreateSessionCreationTimeoutResult() => new()
    {
        Ok = false,
        Error = "The gateway did not respond before session creation timed out. Check the session list to see whether a new session was created before trying again."
    };

    internal static Dictionary<string, object?> BuildSessionCreateParameters(
        SessionCreateRequest request,
        bool legacyLifecycleFallback = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Key))
            parameters["key"] = request.Key;
        if (!string.IsNullOrWhiteSpace(request.AgentId))
            parameters["agentId"] = request.AgentId;

        var unlinkLegacyParallelChild =
            legacyLifecycleFallback && request.SucceedsParent == false;
        if (!unlinkLegacyParallelChild)
        {
            parameters["emitCommandHooks"] = request.EmitCommandHooks;
            if (!string.IsNullOrWhiteSpace(request.ParentSessionKey))
                parameters["parentSessionKey"] = request.ParentSessionKey;
        }

        if (!legacyLifecycleFallback && request.SucceedsParent.HasValue)
            parameters["succeedsParent"] = request.SucceedsParent.Value;

        return parameters;
    }

    private static bool IsLegacySucceedsParentError(string? message) =>
        message?.Contains(
            "invalid sessions.create params",
            StringComparison.OrdinalIgnoreCase) == true &&
        message.Contains("succeedsParent", StringComparison.OrdinalIgnoreCase);

    internal static SessionCreateResult ParseSessionCreateResult(JsonElement payload)
    {
        var ok = !payload.TryGetProperty("ok", out var okElement) ||
                 okElement.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            return new SessionCreateResult
            {
                Ok = false,
                Error = GetString(payload, "reason") ?? "The gateway could not create a new session."
            };
        }

        var key = GetString(payload, "key")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return new SessionCreateResult
            {
                Ok = false,
                Error = "sessions.create returned no session key"
            };
        }

        return new SessionCreateResult
        {
            Ok = true,
            Key = key,
            SessionId = GetString(payload, "sessionId")
        };
    }

    // ── commands.list ──

    /// <summary>
    /// Fetches the gateway command catalog. When <paramref name="query"/> is
    /// supplied it is applied as a client-side filter over the full catalog.
    /// Returns a catalog with <see cref="CommandCatalog.IsSupported"/> = false
    /// when the gateway does not implement <c>commands.list</c>.
    /// Throws if the gateway connection is not open.
    /// </summary>
    public async Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null, int timeoutMs = 15000)
    {
        var payload = await TryRequestPayloadAsync("commands.list", null, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new CommandCatalog { IsSupported = false };

        var catalog = ParseCommandCatalog(payload.Value);
        if (query is { HasFilter: true })
            catalog = ApplyCommandQuery(catalog, query);
        return catalog;
    }

    internal static CommandCatalog ApplyCommandQuery(CommandCatalog catalog, CommandCatalogQuery query)
    {
        var filtered = catalog.Commands.Where(query.Matches).ToList();
        return new CommandCatalog
        {
            Commands = filtered,
            Categories = catalog.Categories,
            Scopes = catalog.Scopes,
            Sources = catalog.Sources,
            IsSupported = catalog.IsSupported
        };
    }

    internal static CommandCatalog ParseCommandCatalog(JsonElement payload)
    {
        var commandsArray = payload.ValueKind == JsonValueKind.Array
            ? payload
            : TryGetArray(payload, "commands") ?? default;

        var commands = new List<GatewayCommand>();
        if (commandsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in commandsArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var command = ParseCommand(item);
                if (!string.IsNullOrEmpty(command.Name))
                    commands.Add(command);
            }
        }

        // The gateway result has no facet lists; derive them so callers always
        // have the schema vocabulary available.
        return new CommandCatalog
        {
            Commands = commands,
            Categories = DistinctNonEmpty(commands.Select(c => c.Category)),
            Scopes = DistinctNonEmpty(commands.Select(c => c.Scope)),
            Sources = DistinctNonEmpty(commands.Select(c => c.Source)),
            IsSupported = true
        };
    }

    private static GatewayCommand ParseCommand(JsonElement item)
    {
        var args = new List<GatewayCommandArg>();
        if (item.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsEl.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.Object) continue;
                args.Add(ParseCommandArg(arg));
            }
        }

        return new GatewayCommand
        {
            Name = GetString(item, "name") ?? "",
            NativeName = GetString(item, "nativeName"),
            TextAliases = GetStringArrayList(item, "textAliases"),
            Description = GetString(item, "description"),
            Category = GetString(item, "category"),
            Source = GetString(item, "source"),
            Scope = GetString(item, "scope"),
            AcceptsArgs = GetOptionalBool(item, "acceptsArgs") ?? args.Count > 0,
            Args = args
        };
    }

    private static GatewayCommandArg ParseCommandArg(JsonElement arg)
    {
        var choices = new List<GatewayCommandArgChoice>();
        if (arg.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choicesEl.EnumerateArray())
            {
                if (choice.ValueKind == JsonValueKind.Object)
                {
                    // value is a (possibly empty) string per schema; only skip
                    // when the property is missing/non-string.
                    if (!choice.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
                        continue;
                    var value = valueEl.GetString() ?? "";
                    choices.Add(new GatewayCommandArgChoice
                    {
                        Value = value,
                        Label = GetString(choice, "label") ?? value
                    });
                }
                else if (choice.ValueKind == JsonValueKind.String)
                {
                    // Tolerate a bare-string choice form for forward/back compat.
                    var value = choice.GetString();
                    if (string.IsNullOrEmpty(value)) continue;
                    choices.Add(new GatewayCommandArgChoice { Value = value!, Label = value! });
                }
            }
        }

        return new GatewayCommandArg
        {
            Name = GetString(arg, "name") ?? "",
            Description = GetString(arg, "description"),
            Type = GetString(arg, "type"),
            Required = GetOptionalBool(arg, "required") ?? false,
            IsDynamic = GetOptionalBool(arg, "dynamic") ?? false,
            Choices = choices
        };
    }

    // ── sessions.patch (extended field set) ──

    /// <summary>
    /// Applies an extended <see cref="SessionPatch"/> to a session. Only the
    /// fields set on the patch are sent. Returns false (without sending) when
    /// the key is blank or the patch has no changes. Completion is observed via
    /// the existing <see cref="SessionCommandCompleted"/> event, like the legacy
    /// <see cref="PatchSessionAsync(string, string?, string?, string?)"/>.
    /// </summary>
    public Task<bool> PatchSessionAsync(string key, SessionPatch patch)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        if (patch is null || !patch.HasChanges) return Task.FromResult(false);
        return TrySendTrackedRequestAsync("sessions.patch", patch.ToPayload(key));
    }

    // ── sessions.files.list / sessions.files.get ──

    /// <summary>
    /// Lists files referenced by a session, optionally scoped to a sub-path or
    /// search (powering the workspace file rail / browser). Returns a result
    /// with <see cref="SessionFileList.IsSupported"/> = false when the gateway
    /// does not implement the method. Throws if the connection is not open.
    /// </summary>
    public async Task<SessionFileList> ListSessionFilesAsync(string key, string? path = null, string? search = null, int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new SessionFileList { Key = key ?? "" };

        var @params = new Dictionary<string, object?> { ["sessionKey"] = key };
        if (!string.IsNullOrEmpty(path)) @params["path"] = path;
        if (!string.IsNullOrEmpty(search)) @params["search"] = search;

        var payload = await TryRequestPayloadAsync("sessions.files.list", @params, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new SessionFileList { Key = key, IsSupported = false };

        return ParseSessionFileList(payload.Value, key);
    }

    /// <summary>
    /// Reads the content of a single session file. Returns a result with
    /// <see cref="SessionFileContent.IsSupported"/> = false when the gateway
    /// does not implement the method. The gateway returns an error for a missing
    /// or too-large file, which propagates as <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<SessionFileContent> GetSessionFileAsync(string key, string path, int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Session key is required", nameof(key));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("File path is required", nameof(path));

        var payload = await TryRequestPayloadAsync(
            "sessions.files.get", new { sessionKey = key, path }, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new SessionFileContent { Key = key, Path = path, IsSupported = false };

        return ParseSessionFileContent(payload.Value, key, path);
    }

    internal static SessionFileList ParseSessionFileList(JsonElement payload, string key)
    {
        var files = new List<SessionFileEntry>();
        var filesArray = TryGetArray(payload, "files");
        if (filesArray is { ValueKind: JsonValueKind.Array })
        {
            foreach (var item in filesArray.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var entry = ParseFileEntry(item);
                if (!string.IsNullOrEmpty(entry.Path) || !string.IsNullOrEmpty(entry.Name))
                    files.Add(entry);
            }
        }

        SessionFileBrowser? browser = null;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("browser", out var browserEl) &&
            browserEl.ValueKind == JsonValueKind.Object)
        {
            browser = ParseFileBrowser(browserEl);
        }

        return new SessionFileList
        {
            Key = FirstNonEmpty(GetStringSafe(payload, "sessionKey"), key) ?? key,
            Root = GetStringSafe(payload, "root"),
            Files = files,
            Browser = browser,
            IsSupported = true
        };
    }

    internal static SessionFileContent ParseSessionFileContent(JsonElement payload, string key, string path)
    {
        // sessions.files.get wraps the entry under "file".
        JsonElement file = default;
        var hasFile = payload.ValueKind == JsonValueKind.Object &&
                      payload.TryGetProperty("file", out file) &&
                      file.ValueKind == JsonValueKind.Object;

        if (!hasFile)
        {
            return new SessionFileContent { Key = key, Path = path, Missing = true, IsSupported = true };
        }

        return new SessionFileContent
        {
            Key = FirstNonEmpty(GetStringSafe(payload, "sessionKey"), key) ?? key,
            Root = GetStringSafe(payload, "root"),
            Path = FirstNonEmpty(GetString(file, "path"), path) ?? path,
            Name = GetString(file, "name"),
            Kind = GetString(file, "kind"),
            Content = GetString(file, "content"),
            Size = GetLongOrNull(file, "size"),
            UpdatedAt = ParseUnixTimestampMs(file, "updatedAtMs"),
            Missing = GetOptionalBool(file, "missing") ?? false,
            IsSupported = true
        };
    }

    private static SessionFileEntry ParseFileEntry(JsonElement item) => new()
    {
        Path = GetString(item, "path") ?? "",
        Name = GetString(item, "name") ?? "",
        Kind = GetString(item, "kind"),
        Missing = GetOptionalBool(item, "missing") ?? false,
        Size = GetLongOrNull(item, "size"),
        UpdatedAt = ParseUnixTimestampMs(item, "updatedAtMs"),
        Content = GetString(item, "content")
    };

    private static SessionFileBrowser ParseFileBrowser(JsonElement browserEl)
    {
        var entries = new List<SessionFileBrowserEntry>();
        if (browserEl.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entriesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                entries.Add(new SessionFileBrowserEntry
                {
                    Path = GetString(item, "path") ?? "",
                    Name = GetString(item, "name") ?? "",
                    Kind = GetString(item, "kind"),
                    SessionKind = GetString(item, "sessionKind"),
                    Size = GetLongOrNull(item, "size"),
                    UpdatedAt = ParseUnixTimestampMs(item, "updatedAtMs")
                });
            }
        }

        return new SessionFileBrowser
        {
            Path = GetString(browserEl, "path") ?? "",
            ParentPath = GetString(browserEl, "parentPath"),
            Search = GetString(browserEl, "search"),
            Entries = entries,
            Truncated = GetOptionalBool(browserEl, "truncated") ?? false
        };
    }

    // ── sessions.compaction.list / get / branch / restore ──

    /// <summary>
    /// Lists the compaction checkpoints for a session (powering the checkpoint
    /// UX). Returns a result with <see cref="SessionCompactionCheckpointList.IsSupported"/>
    /// = false when the gateway does not implement the method. Throws if the
    /// connection is not open.
    /// </summary>
    public async Task<SessionCompactionCheckpointList> ListCompactionCheckpointsAsync(string key, int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new SessionCompactionCheckpointList { Key = key ?? "" };

        var payload = await TryRequestPayloadAsync("sessions.compaction.list", new { key }, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new SessionCompactionCheckpointList { Key = key, IsSupported = false };

        return ParseCompactionCheckpointList(payload.Value, key);
    }

    /// <summary>
    /// Fetches a single compaction checkpoint's metadata. Returns a result with
    /// <see cref="SessionCompactionCheckpointResult.IsSupported"/> = false when
    /// the gateway does not implement the method.
    /// </summary>
    public async Task<SessionCompactionCheckpointResult> GetCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Session key is required", nameof(key));
        if (string.IsNullOrWhiteSpace(checkpointId))
            throw new ArgumentException("Checkpoint id is required", nameof(checkpointId));

        var payload = await TryRequestPayloadAsync(
            "sessions.compaction.get", new { key, checkpointId }, timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return new SessionCompactionCheckpointResult { Key = key, IsSupported = false };

        return ParseCompactionCheckpointResult(payload.Value, key);
    }

    /// <summary>
    /// Branches a new session from a compaction checkpoint. The gateway error
    /// (e.g. checkpoint not found) is surfaced in
    /// <see cref="SessionCompactionMutationResult.Error"/> rather than thrown;
    /// <see cref="SessionCompactionMutationResult.IsSupported"/> is false when
    /// the gateway does not implement the method.
    /// </summary>
    public Task<SessionCompactionMutationResult> BranchCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => MutateCompactionAsync("sessions.compaction.branch", key, checkpointId, timeoutMs);

    /// <summary>
    /// Restores a session to a compaction checkpoint. The gateway error is
    /// surfaced in <see cref="SessionCompactionMutationResult.Error"/> rather
    /// than thrown; <see cref="SessionCompactionMutationResult.IsSupported"/> is
    /// false when the gateway does not implement the method.
    /// </summary>
    public Task<SessionCompactionMutationResult> RestoreCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => MutateCompactionAsync("sessions.compaction.restore", key, checkpointId, timeoutMs);

    private async Task<SessionCompactionMutationResult> MutateCompactionAsync(
        string method, string key, string checkpointId, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new SessionCompactionMutationResult { Ok = false, CheckpointId = checkpointId, Error = "Session key is required" };
        if (string.IsNullOrWhiteSpace(checkpointId))
            return new SessionCompactionMutationResult { Ok = false, Key = key, Error = "Checkpoint id is required" };
        if (!IsConnected)
            return new SessionCompactionMutationResult { Ok = false, Key = key, CheckpointId = checkpointId, Error = "Gateway connection is not open" };

        try
        {
            var payload = await SendWizardRequestAsync(method, new { key, checkpointId }, timeoutMs).ConfigureAwait(false);
            return ParseCompactionMutation(payload, key, checkpointId);
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            _logger.Warn($"{method} unsupported on gateway");
            return new SessionCompactionMutationResult
            {
                Ok = false,
                Key = key,
                CheckpointId = checkpointId,
                Error = ex.Message,
                IsSupported = false
            };
        }
        catch (InvalidOperationException ex)
        {
            // Genuine gateway error (e.g. checkpoint not found) — surface it,
            // do not swallow.
            _logger.Warn($"{method} failed: {ex.Message}");
            return new SessionCompactionMutationResult
            {
                Ok = false,
                Key = key,
                CheckpointId = checkpointId,
                Error = ex.Message
            };
        }
    }

    internal static SessionCompactionCheckpointList ParseCompactionCheckpointList(JsonElement payload, string key)
    {
        var array = TryGetArray(payload, "checkpoints");
        var checkpoints = new List<SessionCompactionCheckpoint>();
        if (array is { ValueKind: JsonValueKind.Array })
        {
            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var checkpoint = ParseCompactionCheckpoint(item);
                checkpoints.Add(checkpoint);
            }
        }

        return new SessionCompactionCheckpointList
        {
            Key = FirstNonEmpty(GetStringSafe(payload, "key"), key) ?? key,
            Checkpoints = checkpoints,
            IsSupported = true
        };
    }

    internal static SessionCompactionCheckpointResult ParseCompactionCheckpointResult(JsonElement payload, string key)
    {
        SessionCompactionCheckpoint? checkpoint = null;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("checkpoint", out var cpEl) &&
            cpEl.ValueKind == JsonValueKind.Object)
        {
            var parsed = ParseCompactionCheckpoint(cpEl);
            if (!string.IsNullOrEmpty(parsed.Id))
                checkpoint = parsed;
        }

        return new SessionCompactionCheckpointResult
        {
            Key = FirstNonEmpty(GetStringSafe(payload, "key"), key) ?? key,
            Checkpoint = checkpoint,
            IsSupported = true
        };
    }

    internal static SessionCompactionMutationResult ParseCompactionMutation(JsonElement payload, string key, string checkpointId)
    {
        SessionCompactionCheckpoint? checkpoint = null;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("checkpoint", out var cpEl) &&
            cpEl.ValueKind == JsonValueKind.Object)
        {
            var parsed = ParseCompactionCheckpoint(cpEl);
            if (!string.IsNullOrEmpty(parsed.Id))
                checkpoint = parsed;
        }

        // branch returns { sourceKey, key=newBranchKey, ... }; restore returns { key, ... }.
        return new SessionCompactionMutationResult
        {
            Ok = true,
            Key = key,
            CheckpointId = checkpointId,
            SourceKey = GetStringSafe(payload, "sourceKey"),
            ResultSessionKey = GetStringSafe(payload, "key"),
            SessionId = GetStringSafe(payload, "sessionId"),
            Checkpoint = checkpoint,
            IsSupported = true
        };
    }

    private static SessionCompactionCheckpoint ParseCompactionCheckpoint(JsonElement item) => new()
    {
        Id = FirstNonEmpty(GetString(item, "checkpointId"), GetString(item, "id")) ?? "",
        SessionKey = GetString(item, "sessionKey"),
        SessionId = GetString(item, "sessionId"),
        CreatedAt = ParseUnixTimestampMs(item, "createdAt"),
        Reason = GetString(item, "reason"),
        TokensBefore = GetLongOrNull(item, "tokensBefore"),
        TokensAfter = GetLongOrNull(item, "tokensAfter"),
        Summary = GetString(item, "summary"),
        FirstKeptEntryId = GetString(item, "firstKeptEntryId")
    };

    // ── Shared request + JSON helpers ──

    /// <summary>
    /// Sends a request/response RPC and returns its payload, or <c>null</c> when
    /// the gateway reports the method is unknown (older gateway). All other
    /// failures (connection not open, gateway error, timeout) propagate.
    /// </summary>
    private async Task<JsonElement?> TryRequestPayloadAsync(string method, object? parameters, int timeoutMs)
    {
        try
        {
            return await SendWizardRequestAsync(method, parameters, timeoutMs).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            _logger.Warn($"{method} unsupported on gateway");
            return null;
        }
    }

    private static JsonElement? TryGetArray(JsonElement parent, string property)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }
        return null;
    }

    // The shared GetString/GetOptionalBool helpers call TryGetProperty, which
    // throws on a non-object element. A gateway payload is normally an object,
    // but be defensive about bare arrays / null payloads.
    private static string? GetStringSafe(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object ? GetString(parent, property) : null;

    private static long? GetLongOrNull(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt64(out var l)) return l;
        if (value.TryGetDouble(out var d)) return (long)d;
        return null;
    }

    private static List<string> GetStringArrayList(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object
            ? GetStringArray(parent, property).ToList()
            : new List<string>();

    private static List<string> DistinctNonEmpty(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }
}
