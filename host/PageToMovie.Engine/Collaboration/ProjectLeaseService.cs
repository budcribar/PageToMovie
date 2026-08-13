using System.Text.Json;

namespace PageToMovie.Engine.Collaboration;

public sealed class ProjectLeaseService : IProjectLeaseService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string, string> _projectDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectLeaseService(ProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _projectDir = id => store.GetProjectDir(id);
    }

    /// <summary>
    /// Test / isolated-root constructor: leases live under
    /// <c>{workspaceRoot}/projects/{projectId}/leases/</c> and project dirs are created on demand.
    /// </summary>
    public ProjectLeaseService(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("workspaceRoot required", nameof(workspaceRoot));
        var root = workspaceRoot.Trim();
        _projectDir = id =>
        {
            var safe = (id ?? "").Trim().Replace('\\', '/').Trim('/');
            var dir = Path.Combine(root, "projects", safe.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            return dir;
        };
    }

    private string LeaseDir(string projectId) =>
        Path.Combine(_projectDir(projectId), "leases");

    private string LeasePath(string projectId, string resourceKey)
    {
        var safe = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName(resourceKey);
        return Path.Combine(LeaseDir(projectId), safe + ".json");
    }

    public async Task<ProjectLease?> GetAsync(string projectId, string resourceKey, CancellationToken ct = default)
    {
        string path;
        try { path = LeasePath(projectId, resourceKey); }
        catch { return null; }
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var lease = JsonSerializer.Deserialize<ProjectLease>(json, JsonOpts);
        if (lease is not null && lease.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            try { File.Delete(path); } catch { /* ignore */ }
            return null;
        }
        return lease;
    }

    public async Task<(bool Acquired, ProjectLease Lease)> TryAcquireAsync(
        string projectId, string resourceKey, string userId, TimeSpan ttl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = LeasePath(projectId, resourceKey);
            var leaseDir = Path.GetDirectoryName(path);
            if (leaseDir is not null)
                Directory.CreateDirectory(leaseDir);
            var existing = await GetAsync(projectId, resourceKey, ct).ConfigureAwait(false);
            if (existing is not null
                && !string.Equals(existing.HolderUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, existing);
            }

            var lease = new ProjectLease
            {
                ResourceKey = resourceKey,
                HolderUserId = userId,
                AcquiredAt = existing?.AcquiredAt ?? DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
            };
            await WriteLeaseAsync(path, lease, ct).ConfigureAwait(false);
            return (true, lease);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseAsync(string projectId, string resourceKey, string userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = LeasePath(projectId, resourceKey);
            var existing = await GetAsync(projectId, resourceKey, ct).ConfigureAwait(false);
            if (existing is null) return true;
            if (!string.Equals(existing.HolderUserId, userId, StringComparison.OrdinalIgnoreCase))
                return false;
            try { File.Delete(path); } catch { /* ignore */ }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Renewed, ProjectLease? Lease)> TryRenewAsync(
        string projectId, string resourceKey, string userId, TimeSpan ttl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = LeasePath(projectId, resourceKey);
            var existing = await GetAsync(projectId, resourceKey, ct).ConfigureAwait(false);
            if (existing is null
                || !string.Equals(existing.HolderUserId, userId, StringComparison.OrdinalIgnoreCase))
                return (false, existing);
            existing.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            await WriteLeaseAsync(path, existing, ct).ConfigureAwait(false);
            return (true, existing);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Transferred, ProjectLease? Lease)> TryTransferAsync(
        string projectId, string resourceKey, string fromUserId, string toUserId, TimeSpan ttl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = LeasePath(projectId, resourceKey);
            var existing = await GetAsync(projectId, resourceKey, ct).ConfigureAwait(false);
            if (existing is null
                || !string.Equals(existing.HolderUserId, fromUserId, StringComparison.OrdinalIgnoreCase))
                return (false, existing);
            existing.HolderUserId = toUserId;
            existing.AcquiredAt = DateTimeOffset.UtcNow;
            existing.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            await WriteLeaseAsync(path, existing, ct).ConfigureAwait(false);
            return (true, existing);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectLease>> ListAsync(string projectId, CancellationToken ct = default)
    {
        string dir;
        try { dir = LeaseDir(projectId); }
        catch { return Array.Empty<ProjectLease>(); }
        if (!Directory.Exists(dir))
            return Array.Empty<ProjectLease>();

        var list = new List<ProjectLease>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var lease = JsonSerializer.Deserialize<ProjectLease>(json, JsonOpts);
                if (lease is null) continue;
                if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                    continue;
                }
                list.Add(lease);
            }
            catch { /* skip bad file */ }
        }
        return list;
    }

    public async Task<int> ReleaseAllForUserAsync(string projectId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryGetExistingLeaseDir(projectId, out var dir)) return 0;
            var n = 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                n += await TryReleaseLeaseFileAsync(path, userId, ct).ConfigureAwait(false);
            }
            return n;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetExistingLeaseDir(string projectId, out string dir)
    {
        try { dir = LeaseDir(projectId); }
        catch { dir = ""; return false; }
        if (!Directory.Exists(dir))
        {
            dir = "";
            return false;
        }
        return true;
    }

    private static async Task<int> TryReleaseLeaseFileAsync(string path, string userId, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var lease = JsonSerializer.Deserialize<ProjectLease>(json, JsonOpts);
            if (lease is null) return 0;
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                TryDeleteLeaseFile(path);
                return 0;
            }
            if (!string.Equals(lease.HolderUserId, userId, StringComparison.OrdinalIgnoreCase))
                return 0;
            return TryDeleteLeaseFile(path) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDeleteLeaseFile(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    private static async Task WriteLeaseAsync(string path, ProjectLease lease, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(lease, JsonOpts), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
