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
/// Combined video-extend recovery: each sidecar hop is one previous clip. Sync writes the
/// tail as the current clip and walks backward when a leftover unsliced head still contains
/// earlier clips — never the raw combined file as a clip.
/// </summary>
public class CombinedExtendRecoveryTests
{
    private const string ProjectId = "Mary19";
    private const string C1 = "assets/video/scene_01_clip_01.mp4";
    private const string C2 = "assets/video/scene_01_clip_02.mp4";
    private const string C3 = "assets/video/scene_01_clip_03.mp4";
    private const string ProviderUrl = "/api/media/proxy/tok-combined";

    [Fact]
    public void TryGetPreviousClipRelativePath_same_scene_clip_minus_one()
    {
        Assert.True(CombinedExtendRecovery.TryGetPreviousClipRelativePath(C2, out var prev));
        Assert.Equal(C1, prev);
        Assert.True(CombinedExtendRecovery.TryGetNthPreviousClipRelativePath(C3, 2, out var c1));
        Assert.Equal(C1, c1);
        Assert.False(CombinedExtendRecovery.TryGetPreviousClipRelativePath(C1, out _));
    }

    [Fact]
    public void PlanPredecessorHops_sliced_C2_does_not_put_C1_in_C3()
    {
        // C3.leadIn is only sliced C2 (5.0); C2 sidecar still has C1's 4.9 hop.
        var hops = CombinedExtendRecovery.PlanPredecessorHops(5.0, new[] { 4.9 });
        Assert.Empty(hops);
    }

    [Fact]
    public void PlanPredecessorHops_unsliced_C2_walks_C1_plus_C2()
    {
        var hops = CombinedExtendRecovery.PlanPredecessorHops(9.8, new[] { 4.9 });
        Assert.Equal(new[] { 4.9 }, hops);
    }

    [Fact]
    public void PreferCombinedSource_local_wins_then_provider()
    {
        Assert.Equal("blob:local", CombinedExtendRecovery.PreferCombinedSource("blob:local", ProviderUrl));
        Assert.Equal(ProviderUrl, CombinedExtendRecovery.PreferCombinedSource(null, ProviderUrl));
        Assert.Null(CombinedExtendRecovery.PreferCombinedSource(null, null));
    }

