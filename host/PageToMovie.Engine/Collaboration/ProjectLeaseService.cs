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

    private readonly ProjectStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectLeaseService(ProjectStore store) => _store = store;

    private string LeasePath(string projectId, string resourceKey)
    {
        var safe = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName(resourceKey);
        return Path.Combine(_store.GetProjectDir(projectId), "leases", safe + ".json");
    }

    public async Task<ProjectLease?> GetAsync(string projectId, string resourceKey, CancellationToken ct = default)
    {
        var path = LeasePath(projectId, resourceKey);
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static async Task WriteLeaseAsync(string path, ProjectLease lease, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(lease, JsonOpts), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
