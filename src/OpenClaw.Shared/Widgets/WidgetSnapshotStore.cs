using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Widgets;

public sealed record WidgetSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("staleAfterMinutes")] int StaleAfterMinutes = 120,
    [property: JsonPropertyName("presentation")] WidgetPresentation? Presentation = null);

/// <summary>Companion writes snapshots atomically; the independently activated provider only reads them.</summary>
public sealed partial class WidgetSnapshotStore(string dataDirectory, IOpenClawLogger logger)
{
    public const int MaxWidgets = 20;
    public const int MaxSnapshotBytes = 524_288;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "widgets");
    private readonly object _gate = new();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidId();

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedId();

    public static void ValidateId(string id)
    {
        if (id is null || !ValidId().IsMatch(id) || ReservedId().IsMatch(id))
            throw new ArgumentException("Widget id must contain 1-64 lowercase letters, digits or hyphens.");
    }

    public static void Validate(WidgetSnapshot snapshot)
    {
        ValidateObservation(snapshot);
        WidgetPresentation.ValidatePublicUrl(snapshot.SourceUrl);
        snapshot.Presentation?.Validate();
    }

    private static void ValidateObservation(WidgetSnapshot snapshot)
    {
        ValidateId(snapshot.Id);
        ValidateText(snapshot.Title, 100, "title");
        ValidateText(snapshot.Value, 120, "value");
        ValidateText(snapshot.Detail, 600, "detail", allowEmpty: true);
        ValidateText(snapshot.SourceUrl, 2_048, "sourceUrl");
        if (snapshot.ObservedAt == default || snapshot.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("Widget observedAt must be a non-future observation timestamp.");
        if (snapshot.StaleAfterMinutes is < 5 or > 10_080)
            throw new ArgumentException("Widget staleAfterMinutes must be between 5 and 10080.");
    }

    public static WidgetSnapshot Parse(JsonElement args)
    {
        var snapshot = args.Deserialize<WidgetSnapshot>(JsonOptions)
            ?? throw new ArgumentException("A widget snapshot object is required.");
        Validate(snapshot);
        return snapshot;
    }

    public IReadOnlyList<WidgetSnapshot> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
                return [];
            var files = Directory.GetFiles(_directory, "*.json");
            if (files.Length > MaxWidgets)
                throw new InvalidDataException("Widget snapshot limit exceeded.");
            var snapshots = new List<WidgetSnapshot>();
            foreach (var path in files)
            {
                try { snapshots.Add(Read(path)); }
                catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    var safeId = ValidId().IsMatch(id) ? id : "(invalid id)";
                    logger.Warn($"Saved widget '{safeId}' rejected ({ex.GetType().Name}). Republish a valid snapshot or delete it.");
                }
            }
            return snapshots.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public void Upsert(WidgetSnapshot snapshot)
    {
        Validate(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length > MaxSnapshotBytes)
            throw new ArgumentException("Widget snapshot exceeds the size limit.");
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            // The file lease also serializes independent Companion processes using the same profile.
            using var lease = new FileStream(Path.Combine(_directory, "writer.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(_directory, snapshot.Id + ".json");
            if (File.Exists(path))
            {
                var previous = Read(path, observationOnly: true);
                if (previous.ObservedAt > snapshot.ObservedAt ||
                    (previous.ObservedAt == snapshot.ObservedAt &&
                     (previous with { Presentation = null }) != (snapshot with { Presentation = null })))
                    throw new ArgumentException("Widget observation is older than or conflicts with the saved snapshot.");
            }
            else if (Directory.GetFiles(_directory, "*.json").Length >= MaxWidgets)
            {
                throw new ArgumentException("Widget snapshot limit reached.");
            }
            var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    public bool Delete(string id)
    {
        ValidateId(id);
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
                return false;
            using var lease = new FileStream(Path.Combine(_directory, "writer.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(_directory, id + ".json");
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }

    private static WidgetSnapshot Read(string path, bool observationOnly = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSnapshotBytes)
            throw new InvalidDataException("Saved widget snapshot exceeds the size limit.");
        var document = JsonNodeFromStream(stream);
        // Compare bounded source facts without rendering or re-admitting an obsolete presentation.
        if (observationOnly)
            document.Remove("presentation");
        else if (document["presentation"] is System.Text.Json.Nodes.JsonObject presentation &&
            !presentation.ContainsKey("card"))
            document["presentation"] = JsonSerializer.SerializeToNode(
                WidgetPresentation.MigrateLegacy(JsonSerializer.SerializeToElement(presentation)), JsonOptions);
        var snapshot = document.Deserialize<WidgetSnapshot>(JsonOptions)
            ?? throw new InvalidDataException("Saved widget snapshot is empty.");
        if (observationOnly)
            ValidateObservation(snapshot);
        else
            Validate(snapshot);
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), snapshot.Id, StringComparison.Ordinal))
            throw new InvalidDataException("Saved widget id does not match its file.");
        return snapshot;
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeFromStream(Stream stream) =>
        System.Text.Json.Nodes.JsonNode.Parse(stream)?.AsObject()
        ?? throw new InvalidDataException("Saved widget snapshot is empty.");

    private static void ValidateText(string? text, int limit, string field, bool allowEmpty = false)
    {
        if (text is null || text.Length > limit || (!allowEmpty && string.IsNullOrWhiteSpace(text)) ||
            text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException($"Widget {field} is missing, too long or contains control characters.");
    }
}
