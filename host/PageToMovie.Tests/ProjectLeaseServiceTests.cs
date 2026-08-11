using Xunit;
using PageToMovie.Engine.Collaboration;

namespace PageToMovie.Tests.Collaboration;

public class ProjectLeaseServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectLeaseService _sut;

    public ProjectLeaseServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-lease-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sut = new ProjectLeaseService(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task TryAcquire_first_holder_succeeds()
    {
        var r = await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        Assert.True(r.Acquired);
        Assert.NotNull(r.Lease);
        Assert.Equal("alice", r.Lease.HolderUserId);
        Assert.Equal("scene:1", r.Lease.ResourceKey);
    }

    [Fact]
    public async Task TryAcquire_same_user_refreshes()
    {
        var first = await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(1));
        var second = await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(10));
        Assert.True(first.Acquired);
        Assert.True(second.Acquired);
        Assert.True(second.Lease.ExpiresAt >= first.Lease.ExpiresAt);
    }

    [Fact]
    public async Task TryAcquire_second_user_gets_conflict()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        var r = await _sut.TryAcquireAsync("proj1", "scene:1", "bob", TimeSpan.FromMinutes(5));
        Assert.False(r.Acquired);
        Assert.Equal("alice", r.Lease.HolderUserId);
        Assert.Equal("scene:1", r.Lease.ResourceKey);
    }

    [Fact]
    public async Task Expired_lease_allows_other_user()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMilliseconds(30));
        await Task.Delay(80);
        var r = await _sut.TryAcquireAsync("proj1", "scene:1", "bob", TimeSpan.FromMinutes(5));
        Assert.True(r.Acquired);
        Assert.Equal("bob", r.Lease.HolderUserId);
    }

    [Fact]
    public async Task Release_allows_other_user()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        var released = await _sut.ReleaseAsync("proj1", "scene:1", "alice");
        Assert.True(released);
        var r = await _sut.TryAcquireAsync("proj1", "scene:1", "bob", TimeSpan.FromMinutes(5));
        Assert.True(r.Acquired);
        Assert.Equal("bob", r.Lease.HolderUserId);
    }

    [Fact]
    public async Task Release_by_non_holder_is_noop()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        var released = await _sut.ReleaseAsync("proj1", "scene:1", "bob");
        Assert.False(released);
        var still = await _sut.TryAcquireAsync("proj1", "scene:1", "bob", TimeSpan.FromMinutes(5));
        Assert.False(still.Acquired);
        Assert.Equal("alice", still.Lease.HolderUserId);
    }

    [Fact]
    public async Task Independent_resources_do_not_conflict()
    {
        var a = await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        var b = await _sut.TryAcquireAsync("proj1", "scene:2", "bob", TimeSpan.FromMinutes(5));
        Assert.True(a.Acquired);
        Assert.True(b.Acquired);
    }

    [Fact]
    public async Task List_returns_only_unexpired()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        await _sut.TryAcquireAsync("proj1", "scene:2", "bob", TimeSpan.FromMilliseconds(20));
        await Task.Delay(60);
        var active = await _sut.ListAsync("proj1");
        Assert.Single(active);
        Assert.Equal("scene:1", active[0].ResourceKey);
        Assert.Equal("alice", active[0].HolderUserId);
    }

    [Fact]
    public async Task ReleaseAllForUser_clears_holder_only()
    {
        await _sut.TryAcquireAsync("proj1", "scene:1", "alice", TimeSpan.FromMinutes(5));
        await _sut.TryAcquireAsync("proj1", "script", "alice", TimeSpan.FromMinutes(5));
        await _sut.TryAcquireAsync("proj1", "scene:2", "bob", TimeSpan.FromMinutes(5));
        var n = await _sut.ReleaseAllForUserAsync("proj1", "alice");
        Assert.Equal(2, n);
        var active = await _sut.ListAsync("proj1");
        Assert.Single(active);
        Assert.Equal("bob", active[0].HolderUserId);
    }

    [Fact]
    public async Task Concurrent_acquires_only_one_wins()
    {
        var tasks = Enumerable.Range(0, 12)
            .Select(i => _sut.TryAcquireAsync("proj1", "scene:race", $"user{i}", TimeSpan.FromMinutes(5)))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        var winners = results.Where(r => r.Acquired).ToList();
        Assert.Single(winners);
        var holder = winners[0].Lease.HolderUserId;
        foreach (var r in results.Where(x => !x.Acquired))
            Assert.Equal(holder, r.Lease.HolderUserId);
    }
}