    [Fact]
    public async Task C2_combined_writes_C1_head_and_C2_tail()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 10);

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C2, leadIn: 4.9));

        Assert.True(n);
        Assert.True(js.SavedRelative(C2));
        Assert.True(js.SavedRelative(C1));
        Assert.True(js.SavedFromTail(C2));
        Assert.True(js.SavedFromHead(C1));
        Assert.DoesNotContain(js.Saves, s => IsCombinedSource(s.Url));
    }

    [Fact]
    public async Task C3_after_sliced_C2_writes_C2_only_C1_is_not_in_C3()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 10);

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C3, leadIn: 5.0, predecessors: [4.9]));

        Assert.True(n);
        Assert.True(js.SavedRelative(C3));
        Assert.True(js.SavedRelative(C2));
        Assert.True(js.SavedFromTail(C3));
        Assert.True(js.SavedFromHead(C2));
        Assert.False(js.SavedRelative(C1));
        Assert.DoesNotContain(js.Saves, s => IsCombinedSource(s.Url));
    }

    [Fact]
    public async Task C3_after_unsliced_C2_splits_head_using_C2_sidecar()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 14.8);

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C3, leadIn: 9.8, predecessors: [4.9]));

        Assert.True(n);
        Assert.True(js.SavedRelative(C3));
        Assert.True(js.SavedRelative(C2));
        Assert.True(js.SavedRelative(C1));
        Assert.True(js.SavedFromTail(C3));
        Assert.True(js.SavedFromTail(C2));
        Assert.True(js.SavedFromHead(C1));
        Assert.DoesNotContain(js.Saves, s => IsCombinedSource(s.Url));
        Assert.DoesNotContain(js.Saves, s =>
            s.Path.Contains("clip_02", StringComparison.Ordinal) && s.Url.StartsWith("blob:head:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C1_present_is_not_overwritten()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 10);
        js.LocalFiles.Add(C1);

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C2, leadIn: 4.9));

        Assert.True(n);
        Assert.True(js.SavedFromTail(C2));
        Assert.False(js.SavedRelative(C1));
        Assert.Contains(C1, js.LocalFiles);
    }

    [Fact]
    public async Task Provider_404_with_no_local_combined_leaves_C1_missing()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 10);
        js.ProviderUnavailable = true;

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C2, leadIn: 4.9));

        Assert.False(n);
        Assert.False(js.SavedRelative(C1));
        Assert.False(js.SavedRelative(C2));
        Assert.DoesNotContain(C1, js.LocalFiles);
    }

    [Fact]
    public async Task Local_combined_C2_is_preferred_over_provider()
    {
        var (svc, js) = await ConnectAsync(providerDuration: 10);
        js.CombinedLocalFiles.Add(C2);
        js.SetDuration("blob:local-combined/" + C2, 10);
        js.ProviderUnavailable = true;

        var n = await svc.TrySaveSyncedMediaFileAsync(ProjectId, CombinedClip(C2, leadIn: 4.9));

        Assert.True(n);
        Assert.True(js.SavedFromTail(C2));
        Assert.True(js.SavedFromHead(C1));
        Assert.Contains(js.TrimSources, u => u.StartsWith("blob:local-combined/", StringComparison.Ordinal));
        Assert.DoesNotContain(js.TrimSources, u =>
            u.Contains("/api/media/proxy/", StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectMediaSyncFile CombinedClip(string relativePath, double leadIn, double[]? predecessors = null) => new()
    {
        RelativePath = relativePath,
        FileName = Path.GetFileName(relativePath),
        IsMp4 = true,
        StreamUrl = ProviderUrl,
        ProviderRecovery = true,
        ProviderLeadInSeconds = leadIn,
        PredecessorLeadInSeconds = predecessors?.ToList() ?? new List<double>(),
    };

    private static bool IsCombinedSource(string url) =>
        url.Contains("/api/media/proxy/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("blob:local-combined/", StringComparison.Ordinal);

    private static async Task<(ClientMediaFolderService Svc, RecoveryJsRuntime Js)> ConnectAsync(double providerDuration)
    {
        var js = new RecoveryJsRuntime { ProviderDurationSeconds = providerDuration };
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost") };
        var api = new EngineApiClient(http);
        var hub = new JobHubClient(Options.Create(new EngineApiOptions()));
        var svc = new ClientMediaFolderService(js, api, hub, new ActiveProjectState())
        {
            AutoSyncOnLogin = false,
        };
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
        public double ProviderDurationSeconds { get; set; } = 10;
        private readonly Dictionary<string, double> _durations = new(StringComparer.Ordinal);
        private int _trimSeq;

        public void SetDuration(string url, double seconds) => _durations[url] = seconds;

        public bool SavedRelative(string relativePath) =>
            Saves.Any(s => PathEndsWith(s.Path, relativePath));

        public bool SavedFromTail(string relativePath) =>
            Saves.Any(s => PathEndsWith(s.Path, relativePath) && s.Url.StartsWith("blob:tail:", StringComparison.Ordinal));

        public bool SavedFromHead(string relativePath) =>
            Saves.Any(s => PathEndsWith(s.Path, relativePath) && s.Url.StartsWith("blob:head:", StringComparison.Ordinal));

        private static bool PathEndsWith(string path, string relativePath) =>
            path.EndsWith("/" + relativePath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(relativePath, StringComparison.OrdinalIgnoreCase);

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
                var keep = args.Length > 1 && double.TryParse(args[1]?.ToString(), out var k) ? k : 1;
                var kind = identifier.EndsWith("trimHeadAsync", StringComparison.Ordinal) ? "head" : "tail";
                var blob = $"blob:{kind}:{++_trimSeq}";
                _durations[blob] = keep;
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
            if (_durations.TryGetValue(url, out var mapped))
                return $"{{\"success\":true,\"seconds\":{mapped.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
            if (url.StartsWith("blob:local-combined/", StringComparison.Ordinal))
                return """{"success":true,"seconds":10}""";
            if (url.StartsWith("blob:local/", StringComparison.Ordinal))
                return """{"success":true,"seconds":5}""";
            if (IsProvider(url))
            {
                return ProviderUnavailable
                    ? """{"success":false,"error":"HTTP 404"}"""
                    : $"{{\"success\":true,\"seconds\":{ProviderDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
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
