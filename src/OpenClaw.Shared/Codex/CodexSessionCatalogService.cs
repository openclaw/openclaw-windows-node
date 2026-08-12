using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared.Codex;

internal interface ICodexSessionCatalogClient
{
    Task<JsonElement> ListThreadsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default);

    Task<JsonElement> ListThreadTurnsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default);
}

internal sealed class CodexSessionCatalogClientAdapter : ICodexSessionCatalogClient
{
    private readonly CodexAppServerClient _client;

    internal CodexSessionCatalogClientAdapter(CodexAppServerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<JsonElement> ListThreadsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        _client.ListThreadsAsync(parameters, cancellationToken);

    public Task<JsonElement> ListThreadTurnsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        _client.ListThreadTurnsAsync(parameters, cancellationToken);
}

internal sealed class CodexSessionCatalogValidationException : Exception
{
    public CodexSessionCatalogValidationException(string message)
        : base(message)
    {
    }
}

internal sealed class CodexSessionCatalogService
{
    internal const int DefaultPageLimit = 50;
    internal const int MaxPageLimit = 100;
    internal const int DefaultTranscriptPageLimit = 20;
    internal const int MaxTranscriptPageLimit = 50;
    internal const int MaxCursorLength = 4096;
    internal const int MaxSearchLength = 500;
    internal const int MaxCwdLength = 4096;
    internal const int MaxSessionIdLength = 256;
    internal const int MaxSessionNameLength = 500;
    internal const int MaxSessionPreviewLength = 500;
    internal const int MaxMetadataLength = 500;
    internal const int MaxActiveFlags = 16;
    internal const int MaxActiveFlagLength = 128;
    internal const int MaxTitleSearchPages = 20;
    internal const int MaxEligibilityPages = 100;
    internal const int EligibilityPageLimit = 10;
    internal const int MaxTranscriptTextLength = 1_000_000;
    internal const int MaxTranscriptPageBytes = 20 * 1024 * 1024;
    internal const int MaxJsonRpcEnvelopeBytes = 4 * 1024;
    internal const int MaxCatalogOperationOverheadBytes = 4 * 1024;

    private static readonly HashSet<string> InteractiveStringSources =
        new(StringComparer.Ordinal) { "cli", "vscode" };

    private static readonly HashSet<string> InteractiveCustomSources =
        new(StringComparer.Ordinal) { "atlas", "chatgpt" };

    // This mirrors the current thread/turns/list contract. Keep this finite:
    // App Server is an untrusted versioned boundary and must not grow the
    // Windows catalog payload merely by adding fields upstream.
    private static readonly HashSet<string> TranscriptTurnFields =
        new(StringComparer.Ordinal) { "id", "status", "createdAt", "updatedAt", "itemsView" };

    private static readonly HashSet<string> TranscriptItemFields =
        new(StringComparer.Ordinal)
        {
            "id", "type", "title", "status", "name", "tool", "server", "command", "cwd", "query",
            "arguments", "result", "error", "exitCode", "durationMs", "aggregatedOutput", "text",
            "contentItems", "content", "clientId", "summary", "commandActions", "changes",
        };

    private readonly ICodexSessionCatalogClient _client;

    internal CodexSessionCatalogService(CodexAppServerClient client)
        : this(new CodexSessionCatalogClientAdapter(client))
    {
    }

    internal CodexSessionCatalogService(ICodexSessionCatalogClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal async Task<JsonElement> ListThreadsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var request = ReadListRequest(arguments);
        if (request.SearchTerm is null)
        {
            var response = await _client.ListThreadsAsync(
                CreateThreadListParameters(request),
                cancellationToken).ConfigureAwait(false);
            return ProjectThreadPage(response, searchTerm: null, request.Limit);
        }

        return await SearchThreadPagesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<JsonElement> ListThreadTurnsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var request = ReadTranscriptRequest(arguments);
        await RequireFreshEligibilityAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);

