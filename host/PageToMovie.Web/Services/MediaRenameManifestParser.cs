using System.Text.Json;

namespace PageToMovie.Web.Services;

/// <summary>Parses the server's media-renames JSON payload. Extracted from
/// <see cref="EngineApiClient.GetMediaRenamesAsync"/> so that method stays under the CC cap.</summary>
internal static class MediaRenameManifestParser
{
    public static IReadOnlyList<MediaRenameManifestEntry> Parse(string json)
    {
        var entries = new List<MediaRenameManifestEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return entries;
        foreach (var e in arr.EnumerateArray())
        {
            var entry = TryReadEntry(e);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries;
    }

    private static MediaRenameManifestEntry? TryReadEntry(JsonElement e)
    {
        var entry = new MediaRenameManifestEntry
        {
            Id = ReadEntryId(e),
        };
        AddRenames(entry, e);
        AddDeletes(entry, e);
        return entry.Id > 0 ? entry : null;
    }

    private static long ReadEntryId(JsonElement e) =>
        e.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0;

    private static void AddRenames(MediaRenameManifestEntry entry, JsonElement e)
    {
        if (!e.TryGetProperty("renames", out var rn) || rn.ValueKind != JsonValueKind.Array)
            return;
        foreach (var r in rn.EnumerateArray())
        {
            var from = r.TryGetProperty("from", out var f) ? f.GetString() : null;
            var to = r.TryGetProperty("to", out var t) ? t.GetString() : null;
            if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                entry.Renames.Add((from, to));
        }
    }

    private static void AddDeletes(MediaRenameManifestEntry entry, JsonElement e)
    {
        if (!e.TryGetProperty("deletes", out var dl) || dl.ValueKind != JsonValueKind.Array)
            return;
        foreach (var d in dl.EnumerateArray())
        {
            if (d.GetString() is { Length: > 0 } path) entry.Deletes.Add(path);
        }
    }
}
