using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

public sealed class BookTextRegistryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptm-book-registry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Register_is_content_addressed_and_resolves_by_id_or_hash_for_owner()
    {
        var service = new BookTextRegistryService(
            Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        const string text = "Mary had a little lamb.";

        var first = await service.RegisterAsync(text, "user-a", "project-1");
        var duplicate = await service.RegisterAsync(text, "user-a", "project-2");

        Assert.Equal(first.BookId, duplicate.BookId);
        Assert.Equal(first.Sha256, duplicate.Sha256);
        Assert.Equal(text, (await service.ResolveAsync(first.BookId, "user-a"))?.Text);
        Assert.Equal(first.BookId, (await service.ResolveAsync(first.Sha256, "user-a"))?.BookId);
        Assert.Null(await service.ResolveAsync(first.BookId, "user-b"));

        var fountain = await service.RegisterArtifactAsync(
            first.BookId, "user-a", "fountain", "Title: Mary\n\nEXT. SCHOOL - DAY",
            OfflineTestModelConfig.Required("chat"), "book-to-fountain.v3", new string('a', 64),
            0.2, "{\"visionMeta\":\"2\"}");
        var sameDerivation = await service.RegisterArtifactAsync(
            first.BookId, "user-a", "fountain", "Title: Mary\n\nEXT. SCHOOL - DAY",
            OfflineTestModelConfig.Required("chat"), "book-to-fountain.v3", new string('a', 64),
            0.2, "{\"visionMeta\":\"2\"}");
        Assert.Equal(fountain.ArtifactId, sameDerivation.ArtifactId);
        Assert.Equal(fountain.Content, (await service.ResolveArtifactAsync(fountain.ArtifactId, "user-a"))?.Content);
        var changedPrompt = await service.RegisterArtifactAsync(
            first.BookId, "user-a", "fountain", "Title: Mary changed",
            OfflineTestModelConfig.Required("chat"), "book-to-fountain.v4", new string('c', 64),
            0.2, "{\"visionMeta\":\"2\"}");
        Assert.NotEqual(fountain.ArtifactId, changedPrompt.ArtifactId);

        await service.LinkToProjectAsync(first.BookId, "user-a", "cloned-project");
        Assert.Equal(first.BookId, (await service.ResolveAsync(first.BookId, "user-a"))?.BookId);

        var publicBook = await service.RegisterAsync(
            "A publicly readable book.", "user-a", "public-project", "Public");
        Assert.NotNull(await service.ResolveAsync(publicBook.BookId, "user-b"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.LinkToProjectAsync(publicBook.BookId, "user-b", "not-allowed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RegisterArtifactAsync(
            publicBook.BookId, "user-b", "fountain", "readers cannot write",
            OfflineTestModelConfig.Required("chat"), "v1", new string('b', 64), 0.2, "{}"));

        var forkableBook = await service.RegisterAsync(
            "A forkable book.", "user-a", "forkable-project", "Forkable");
        Assert.NotNull(await service.ResolveAsync(forkableBook.BookId, "user-b"));
        await service.LinkToProjectAsync(forkableBook.BookId, "user-b", "user-b-fork");
        Assert.NotNull(await service.ResolveAsync(forkableBook.BookId, "user-b"));

        await service.LinkForkAsync(
            "forkable-project", "user-c", "user-c-fork", invitationAuthorized: false);
        Assert.NotNull(await service.ResolveAsync(forkableBook.BookId, "user-c"));

        await service.LinkForkAsync(
            "public-project", "user-c", "public-not-forked", invitationAuthorized: false);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.LinkToProjectAsync(publicBook.BookId, "user-c", "still-not-allowed"));

        await service.SetProjectVisibilityAsync("user-a", "project-1", "Public");
        Assert.NotNull(await service.ResolveAsync(first.BookId, "user-b"));
    }

    [Fact]
    public async Task Screenplay_generation_reuses_complete_shared_adaptation_package()
    {
        var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var project = await store.CreateProjectAsync("mary-cache", "Mary Cache");
        await OfflineTestModelConfig.ApplyAsync(store, project.Id);
        var source = Path.Combine(store.GetProjectDir(project.Id), "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "book_full.txt"),
            "Mary takes her lamb to school. The teacher sends it outside. Mary returns and comforts it.");

        var response = """
            Title: Mary Cache

            EXT. ROAD - MORNING

            Mary and her lamb walk toward school.

            INT. SCHOOLROOM - DAY

            The children laugh when the lamb enters.

            TEACHER
            Lambs must wait outside.

            EXT. SCHOOLYARD - DAY

            The lamb waits patiently near the door.

            EXT. SCHOOLYARD - AFTERNOON

            Mary returns and embraces the lamb.

            MARY
            I will keep you safe.

            FADE OUT.

            THE END

            ---VISION_META---
            {"visual_medium":"illustrated_picture_book","render_style_lock":"painted storybook continuity"}
            ---END_VISION_META---
            """;
        var chat = new CountingChatClient(response);
        var registry = new BookTextRegistryService(
            Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));

        var first = await ScreenplayService.CreateDraftFromBookAsync(
            store, project.Id, chat, model: OfflineTestModelConfig.Required("chat"),
            bookRegistry: registry, cacheUserId: "user-a");
        Assert.True(first.Ok, first.Error);
        var vision = ProjectVisionMeta.RequireDecided(store.GetProjectDir(project.Id));
        Assert.Equal(ProjectVisionMeta.MediumIllustrated, vision.VisualMedium);
        Assert.False(string.IsNullOrWhiteSpace(vision.RenderStyleLock));
        var callsAfterFirst = chat.Calls;
        Assert.True(callsAfterFirst > 0);

        File.Delete(ScreenplayService.GetDraftPath(store, project.Id));
        var second = await ScreenplayService.CreateDraftFromBookAsync(
            store, project.Id, chat, model: OfflineTestModelConfig.Required("chat"),
            bookRegistry: registry, cacheUserId: "user-a");
        Assert.True(second.Ok, second.Error);
        Assert.Equal(callsAfterFirst, chat.Calls);
        Assert.Contains("reused", second.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CountingChatClient(string response) : IChatClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "", double temperature = 0.2,
            CancellationToken ct = default, string? reasoningEffort = null, string? serviceTier = null)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
