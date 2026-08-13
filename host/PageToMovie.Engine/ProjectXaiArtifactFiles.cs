using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PageToMovie.Engine;

/// <summary>
/// Project-local xAI Files handles for artifacts we produce (screenplay.max, etc.).
/// Reuse <c>file_id</c> when the content SHA-256 still matches and the upload has not expired.
/// Book text prefers <see cref="BookTextRegistryService"/> (<c>provider=xai</c>); this sidecar
/// is for project-owned files that change independently of the book.
/// </summary>
public static class ProjectXaiArtifactFiles
{
    public const string RelativePath = "source/xai_artifact_files.json";
    public const string KindScreenplayMax = "screenplay.max";
    public const string KindScreenplayDraft = "screenplay.draft";
    public const string KindBookFull = "book_full";

    public static string GetPath(string projectDir) => Path.Combine(projectDir, RelativePath);

    public sealed class Entry
    {
        public string Kind { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string FileId { get; set; } = "";
        public long? ExpiresAtUnix { get; set; }
        public int Bytes { get; set; }
        public string? Filename { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    public static string Sha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static bool TryGetReusable(
        string projectDir, string kind, string sha256, out Entry? entry, long nowUnix = 0)
    {
        entry = null;
        var doc = TryRead(projectDir);
        if (doc is null || string.IsNullOrWhiteSpace(sha256)) return false;
        if (!doc.TryGetValue(kind, out var found) || found is null) return false;
        if (!string.Equals(found.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(found.FileId)) return false;
        if (nowUnix <= 0) nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (found.ExpiresAtUnix is long exp && exp <= nowUnix + 3600) return false;
        entry = found;
        return true;
    }

    public static void Upsert(string projectDir, Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Kind))
            throw new ArgumentException("kind required", nameof(entry));
        var path = GetPath(projectDir);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var doc = TryRead(projectDir) ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        entry.UpdatedAt = DateTime.UtcNow.ToString("o");
        doc[entry.Kind] = entry;
        var payload = new { schema_version = 1, files = doc };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Indented) + "\n");
    }

    public static Dictionary<string, Entry>? TryRead(string projectDir)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in files.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                map[p.Name] = new Entry
                {
                    Kind = p.Name,
                    Sha256 = Str(p.Value, "sha256") ?? Str(p.Value, "Sha256") ?? "",
                    FileId = Str(p.Value, "file_id") ?? Str(p.Value, "FileId") ?? "",
                    ExpiresAtUnix = Long(p.Value, "expires_at_unix") ?? Long(p.Value, "ExpiresAtUnix"),
                    Bytes = Int(p.Value, "bytes") ?? Int(p.Value, "Bytes") ?? 0,
                    Filename = Str(p.Value, "filename") ?? Str(p.Value, "Filename"),
                    UpdatedAt = Str(p.Value, "updated_at") ?? Str(p.Value, "UpdatedAt") ?? "",
                };
            }
            return map;
        }
        catch
        {
            return null;
        }
    }

    static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
