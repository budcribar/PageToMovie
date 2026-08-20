using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Combined video-extend recovery: clip N's provider/local copy is previous + new tail.
/// Sync must write the tail as the current clip and, when clip-1 is missing, the head as
/// that previous clip — never the raw combined file as the current clip.
/// </summary>
public class CombinedExtendRecoveryTests
{
    private const string ProjectId = "Mary19";
    private const string C1 = "assets/video/scene_01_clip_01.mp4";
    private const string C2 = "assets/video/scene_01_clip_02.mp4";
    private const string ProviderUrl = "/api/media/proxy/tok-combined";

    [Fact]
    public void TryGetPreviousClipRelativePath_same_scene_clip_minus_one()
    {
        Assert.True(CombinedExtendRecovery.TryGetPreviousClipRelativePath(C2, out var prev));
        Assert.Equal(C1, prev);
        Assert.False(CombinedExtendRecovery.TryGetPreviousClipRelativePath(C1, out _));
        Assert.False(CombinedExtendRecovery.TryGetPreviousClipRelativePath("assets/music/scene_01_seg_01.wav", out _));
    }

    [Fact]
    public void PreferCombinedSource_local_wins_then_provider()
    {
        Assert.Equal("blob:local", CombinedExtendRecovery.PreferCombinedSource("blob:local", ProviderUrl));
        Assert.Equal(ProviderUrl, CombinedExtendRecovery.PreferCombinedSource(null, ProviderUrl));
        Assert.Null(CombinedExtendRecovery.PreferCombinedSource(null, null));
    }

    [Fact]
    public async Task C1_missing_and_combined_C2_writes_both()
    {
        var (svc, js) = await ConnectAsync();

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedC2());

