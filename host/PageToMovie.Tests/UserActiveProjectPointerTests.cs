using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The per-user active-project pointer must persist even when the user has no row yet in the users
/// table (fresh workspace, dev/fakes login, a session that never touched it). A bare UPDATE used
/// to persist nothing, so GET /api/projects fell back to the first project alphabetically — the
/// browser had the just-created project active, the server had another one, and every job started
/// via the API (shot plan, generation) ran against the wrong, empty project.
/// </summary>
public class UserActiveProjectPointerTests
{
    private static UserDatabaseService MakeDb()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_active_ptr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
        return new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);
    }

    [Fact]
    public async Task Set_persists_for_a_user_without_a_row_and_updates_thereafter()
    {
        var db = MakeDb();
        const string user = "someone@example.com";

        Assert.Null(await db.GetUserActiveProjectAsync(user));

        await db.SetUserActiveProjectAsync(user, "someoneexample_com/Alpha");
        Assert.Equal("someoneexample_com/Alpha", await db.GetUserActiveProjectAsync(user));

        await db.SetUserActiveProjectAsync(user, "someoneexample_com/Beta");
        Assert.Equal("someoneexample_com/Beta", await db.GetUserActiveProjectAsync(user));

        await db.SetUserActiveProjectAsync(user, null);
        Assert.Null(await db.GetUserActiveProjectAsync(user));
    }
}
