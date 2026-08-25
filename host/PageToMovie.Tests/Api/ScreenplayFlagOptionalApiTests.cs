using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// The clip and scene edit endpoints take an optional <c>?screenplay=true</c> that also removes the
/// matching line from the screenplay. It has to stay optional: a minimal-API handler parameter that
/// is a non-nullable value type is REQUIRED, so declaring it <c>bool</c> made every request without
/// the query string fail binding with 400 before the handler ran — deleting a clip from the Scenes
/// page did nothing at all, with no server-side clue why. Nullable is what makes it optional.
/// </summary>
public class ScreenplayFlagOptionalApiTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public ScreenplayFlagOptionalApiTests(PageToMovieApiFactory factory) => _factory = factory;

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var slug = "FlagTest_" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync("/api/projects", new { name = slug, title = "Flag Test" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("active").GetProperty("id").GetString()!;
    }

    private static string Api(string projectId) => $"/api/projects/{Uri.EscapeDataString(projectId)}";

    /// <summary>
    /// Asserts the request REACHED the handler, not that it succeeded. These projects have no shot
    /// plan, so "clip not found" — itself a 400 here — is the correct answer. Status alone cannot
    /// tell that apart from a binding failure; the body can, because a binding failure never gets
    /// far enough to produce the handler's own JSON.
    /// </summary>
    private static async Task AssertReachedHandlerAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("BadHttpRequestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Required parameter", body, StringComparison.Ordinal);
        Assert.Contains("\"ok\"", body, StringComparison.Ordinal);
    }

    /// <summary>The exact request the Scenes page sends when the screenplay box is unchecked.</summary>
    [Fact]
    public async Task Deleting_a_clip_without_the_flag_is_not_a_binding_failure()
    {
        var client = _factory.CreateUserClient("flag-user-1");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.DeleteAsync($"{Api(projectId)}/scenes/2/clips/4");

        await AssertReachedHandlerAsync(resp);
    }

    [Fact]
    public async Task Deleting_a_clip_with_the_flag_is_also_accepted()
    {
        var client = _factory.CreateUserClient("flag-user-2");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.DeleteAsync($"{Api(projectId)}/scenes/2/clips/4?screenplay=true");

        await AssertReachedHandlerAsync(resp);
    }

    [Fact]
    public async Task Deleting_a_scene_without_the_flag_is_not_a_binding_failure()
    {
        var client = _factory.CreateUserClient("flag-user-3");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.DeleteAsync($"{Api(projectId)}/scenes/2");

        await AssertReachedHandlerAsync(resp);
    }

    [Fact]
    public async Task Adding_a_scene_without_the_flag_is_not_a_binding_failure()
    {
        var client = _factory.CreateUserClient("flag-user-4");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.PostAsync($"{Api(projectId)}/scenes", content: null);

        await AssertReachedHandlerAsync(resp);
    }

    [Fact]
    public async Task Adding_a_clip_without_the_flag_is_not_a_binding_failure()
    {
        var client = _factory.CreateUserClient("flag-user-5");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.PostAsJsonAsync(
            $"{Api(projectId)}/scenes/2/clips",
            new { clip = 9, visualPrompt = "A door closes." });

        await AssertReachedHandlerAsync(resp);
    }

    /// <summary>The preview the delete prompt asks for before it can say what else goes.</summary>
    [Fact]
    public async Task The_delete_preview_answers_for_a_project_with_no_shot_plan()
    {
        var client = _factory.CreateUserClient("flag-user-6");
        var projectId = await CreateProjectAsync(client);

        var resp = await client.GetAsync($"{Api(projectId)}/scenes/2/clips/4/delete-preview");

        await AssertReachedHandlerAsync(resp);
    }
}
