using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Approve runs Cast from the screenplay inline. HTTP 200 / ok:true must not fire
/// unless <c>vision_meta.performance_lock</c> was persisted — the Screenplay page
/// never reads <c>cast.ok</c> and would otherwise send the operator to Estimate.
/// </summary>
public sealed class ScreenplaySignOffCastLockApiTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public ScreenplaySignOffCastLockApiTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SignOff_fails_when_extract_cannot_persist_a_performance_lock()
    {
        var client = _factory.CreateUserClient("signoff-lock-fail");
        var projectId = await CreateProjectAsync(client);
        await ApplyPlanningModelsAsync(projectId, writeMedium: false);

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay/sign-off",
            new { text = Fountain });
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var error = doc.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("performance lock", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cast from the screenplay", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("book/screenplay", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan looks", error, StringComparison.OrdinalIgnoreCase);

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        Assert.Null(ProjectVisionMeta.TryGetPerformanceLock(store.GetProjectDir(projectId)));
        Assert.False(ScreenplayService.Get(store, projectId).Status.Signed);
    }

    [Fact]
    public async Task SignOff_succeeds_when_extract_persists_a_performance_lock()
    {
        var client = _factory.CreateUserClient("signoff-lock-ok");
        var projectId = await CreateProjectAsync(client);
        await ApplyPlanningModelsAsync(projectId, writeMedium: true);

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay/sign-off",
            new { text = Fountain });
        var body = await resp.Content.ReadAsStringAsync();

        Assert.True(resp.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("cast").GetProperty("ok").GetBoolean(), body);

        var store = _factory.Services.GetRequiredService<ProjectStore>();
        var lockText = ProjectVisionMeta.TryGetPerformanceLock(store.GetProjectDir(projectId));
        Assert.False(string.IsNullOrWhiteSpace(lockText));
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "SignOffLock_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Sign-off lock" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    private async Task ApplyPlanningModelsAsync(string projectId, bool writeMedium)
    {
        var store = _factory.Services.GetRequiredService<ProjectStore>();
        await OfflineTestModelConfig.ApplyAsync(store, projectId, writeDecidedVision: false);
        if (!writeMedium)
            return;
        ProjectVisionMeta.Write(store.GetProjectDir(projectId), new ProjectVisionMeta.Document
        {
            VisualMedium = ProjectVisionMeta.MediumIllustrated,
            DecidedBy = "adaptation",
        });
    }

    private const string Fountain = """
        Title: Sign-off Lock

        INT. ROOM - DAY

        HERO
        Hello.
        """;
}