        Assert.True(n);
        Assert.True(js.SavedClip(C2, "blob:tail"));
        Assert.True(js.SavedClip(C1, "blob:head"));
        Assert.DoesNotContain(js.Saves, s => IsCombinedSource(s.Url));
    }

    [Fact]
    public async Task C1_present_is_not_overwritten()
    {
        var (svc, js) = await ConnectAsync();
        js.LocalFiles.Add(C1);

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedC2());

        Assert.True(n);
        Assert.True(js.SavedClip(C2, "blob:tail"));
        Assert.False(js.SavedRelative(C1));
        Assert.Contains(C1, js.LocalFiles);
    }

    [Fact]
    public async Task Provider_404_with_no_local_combined_leaves_C1_missing()
    {
        var (svc, js) = await ConnectAsync();
        js.ProviderUnavailable = true;

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedC2());

        Assert.False(n);
        Assert.False(js.SavedRelative(C1));
        Assert.False(js.SavedRelative(C2));
        Assert.DoesNotContain(C1, js.LocalFiles);
    }

    [Fact]
    public async Task Local_combined_C2_is_preferred_over_provider()
    {
        var (svc, js) = await ConnectAsync();
        js.CombinedLocalFiles.Add(C2);
        js.ProviderUnavailable = true; // provider must not be required

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedC2());

        Assert.True(n);
        Assert.True(js.SavedClip(C2, "blob:tail"));
        Assert.True(js.SavedClip(C1, "blob:head"));
        Assert.Contains(js.TrimSources, u => u.StartsWith("blob:local-combined/", StringComparison.Ordinal));
        Assert.DoesNotContain(js.TrimSources, u =>
            u.Contains("/api/media/proxy/", StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectMediaSyncFile CombinedC2() => new()
    {
        RelativePath = C2,
        FileName = "scene_01_clip_02.mp4",
        IsMp4 = true,
        StreamUrl = ProviderUrl,
        ProviderRecovery = true,
        ProviderLeadInSeconds = 4.9,
    };

    private static bool IsCombinedSource(string url) =>
        url.Contains("/api/media/proxy/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("blob:local-combined/", StringComparison.Ordinal);

    private static async Task<(ClientMediaFolderService Svc, RecoveryJsRuntime Js)> ConnectAsync()
    {
        var js = new RecoveryJsRuntime();
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost") };
        var api = new EngineApiClient(http);
        var hub = new JobHubClient(Options.Create(new EngineApiOptions()));
        var svc = new ClientMediaFolderService(js, api, hub, new ActiveProjectState())
        {
            AutoSyncOnLogin = false,
        };
        // FolderName/FullPath is enough for IsConnected — skip ConnectFolderAsync so tests
        // do not open a SignalR hub.
        await svc.SetFullPathAsync("/tmp/media");
        return (svc, js);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class RecoveryJsRuntime : IJSRuntime
    {
        public HashSet<string> LocalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CombinedLocalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Url, string Path)> Saves { get; } = new();
        public List<string> TrimSources { get; } = new();
        public bool ProviderUnavailable { get; set; }

        public bool SavedRelative(string relativePath) =>
            Saves.Any(s => s.Path.EndsWith("/" + relativePath, StringComparison.OrdinalIgnoreCase)
                           || s.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        public bool SavedClip(string relativePath, string url) =>
            Saves.Any(s => s.Url == url
                           && (s.Path.EndsWith("/" + relativePath, StringComparison.OrdinalIgnoreCase)
                               || s.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase)));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            args ??= Array.Empty<object?>();
            var json = ResponseJson(identifier, args);
            var result = JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;
            return ValueTask.FromResult(result);
        }

        private string ResponseJson(string identifier, object?[] args)
        {
            if (identifier == "PageToMovieMedia.connectFolderAsync")
                return """{"success":true,"folderName":"Test","fullPath":"/tmp/media"}""";

            if (identifier == "PageToMovieMedia.statLocalFileAsync")
            {
                var rel = StripProject(Arg(args, 0));
                var present = LocalFiles.Contains(rel) || CombinedLocalFiles.Contains(rel);
                return present
                    ? """{"success":true,"sizeBytes":50000}"""
                    : """{"success":false}""";
            }

            if (identifier == "PageToMovieMedia.getBlobUrlAsync")
            {
                var rel = StripProject(Arg(args, 0));
                if (CombinedLocalFiles.Contains(rel))
                    return $"{{\"success\":true,\"url\":\"blob:local-combined/{rel}\"}}";
                if (LocalFiles.Contains(rel))
                    return $"{{\"success\":true,\"url\":\"blob:local/{rel}\"}}";
                return """{"success":false}""";
            }

            if (identifier == "PageToMovieFfmpeg.probeDurationAsync")
                return Probe(Arg(args, 0));

            if (identifier is "PageToMovieFfmpeg.trimTailAsync" or "PageToMovieFfmpeg.trimHeadAsync")
            {
                var url = Arg(args, 0);
                TrimSources.Add(url);
                if (IsUnavailable(url))
                    return """{"success":false,"error":"HTTP 404"}""";
                var blob = identifier.EndsWith("trimHeadAsync", StringComparison.Ordinal) ? "blob:head" : "blob:tail";
                return $"{{\"success\":true,\"url\":\"{blob}\"}}";
            }

            if (identifier == "PageToMovieMedia.saveFromUrlAsync")
            {
                var url = Arg(args, 0);
                var path = Arg(args, 1);
                Saves.Add((url, path));
                if (IsCombinedSource(url))
                    return """{"success":false,"error":"refused to save combined file as a clip"}""";
                var rel = StripProject(path);
                LocalFiles.Add(rel);
                CombinedLocalFiles.Remove(rel);
                return """{"success":true,"sha256":"abc","sizeBytes":40000,"relativePath":"x"}""";
            }

            if (identifier == "PageToMovieMedia.getFullPath")
                return "\"/tmp/media\"";

            return """{"success":true}""";
        }

        private string Probe(string url)
        {
            if (url.StartsWith("blob:local-combined/", StringComparison.Ordinal))
                return """{"success":true,"seconds":10}""";
            if (url.StartsWith("blob:local/", StringComparison.Ordinal))
                return """{"success":true,"seconds":5}""";
            if (IsProvider(url))
            {
                return ProviderUnavailable
                    ? """{"success":false,"error":"HTTP 404"}"""
                    : """{"success":true,"seconds":10}""";
            }
            return """{"success":false}""";
        }

        private bool IsUnavailable(string url) => IsProvider(url) && ProviderUnavailable;

        private static bool IsProvider(string url) =>
            url.Contains("/api/media/proxy/", StringComparison.OrdinalIgnoreCase);

        private static string Arg(object?[] args, int i) => args.Length > i ? args[i]?.ToString() ?? "" : "";

        private static string StripProject(string clientPath)
        {
            var norm = clientPath.Replace('\\', '/');
            var prefix = ProjectId + "/";
            return norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? norm[prefix.Length..]
                : norm;
        }
    }
}