        var response = await _client.ListThreadTurnsAsync(
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["threadId"] = request.ThreadId,
                ["cursor"] = request.Cursor,
                ["limit"] = request.Limit,
                ["sortDirection"] = "desc",
                ["itemsView"] = "full",
            }.Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value)),
            cancellationToken).ConfigureAwait(false);
        return ProjectTranscriptPage(response);
    }

    private async Task<JsonElement> SearchThreadPagesAsync(
        ListRequest request,
        CancellationToken cancellationToken)
    {
        var sessions = new List<JsonElement>(request.Limit);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = request.Cursor;
        string? nextCursor = null;
        string? backwardsCursor = null;
        for (var pageIndex = 0; pageIndex < MaxTitleSearchPages; pageIndex++)
        {
            var pageRequest = request with
            {
                Cursor = cursor,
                Limit = request.Limit - sessions.Count,
            };
            var response = await _client.ListThreadsAsync(
                CreateThreadListParameters(pageRequest),
                cancellationToken).ConfigureAwait(false);
            var page = ProjectThreadPage(response, request.SearchTerm, pageRequest.Limit);
            if (pageIndex == 0
                && page.TryGetProperty("backwardsCursor", out var backwards)
                && backwards.ValueKind == JsonValueKind.String)
            {
                backwardsCursor = backwards.GetString();
            }
            sessions.AddRange(page.GetProperty("sessions")
                .EnumerateArray()
                .Take(request.Limit - sessions.Count)
                .Select(value => value.Clone()));
            nextCursor = page.TryGetProperty("nextCursor", out var next)
                && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (sessions.Count >= request.Limit || nextCursor is null)
                break;
            if (!seenCursors.Add(nextCursor))
                throw new InvalidDataException("Repeated Codex App Server search cursor.");
            cursor = nextCursor;
        }

        var result = new Dictionary<string, object?> { ["sessions"] = sessions };
        if (nextCursor is not null)
            result["nextCursor"] = nextCursor;
        if (backwardsCursor is not null)
            result["backwardsCursor"] = backwardsCursor;
        return JsonSerializer.SerializeToElement(result);
    }

    private async Task RequireFreshEligibilityAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageIndex = 0; pageIndex < MaxEligibilityPages; pageIndex++)
        {
            var response = await _client.ListThreadsAsync(
                CreateThreadListParameters(
                    new ListRequest(cursor, EligibilityPageLimit, null, null),
                    useStateDbOnly: false),
                cancellationToken).ConfigureAwait(false);
            var page = ProjectThreadPage(response, searchTerm: null, EligibilityPageLimit);
            if (page.GetProperty("sessions").EnumerateArray().Any(session =>
                session.GetProperty("threadId").GetString() == threadId))
            {
                return;
            }
            var nextCursor = page.TryGetProperty("nextCursor", out var next)
                && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (nextCursor is null)
            {
                throw new CodexSessionCatalogValidationException(
                    "Codex session is not a non-archived interactive Codex session");
            }
            if (!seenCursors.Add(nextCursor))
            {
                throw new CodexSessionCatalogValidationException(
                    "Codex session eligibility could not be verified");
            }
            cursor = nextCursor;
        }
        throw new CodexSessionCatalogValidationException(
            "Codex session eligibility could not be verified");
    }

    private static ListRequest ReadListRequest(JsonElement arguments)
    {
        var values = ReadObject(arguments, "Codex session catalog parameters must be an object");
        RejectUnknownFields(values, new HashSet<string>(StringComparer.Ordinal)
        {
            "cursor", "limit", "searchTerm", "cwd",
        });
        return new ListRequest(
            ReadOptionalString(values, "cursor", MaxCursorLength),
            ReadLimit(values, "limit", DefaultPageLimit, MaxPageLimit),
            ReadOptionalString(values, "searchTerm", MaxSearchLength),
            ReadOptionalString(values, "cwd", MaxCwdLength));
    }

    private static TranscriptRequest ReadTranscriptRequest(JsonElement arguments)
    {
        var values = ReadObject(arguments, "Codex session read parameters must be an object");
        RejectUnknownFields(values, new HashSet<string>(StringComparer.Ordinal)
        {
            "threadId", "cursor", "limit",
        });
        var threadId = ReadOptionalString(values, "threadId", MaxSessionIdLength);
        if (threadId is null)
            throw new CodexSessionCatalogValidationException("threadId is required");
        if (!Guid.TryParseExact(threadId, "D", out _))
            throw new CodexSessionCatalogValidationException("threadId must be a UUID");
        return new TranscriptRequest(
            threadId,
            ReadOptionalString(values, "cursor", MaxCursorLength),
            ReadLimit(values, "limit", DefaultTranscriptPageLimit, MaxTranscriptPageLimit));
    }

    private static JsonElement CreateThreadListParameters(
        ListRequest request,
        bool useStateDbOnly = true) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["cursor"] = request.Cursor,
            ["limit"] = request.Limit,
            ["modelProviders"] = Array.Empty<string>(),
            ["sortKey"] = "updated_at",
            ["sortDirection"] = "desc",
            ["archived"] = false,
            ["useStateDbOnly"] = useStateDbOnly ? true : null,
            ["cwd"] = request.Cwd,
        }.Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value));

    private static JsonElement ProjectThreadPage(
        JsonElement response,
        string? searchTerm,
        int maxSessions)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() > MaxPageLimit)
        {
            throw new InvalidDataException("Invalid Codex App Server thread page.");
        }

        var sessions = new List<Dictionary<string, object?>>();
        foreach (var thread in data.EnumerateArray())
        {
            var session = ProjectThread(thread);
            if (session is null)
                continue;
            if (searchTerm is not null
                && (!session.TryGetValue("name", out var name)
                    || name is not string title
                    || !title.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }
            sessions.Add(session);
            if (sessions.Count == maxSessions)
                break;
        }

        var result = new Dictionary<string, object?> { ["sessions"] = sessions };
        CopyCursor(response, result, "nextCursor");
        CopyCursor(response, result, "backwardsCursor");
        return JsonSerializer.SerializeToElement(result);
    }

    private static Dictionary<string, object?>? ProjectThread(JsonElement thread)
    {
        if (thread.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid Codex App Server thread.");
        if (thread.TryGetProperty("archived", out var archived)
            && archived.ValueKind == JsonValueKind.True)
        {
            return null;
        }
        var source = ReadInteractiveSource(thread);
        if (source is null)
            return null;
        if (!thread.TryGetProperty("id", out var idValue)
            || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(idValue.GetString(), "D", out _))
        {
            throw new InvalidDataException("Invalid Codex App Server thread id.");
        }

        var result = new Dictionary<string, object?>
        {
            ["threadId"] = idValue.GetString(),
            ["status"] = ReadStatus(thread, out var activeFlags),
            ["archived"] = false,
        };
        CopyBoundedString(thread, result, "sessionId", "sessionId", MaxSessionIdLength);
        CopyNameAndFallback(thread, result);
        CopyBoundedString(thread, result, "cwd", "cwd", MaxCwdLength);
        if (activeFlags.Count > 0)
            result["activeFlags"] = activeFlags;
        CopyFiniteNumber(thread, result, "createdAt");
        CopyFiniteNumber(thread, result, "updatedAt");
        CopyFiniteNumber(thread, result, "recencyAt", allowNull: true);
        result["source"] = source;
        CopyBoundedString(thread, result, "modelProvider", "modelProvider", MaxMetadataLength, truncate: true);
        CopyBoundedString(thread, result, "cliVersion", "cliVersion", MaxMetadataLength, truncate: true);
        if (thread.TryGetProperty("gitInfo", out var gitInfo) && gitInfo.ValueKind == JsonValueKind.Object)
            CopyBoundedString(gitInfo, result, "branch", "gitBranch", MaxMetadataLength, truncate: true);
        return result;
    }

    private static JsonElement ProjectTranscriptPage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() > MaxTranscriptPageLimit)
        {
            throw new InvalidDataException("Invalid Codex App Server transcript page.");
        }

        var turns = data.EnumerateArray().Select(ProjectTranscriptTurn).ToArray();
        var result = new Dictionary<string, object?> { ["data"] = turns };
        CopyCursor(response, result, "nextCursor");
        CopyCursor(response, result, "backwardsCursor");
        var page = JsonSerializer.SerializeToElement(result);
        if (Encoding.UTF8.GetByteCount(page.GetRawText()) > MaxTranscriptPageBytes)
            throw new InvalidDataException("Codex App Server transcript page exceeds the byte limit.");
        return page;
    }

    private static Dictionary<string, object?> ProjectTranscriptTurn(JsonElement turn)
    {
        if (turn.ValueKind != JsonValueKind.Object
            || !turn.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Invalid Codex App Server transcript page.");
        }

        var projected = new Dictionary<string, object?>();
        foreach (var property in turn.EnumerateObject())
        {
            if (!TranscriptTurnFields.Contains(property.Name))
                continue;
            ValidateTranscriptText(property.Value);
            projected[property.Name] = property.Value.Clone();
        }

        projected["items"] = items.EnumerateArray().Select(ProjectTranscriptItem).ToArray();
        return projected;
    }

    private static Dictionary<string, object?> ProjectTranscriptItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid Codex App Server transcript page.");

        var projected = new Dictionary<string, object?>();
        foreach (var property in item.EnumerateObject())
        {
            if (!TranscriptItemFields.Contains(property.Name))
                continue;
            ValidateTranscriptText(property.Value);
            projected[property.Name] = property.Value.Clone();
        }
        return projected;
    }

    private static void ValidateTranscriptText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (value.GetString()!.Length > MaxTranscriptTextLength)
                    throw new InvalidDataException("Codex App Server transcript text exceeds the limit.");
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateTranscriptText(item);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateTranscriptText(property.Value);
                break;
        }
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement value, string error)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new CodexSessionCatalogValidationException(error);
        return value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.Ordinal);
    }

    private static void RejectUnknownFields(
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlySet<string> allowed)
    {
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null)
        {
            throw new CodexSessionCatalogValidationException(
                $"unknown Codex session catalog parameter: {SanitizeErrorToken(unknown)}");
        }
    }

    private static string? ReadOptionalString(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        int maxLength)
    {
        if (!values.TryGetValue(key, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new CodexSessionCatalogValidationException($"{key} must be a string");
        var text = value.GetString()!.Trim();
        if (text.Length == 0)
            return null;
        if (text.Length > maxLength)
            throw new CodexSessionCatalogValidationException($"{key} must be at most {maxLength} characters");
        return text;
    }

    private static int ReadLimit(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        int defaultValue,
        int maxValue)
    {
        if (!values.TryGetValue(key, out var value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var limit)
            || limit < 1
            || limit > maxValue)
        {
            throw new CodexSessionCatalogValidationException(
                $"{key} must be an integer from 1 to {maxValue}");
        }
        return limit;
    }

    private static string? ReadInteractiveSource(JsonElement thread)
    {
        if (!thread.TryGetProperty("source", out var source))
            return null;
        if (source.ValueKind == JsonValueKind.String)
        {
            var value = source.GetString();
            return value is not null && InteractiveStringSources.Contains(value) ? value : null;
        }
        if (source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("custom", out var custom)
            && custom.ValueKind == JsonValueKind.String)
        {
            var value = custom.GetString();
            return value is not null && InteractiveCustomSources.Contains(value) ? value : null;
        }
        return null;
    }

    private static string ReadStatus(JsonElement thread, out List<string> activeFlags)
    {
        activeFlags = [];
        if (!thread.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
            return "notLoaded";
        if (!status.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return "notLoaded";
        var value = type.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            throw new InvalidDataException("Invalid Codex App Server thread status.");
        if (value == "active"
            && status.TryGetProperty("activeFlags", out var flags)
            && flags.ValueKind == JsonValueKind.Array)
        {
            foreach (var flag in flags.EnumerateArray().Take(MaxActiveFlags))
            {
                if (flag.ValueKind != JsonValueKind.String)
                    continue;
                var normalized = BoundedString(flag.GetString(), MaxActiveFlagLength, truncate: false);
                if (normalized is not null)
                    activeFlags.Add(normalized);
            }
        }
        return value;
    }

    private static void CopyNameAndFallback(
        JsonElement thread,
        IDictionary<string, object?> result)
    {
        if (thread.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Null)
            result["name"] = null;
        var normalizedName = thread.TryGetProperty("name", out name) && name.ValueKind == JsonValueKind.String
            ? BoundedString(name.GetString(), MaxSessionNameLength, truncate: true)
            : null;
        if (normalizedName is not null)
        {
            result["name"] = normalizedName;
            return;
        }
        if (!thread.TryGetProperty("preview", out var preview) || preview.ValueKind != JsonValueKind.String)
            return;
        var sanitized = SanitizePreview(preview.GetString()!);
        var fallback = BoundedString(sanitized, MaxSessionPreviewLength, truncate: true);
        if (fallback is not null)
            result["fallbackName"] = fallback;
    }

    private static void CopyBoundedString(
        JsonElement source,
        IDictionary<string, object?> destination,
        string sourceName,
        string destinationName,
        int maxLength,
        bool truncate = false)
    {
        if (!source.TryGetProperty(sourceName, out var value) || value.ValueKind != JsonValueKind.String)
            return;
        var normalized = BoundedString(value.GetString(), maxLength, truncate);
        if (normalized is not null)
            destination[destinationName] = normalized;
    }

    private static void CopyFiniteNumber(
        JsonElement source,
        IDictionary<string, object?> destination,
        string name,
        bool allowNull = false)
    {
        if (!source.TryGetProperty(name, out var value))
            return;
        if (allowNull && value.ValueKind == JsonValueKind.Null)
        {
            destination[name] = null;
            return;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            destination[name] = value.Clone();
    }

    private static void CopyCursor(
        JsonElement source,
        IDictionary<string, object?> destination,
        string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid Codex App Server cursor.");
        var cursor = value.GetString();
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxCursorLength)
            throw new InvalidDataException("Invalid Codex App Server cursor.");
        destination[name] = cursor;
    }

    private static string? BoundedString(string? value, int maxLength, bool truncate)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        if (normalized.Length <= maxLength)
            return normalized;
        return truncate ? TruncateUtf16Safe(normalized, maxLength) : null;
    }

    private static string TruncateUtf16Safe(string value, int maxLength)
    {
        var length = maxLength;
        if (length > 0
            && length < value.Length
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }
        return value[..length];
    }

    private static string SanitizePreview(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\u001b')
            {
                SkipTerminalEscape(value, ref index);
                continue;
            }
            if (character == '\u009b')
            {
                SkipControlSequence(value, ref index);
                continue;
            }
            if (character == '\u009d')
            {
                SkipOperatingSystemCommand(value, ref index);
                continue;
            }
            var whitespace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (whitespace)
            {
                if (!previousWhitespace)
                    builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
            previousWhitespace = whitespace;
        }
        return builder.ToString().Trim();
    }

    private static void SkipTerminalEscape(string value, ref int index)
    {
        if (index + 1 >= value.Length)
            return;
        var introducer = value[++index];
        if (introducer == '[')
        {
            SkipControlSequence(value, ref index);
            return;
        }
        if (introducer != ']')
            return;
        SkipOperatingSystemCommand(value, ref index);
    }

    private static void SkipControlSequence(string value, ref int index)
    {
        while (index + 1 < value.Length)
        {
            var candidate = value[++index];
            if (candidate is >= '@' and <= '~')
                return;
        }
    }

    private static void SkipOperatingSystemCommand(string value, ref int index)
    {
        while (index + 1 < value.Length)
        {
            var candidate = value[++index];
            if (candidate is '\a' or '\u009c')
                return;
            if (candidate == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')
            {
                index++;
                return;
            }
        }
    }

    private static string SanitizeErrorToken(string value)
    {
        var sanitized = new string(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-').Take(64).ToArray());
        return sanitized.Length > 0 ? sanitized : "unknown";
    }

    private sealed record ListRequest(string? Cursor, int Limit, string? SearchTerm, string? Cwd);

    private sealed record TranscriptRequest(string ThreadId, string? Cursor, int Limit);
}
