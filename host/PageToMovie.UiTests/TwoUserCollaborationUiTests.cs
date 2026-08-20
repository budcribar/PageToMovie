using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace PageToMovie.UiTests;

/// <summary>
/// Two users on ONE project, at the UI level: the owner (admin, driving the real browser) works the
/// Film page while a second editor (bob, own identity via the API the app itself uses) holds a
/// scene lease. The owner's UI must SHOW the lock (row 🔒 badge + detail lock chip), refuse the
/// locked scene's delete, and clear once bob releases. Complements MultiUserLeaseUiTests, which
/// proves the lease API itself.
/// </summary>
[Collection("ui-pipeline")]
public sealed class TwoUserCollaborationUiTests
{
    private readonly PipelineFixture _fx;
    public TwoUserCollaborationUiTests(PipelineFixture fx) => _fx = fx;

    private static Task<string> ApiAsync(IPage page, string method, string path, string? body = null) =>
        page.EvaluateAsync<string>(@"async ([method, path, body]) => {
            const raw = sessionStorage.getItem('PageToMovie.admin.session');
            const s = JSON.parse(raw);
            const h = {'Authorization':'Bearer '+(s.Token||s.token), 'X-User-Id':(s.UserId||s.userId||''), 'Content-Type':'application/json'};
            const r = await fetch(path, {method, headers:h, body: body || undefined});
            return await r.text();
        }", new object?[] { method, path, body });

    [Fact]
    public async Task Second_editors_scene_lease_shows_in_owners_ui_and_blocks_delete()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        using var bob = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl.TrimEnd('/') + "/") };
        bob.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", "bob");
        bob.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            var name = "Collab_" + Guid.NewGuid().ToString("N")[..6];
            await PipelineFlow.RunToScenesAsync(page, _fx.BaseUrl, name, "mary_had_a_lamb.fountain");

            await Ui.GotoHomePickerAsync(page);
            var projectId = await Ui.SelectedPickerValueAsync(page);

            // Owner grants bob editor through the app's own ACL endpoint.
            var grant = await ApiAsync(page, "POST",
                PageToMovie.Core.Utils.ProjectIdRouting.ProjectApi(projectId) + "/acl/editors",
                JsonSerializer.Serialize(new { userId = "bob" }));
            Assert.Contains("bob", grant); // ACL response lists the granted editor

            // Bob (his own identity) takes the scene-1 lease.
            var leasePath = $"api/projects/{Uri.EscapeDataString(projectId)}/leases/{Uri.EscapeDataString("scene:1")}/acquire";
            using (var resp = await bob.PostAsync(leasePath, content: null))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }

            // The owner's Film page shows the lock: row badge on scene 1…
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            var scene1 = page.Locator("[data-testid='scene-row'][data-scene-number='1']");
            await Assertions.Expect(scene1.Locator("[title='Locked by bob']"))
                .ToBeVisibleAsync(new() { Timeout = 30_000 });

            // …and the opened detail carries the lock chip; the ⋯ menu's Delete scene is disabled.
            await scene1.Locator("span.badge").First.ClickAsync();
            await Assertions.Expect(page.GetByText("🔒 bob").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await page.GetByTestId("scene-menu").ClickAsync();
            await Assertions.Expect(page.GetByTestId("scene-delete")).ToBeDisabledAsync(new() { Timeout = 10_000 });
            await page.GetByTestId("scene-menu").ClickAsync(); // close the menu again

            // The server also refuses the delete outright (423) while bob holds the lease.
            var del = await ApiAsync(page, "DELETE",
                PageToMovie.Core.Utils.ProjectIdRouting.ProjectApi(projectId) + "/scenes/1");
            Assert.Contains("scene_locked", del);

            // Bob releases — the owner's UI clears after a reload.
            var releasePath = $"api/projects/{Uri.EscapeDataString(projectId)}/leases/{Uri.EscapeDataString("scene:1")}/release";
            using (var resp = await bob.PostAsync(releasePath, content: null))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
            await Ui.GotoAppAsync(page, _fx.BaseUrl, "/scenes");
            await Assertions.Expect(page.Locator("[data-testid='scene-row'][data-scene-number='1']")
                .Locator("[title='Locked by bob']")).ToHaveCountAsync(0, new() { Timeout = 30_000 });
        }
        finally { await ctx.CloseAsync(); }
    }
}
