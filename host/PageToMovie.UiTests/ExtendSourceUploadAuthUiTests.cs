using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Browser <c>fetch</c> to the extend-source upload endpoint on a host with
/// <c>Auth:RequireLogin</c> on and a non-admin anonymous default user. Mirrors production:
/// same-origin POST with no Authorization and no X-User-Id must 403; the same POST with
/// <c>?mt=</c> (token_use=media) must succeed.
/// </summary>
[Collection("ui-require-login")]
public sealed class ExtendSourceUploadAuthUiTests
{
    private readonly RequireLoginUiFixture _fx;

    public ExtendSourceUploadAuthUiTests(RequireLoginUiFixture fx) => _fx = fx;

    [Fact]
    public async Task Js_style_extend_source_post_is_forbidden_without_mt_and_ok_with_media_token()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            await page.GotoAsync(_fx.BaseUrl + "/?admin=1", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.WaitForFunctionAsync(
                "() => { const r = sessionStorage.getItem('PageToMovie.admin.session'); return !!(r && JSON.parse(r).Token); }",
                null,
                new() { Timeout = 60_000 });

            var created = await Ui.ApiFetchAsync(page, "/api/projects", "POST",
                "{\"name\":\"ExtendAuthUi\",\"title\":\"Extend Auth UI\"}");
            using var createdDoc = JsonDocument.Parse(created);
            var projectId = createdDoc.RootElement.GetProperty("active").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(projectId), created);

            var resultJson = await page.EvaluateAsync<string>(@"async (projectId) => {
                const raw = sessionStorage.getItem('PageToMovie.admin.session');
                if (!raw) return JSON.stringify({ err: 'no session' });
                const s = JSON.parse(raw);
                const sessionHeaders = {
                    'Authorization': 'Bearer ' + (s.Token || s.token),
                    'X-User-Id': (s.UserId || s.userId || '')
                };
                const mtResp = await fetch('/api/auth/media-token', { method: 'POST', headers: sessionHeaders });
                const mtBody = await mtResp.json().catch(() => ({}));
                const mediaToken = mtBody.token || mtBody.Token || '';

                const buf = new Uint8Array(2048);
                buf.set([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6d, 0x70, 0x34, 0x32]);
                const blob = new Blob([buf], { type: 'video/mp4' });
                const path = '/api/projects/' + encodeURIComponent(projectId) + '/scenes/3/clips/2/upload?kind=extend-source&seconds=8.04';

                const formBare = new FormData();
                formBare.append('video', blob, 'upload.mp4');
                const bare = await fetch(path, { method: 'POST', body: formBare, credentials: 'same-origin' });

                const formMt = new FormData();
                formMt.append('video', blob, 'upload.mp4');
                const withMt = await fetch(path + '&mt=' + encodeURIComponent(mediaToken), {
                    method: 'POST', body: formMt, credentials: 'same-origin'
                });

                return JSON.stringify({
                    bareStatus: bare.status,
                    mtStatus: withMt.status,
                    mtOk: mtResp.ok,
                    hasMediaToken: !!mediaToken
                });
            }", projectId);

            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("hasMediaToken").GetBoolean(), "session should mint a media token");
            Assert.Equal((int)HttpStatusCode.Forbidden, root.GetProperty("bareStatus").GetInt32());
            var mtStatus = root.GetProperty("mtStatus").GetInt32();
            Assert.True(mtStatus is >= 200 and < 300, $"authenticated extend-source POST → {mtStatus}: {resultJson}");
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }
}
