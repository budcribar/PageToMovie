using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PageToMovie.Api;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Tests.Api;
using Xunit;

namespace PageToMovie.Tests;

[Collection("catalog-serial")]

/// <summary>
/// Wipe-and-resync: after a local project folder is emptied, media-sync must still offer
/// character plates, location plates (Railway-keep exception — must survive register/offload),
/// JSON sidecars, and a provider-recovery MP4 ticket. MP4 bytes are not a Railway recovery
/// source; they come back via sidecar <c>source_url</c> / <c>source_file_id</c> → media proxy.
/// </summary>
public class MediaWipeResyncTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public MediaWipeResyncTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public async Task After_register_offload_media_sync_still_offers_looks_sidecar_and_mp4_recovery()
    {
        const string sourceUrl = "https://vidgen.example/scene01.mp4";
        const string sourceFileId = "file_wipe_resync";

        var client = _factory.CreateUserClient("wipe-resync-user");
        var projectId = await CreateProjectAsync(client);
        var projectDir = Path.Combine(_factory.WorkspaceRoot, "projects", projectId);

        var charRel = ProjectAssetNaming.CharactersRelativePrefix + "hero_ref.png";
        var locRel = ProjectAssetNaming.LocationsRelativePrefix + "kitchen_ref.png";
        var locVariantRel = ProjectAssetNaming.LocationsRelativePrefix + "kitchen_variant_01.png";
        var mp4Rel = "assets/video/scene_01_clip_01_take_01.mp4";
        var sidecarRel = "assets/video/scene_01_clip_01_take_01.clip.json";
        var leftoverAliasRel = "assets/video/scene_01_clip_01.mp4";

        var charPath = WriteBytes(projectDir, charRel, "char-plate");
        var locPath = WriteBytes(projectDir, locRel, "loc-plate");
        var locVariantPath = WriteBytes(projectDir, locVariantRel, "loc-variant");
        var mp4Path = WriteBytes(projectDir, mp4Rel, "clip-bytes");
        WriteText(projectDir, "assets/video/scene_01_clip_01.current.json", """{"take":1}""");
        var sidecarPath = WriteText(projectDir, sidecarRel,
            $$"""{"scene":1,"clip":1,"take":1,"source_url":"{{sourceUrl}}","source_file_id":"{{sourceFileId}}"}""");

        await RegisterAsync(client, projectId, charRel, charPath, "image");
        await RegisterAsync(client, projectId, locRel, locPath, "image");
        await RegisterAsync(client, projectId, locVariantRel, locVariantPath, "image");
        await RegisterAsync(client, projectId, mp4Rel, mp4Path, "clip", scene: 1, clip: 1);

        Assert.True(File.Exists(charPath), "Character plate must survive register/offload");
        Assert.True(File.Exists(locPath), "Location plate must survive register/offload");
        Assert.True(File.Exists(locVariantPath), "Location variant must survive register/offload");
        Assert.True(File.Exists(sidecarPath), "Sidecar must stay on the server");
        Assert.False(File.Exists(mp4Path), "Railway must not keep clip bytes as a recovery source");
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(projectDir, "assets", "video"), "*.mp4", SearchOption.AllDirectories));

        var sync = await client.GetAsync($"{ProjectIdRouting.ProjectApi(projectId)}/media/sync");
        Assert.True(sync.IsSuccessStatusCode, await sync.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, files.ValueKind);

        AssertListed(files, charRel);
        AssertListed(files, locRel);
        AssertListed(files, locVariantRel);
        AssertListed(files, sidecarRel);

        var leftoverDisk = FindFile(files, e =>
            RelOf(e).Equals(leftoverAliasRel, StringComparison.OrdinalIgnoreCase));
        Assert.False(leftoverDisk.HasValue, "media-sync must not offer the leftover player alias");

        var diskMp4 = FindFile(files, e =>
            RelOf(e).Equals(mp4Rel, StringComparison.OrdinalIgnoreCase) && !IsProviderRecovery(e));
        Assert.False(diskMp4.HasValue, "media-sync must not offer a Railway disk copy of the MP4");

        var recovery = FindFile(files, e =>
            RelOf(e).Equals(mp4Rel, StringComparison.OrdinalIgnoreCase) && IsProviderRecovery(e));
        Assert.True(recovery.HasValue, "After a local wipe, MP4 recovery must be a provider ticket");
        Assert.True(recovery.Value.GetProperty("isMp4").GetBoolean());
        var streamUrl = recovery.Value.GetProperty("streamUrl").GetString() ?? "";
        Assert.StartsWith("/api/media/proxy/", streamUrl);
        Assert.DoesNotContain("/media/file", streamUrl, StringComparison.OrdinalIgnoreCase);

        var ticketed = new List<(string? Url, string? FileId)>();
        var entries = MediaEndpoints.CollectProviderRecoveryEntries(
            Path.Combine(projectDir, "assets", "video"),
            (url, fileId) =>
            {
                ticketed.Add((url, fileId));
                return "tok";
            });
        var entry = Assert.Single(entries);
        Assert.True(entry.ProviderRecovery);
        Assert.Equal(mp4Rel, entry.RelativePath);
        var issued = Assert.Single(ticketed);
        Assert.Equal(sourceUrl, issued.Url);
        Assert.Equal(sourceFileId, issued.FileId);
    }

    private static async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "WipeResync_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Wipe Resync" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    private static string WriteBytes(string projectDir, string relativePath, string contents)
    {
        var full = Path.Combine(projectDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    private static string WriteText(string projectDir, string relativePath, string contents) =>
        WriteBytes(projectDir, relativePath, contents);

    private static async Task RegisterAsync(
        HttpClient client, string projectId, string relativePath, string fullPath, string kind,
        int? scene = null, int? clip = null)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var body = new MediaRegisterRequest
        {
            RelativePath = relativePath,
            Sha256 = sha,
            SizeBytes = bytes.LongLength,
            Kind = kind,
            Scene = scene,
            Clip = clip,
        };
        var resp = await client.PostAsJsonAsync(
            $"{ProjectIdRouting.ProjectApi(projectId)}/media/register", body);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static void AssertListed(JsonElement files, string relativePath) =>
        Assert.True(
            FindFile(files, e => RelOf(e).Equals(relativePath, StringComparison.OrdinalIgnoreCase)).HasValue,
            $"media-sync must list {relativePath}");

    private static JsonElement? FindFile(JsonElement files, Func<JsonElement, bool> pred)
    {
        foreach (var e in files.EnumerateArray())
        {
            if (pred(e)) return e;
        }
        return null;
    }

    private static string RelOf(JsonElement e) =>
        e.TryGetProperty("relativePath", out var rel) ? rel.GetString() ?? "" : "";

    private static bool IsProviderRecovery(JsonElement e) =>
        e.TryGetProperty("providerRecovery", out var flag) && flag.ValueKind == JsonValueKind.True;
}
