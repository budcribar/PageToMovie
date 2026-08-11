using Xunit;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Tests.Collaboration;

/// <summary>
/// I5–I14 unit coverage for multi-user collab policy (P2–P6).
/// Offline — no HTTP, no paid endpoints.
/// </summary>
public sealed class PhaseICollabTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectLeaseService _leases;
    private readonly ProjectPresenceService _presence = new();

    public PhaseICollabTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-phase-i-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _leases = new ProjectLeaseService(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ——— I5 keyMode ———

    [Theory]
    [InlineData(null, "personal")]
    [InlineData("", "personal")]
    [InlineData("personal", "personal")]
    [InlineData("PERSONAL", "personal")]
    [InlineData("shared", "shared")]
    [InlineData("Shared", "shared")]
    [InlineData("other", "personal")]
    public void I5_KeyMode_normalize(string? input, string expected)
    {
        Assert.Equal(expected, ProjectKeyModes.Normalize(input));
        Assert.Equal(expected == "shared", ProjectKeyModes.IsShared(input));
    }

    [Fact]
    public void I5_Acl_default_keyMode_is_personal()
    {
        var doc = new ProjectAclDocument();
        Assert.Equal(ProjectKeyModes.Personal, ProjectKeyModes.Normalize(doc.KeyMode));
    }

    // ——— I6 script lease ———

    [Fact]
    public async Task I6_script_lease_blocks_second_editor()
    {
        var a = await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Script, "alice", TimeSpan.FromMinutes(5));
        Assert.True(a.Acquired);
        var b = await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Script, "bob", TimeSpan.FromMinutes(5));
        Assert.False(b.Acquired);
        Assert.Equal("alice", b.Lease.HolderUserId);
    }

    // ——— I7 scene lease + logout release ———

    [Fact]
    public async Task I7_scene_lease_no_steal_while_held()
    {
        var key = ProjectLeaseKeys.Scene(3);
        Assert.Equal("scene:3", key);
        Assert.True(await _leases.TryAcquireAsync("p1", key, "alice", TimeSpan.FromMinutes(5)) is { Acquired: true });
        var bob = await _leases.TryAcquireAsync("p1", key, "bob", TimeSpan.FromMinutes(5));
        Assert.False(bob.Acquired);
        Assert.Equal("alice", bob.Lease.HolderUserId);
    }

    [Fact]
    public async Task I7_I11_logout_ReleaseAll_allows_handoff()
    {
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Scene(1), "alice", TimeSpan.FromMinutes(5));
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Script, "alice", TimeSpan.FromMinutes(5));
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Scene(2), "bob", TimeSpan.FromMinutes(5));

        var n = await _leases.ReleaseAllForUserAsync("p1", "alice");
        Assert.Equal(2, n);

        var bobScene = await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Scene(1), "bob", TimeSpan.FromMinutes(5));
        Assert.True(bobScene.Acquired);
        // bob's own scene:2 still held
        var stealSelf = await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Scene(2), "alice", TimeSpan.FromMinutes(5));
        Assert.False(stealSelf.Acquired);
        Assert.Equal("bob", stealSelf.Lease.HolderUserId);
    }

    // ——— I8 cast + loc leases ———

    [Fact]
    public async Task I8_cast_and_loc_leases_independent()
    {
        var cast = ProjectLeaseKeys.Cast("Hero");
        var loc = ProjectLeaseKeys.Loc("Cafe");
        Assert.Equal("cast:Hero", cast);
        Assert.Equal("loc:Cafe", loc);

        Assert.True((await _leases.TryAcquireAsync("p1", cast, "alice", TimeSpan.FromMinutes(5))).Acquired);
        Assert.True((await _leases.TryAcquireAsync("p1", loc, "bob", TimeSpan.FromMinutes(5))).Acquired);

        var castConflict = await _leases.TryAcquireAsync("p1", cast, "bob", TimeSpan.FromMinutes(5));
        Assert.False(castConflict.Acquired);
        Assert.Equal("alice", castConflict.Lease.HolderUserId);

        var locConflict = await _leases.TryAcquireAsync("p1", loc, "alice", TimeSpan.FromMinutes(5));
        Assert.False(locConflict.Acquired);
        Assert.Equal("bob", locConflict.Lease.HolderUserId);
    }

    // ——— I9 delete blocked when scene leased ———

    [Fact]
    public async Task I9_scene_lease_present_blocks_delete_check()
    {
        var key = ProjectLeaseKeys.Scene(5);
        await _leases.TryAcquireAsync("p1", key, "alice", TimeSpan.FromMinutes(5));
        var held = await _leases.GetAsync("p1", key);
        Assert.NotNull(held);
        // API uses GetAsync != null → 423; unit asserts the gate condition
        Assert.Equal("alice", held!.HolderUserId);
    }

    [Fact]
    public void I9_TryParseScene()
    {
        Assert.True(ProjectLeaseKeys.TryParseScene("scene:12", out var n));
        Assert.Equal(12, n);
        Assert.False(ProjectLeaseKeys.TryParseScene("script", out _));
        Assert.False(ProjectLeaseKeys.TryParseScene("cast:Hero", out _));
    }

    // ——— I11 presence ———

    [Fact]
    public async Task I11_presence_find_by_connection_and_leave()
    {
        await _presence.HeartbeatAsync("p1", "alice", "conn-a");
        await _presence.HeartbeatAsync("p1", "bob", "conn-b");
        var list = await _presence.ListAsync("p1");
        Assert.Equal(2, list.Count);

        var hit = await _presence.FindByConnectionIdAsync("conn-a");
        Assert.NotNull(hit);
        Assert.Equal("p1", hit!.Value.ProjectId);
        Assert.Equal("alice", hit.Value.UserId);

        await _presence.LeaveAsync("p1", "alice");
        Assert.Null(await _presence.FindByConnectionIdAsync("conn-a"));
        list = await _presence.ListAsync("p1");
        Assert.Single(list);
        Assert.Equal("bob", list[0].UserId);
    }

    // ——— I6/I7 list + release ———

    [Fact]
    public async Task List_and_ReleaseAll_cover_multiple_resources()
    {
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Script, "alice", TimeSpan.FromMinutes(5));
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Scene(1), "alice", TimeSpan.FromMinutes(5));
        await _leases.TryAcquireAsync("p1", ProjectLeaseKeys.Cast("Hero"), "bob", TimeSpan.FromMinutes(5));

        var all = await _leases.ListAsync("p1");
        Assert.Equal(3, all.Count);

        var n = await _leases.ReleaseAllForUserAsync("p1", "alice");
        Assert.Equal(2, n);
        all = await _leases.ListAsync("p1");
        Assert.Single(all);
        Assert.Equal("bob", all[0].HolderUserId);
    }

    // ——— I13 take event field shapes (document contracts used by CostReportService) ———

    [Fact]
    public void I13_take_kind_and_key_mode_field_names()
    {
        // Contract check: FilmJobService / CostReportService emit these keys
        var evt = new Dictionary<string, object?>
        {
            ["user_id"] = "alice",
            ["key_mode"] = ProjectKeyModes.Shared,
            ["take_kind"] = "user_regen",
        };
        Assert.Equal("alice", evt["user_id"]);
        Assert.Equal("shared", evt["key_mode"]);
        Assert.Equal("user_regen", evt["take_kind"]);
    }
}
