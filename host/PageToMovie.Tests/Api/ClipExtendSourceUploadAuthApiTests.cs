using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Production path: JS <c>uploadUrlToServerAsync</c> POSTs extend-source with no Authorization
/// and no X-User-Id. Under <c>Auth:RequireLogin</c> that used to 403 (ACL saw the anonymous
/// default user). The fix authenticates <c>?mt=</c> (token_use=media) before ACL — same scheme
/// as media GET URLs.
/// </summary>
public class ClipExtendSourceUploadAuthApiTests : IClassFixture<RequireLoginApiFactory>
{
    private readonly RequireLoginApiFactory _factory;

    public ClipExtendSourceUploadAuthApiTests(RequireLoginApiFactory factory) => _factory = factory;

    private static MultipartFormDataContent BuildVideoForm(string fileName = "upload.mp4")
    {
        var bytes = Encoding.ASCII.GetBytes("....ftypmp42" + new string('x', 2000));
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(content, "video", fileName);
        return form;
    }

    private static async Task<string> DevLoginAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/auth/dev-login", null);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private static void SetBearerOnly(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Remove("X-User-Id");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "ExtendAuth_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Extend Auth Test" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    private static async Task<string> IssueMediaTokenAsync(HttpClient sessionClient)
    {
        var resp = await sessionClient.PostAsync("/api/auth/media-token", null);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private static string UploadPath(string projectId, string query) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/3/clips/2/upload?{query}";

    [Fact]
    public async Task Extend_source_upload_without_token_is_forbidden()
    {
        using var session = _factory.CreateClient();
        var jwt = await DevLoginAsync(session);
        SetBearerOnly(session, jwt);
        var projectId = await CreateProjectAsync(session);

        using var anon = _factory.CreateClient();
        using var form = BuildVideoForm();
        var resp = await anon.PostAsync(UploadPath(projectId, "kind=extend-source"), form);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Extend_source_upload_succeeds_with_media_token_query()
    {
        using var session = _factory.CreateClient();
        var jwt = await DevLoginAsync(session);
        SetBearerOnly(session, jwt);
        var projectId = await CreateProjectAsync(session);
        var media = await IssueMediaTokenAsync(session);

        using var browser = _factory.CreateClient(); // no Authorization, no X-User-Id — JS fetch
        using var form = BuildVideoForm();
        var resp = await browser.PostAsync(
            UploadPath(projectId, "kind=extend-source&seconds=8.04&mt=" + Uri.EscapeDataString(media)),
            form);

        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Extend_source_upload_succeeds_with_session_bearer_only()
    {
        using var session = _factory.CreateClient();
        var jwt = await DevLoginAsync(session);
        SetBearerOnly(session, jwt);
        var projectId = await CreateProjectAsync(session);

        using var form = BuildVideoForm();
        var resp = await session.PostAsync(UploadPath(projectId, "kind=extend-source"), form);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Extend_source_upload_rejects_session_jwt_in_mt_query()
    {
        using var session = _factory.CreateClient();
        var jwt = await DevLoginAsync(session);
        SetBearerOnly(session, jwt);
        var projectId = await CreateProjectAsync(session);

        using var browser = _factory.CreateClient();
        using var form = BuildVideoForm();
        var resp = await browser.PostAsync(
            UploadPath(projectId, "kind=extend-source&mt=" + Uri.EscapeDataString(jwt)),
            form);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
