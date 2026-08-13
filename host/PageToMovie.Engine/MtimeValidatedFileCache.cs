using System.Collections.Concurrent;

namespace PageToMovie.Engine;

/// <summary>
/// External read/write coordination hook for <see cref="MtimeValidatedFileCache{T,S}"/>. A no-op
/// when nothing mutates the file in place outside its own atomic write-then-rename (mtime/length
/// alone is self-correcting there — see ProjectReadCache.GetOrLoadJsonDocumentAsync's contract).
/// A real gate when the cache must serialize its read against a writer that appends to the same
/// file in place (e.g. an append-only log guarded by the writer's own SemaphoreSlim) so a read
/// can never land mid-write.
/// </summary>
public interface ISemaphore
{
    Task WaitAsync(CancellationToken ct = default);
    void Release();
}

/// <summary>Zero-cost: no external writer to coordinate with.</summary>
public readonly struct NoOpSemaphore : ISemaphore
{
    public Task WaitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Release() { }
}

/// <summary>
/// Wraps a real <see cref="SemaphoreSlim"/> — pass the writer's own gate instance so cache reads
/// and the writer's appends never interleave.
/// </summary>
public readonly struct RealSemaphore : ISemaphore
{
    private readonly SemaphoreSlim _gate;
    public RealSemaphore(SemaphoreSlim gate) => _gate = gate;
    public Task WaitAsync(CancellationToken ct = default) => _gate.WaitAsync(ct);
    public void Release() => _gate.Release();
}

/// <summary>
/// Single-file, mtime+length-validated cache. Shared value returned by <see cref="GetOrLoadAsync"/>
/// — treat as read-only, do not mutate or dispose. Reparsed only when the file's mtime/length
/// changes since the last load, single-flighted per path so concurrent misses on the same file
/// don't duplicate the read+parse work.
///
/// <typeparamref name="S"/> is the external read/write coordination strategy (see
/// <see cref="ISemaphore"/>): constrained to <c>struct</c> so the common <see cref="NoOpSemaphore"/>
/// case has no virtual dispatch and no allocation — the JIT specializes per concrete <c>S</c>.
/// Use <see cref="NoOpSemaphore"/> when nothing else writes the file in place (atomic
/// write-then-rename callers, e.g. dialogue verification results). Use <see cref="RealSemaphore"/>,
/// resolved to the writer's own per-path gate, when the file is mutated in place by an appender
/// this cache doesn't own (e.g. telemetry JSONL logs) — otherwise a read can land mid-append.
/// </summary>
public sealed class MtimeValidatedFileCache<T, S>
    where T : class
    where S : struct, ISemaphore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, S> _resolveReadGate;
    private readonly Action<T>? _onEvicted;

    /// <param name="resolveReadGate">
    /// Per-path external read/write coordination gate, keyed by the file's full path. Defaults to
    /// <c>default(S)</c> (a no-op for <see cref="NoOpSemaphore"/>) when omitted. Pass a resolver
    /// that looks up (or shares) a writer's own <see cref="SemaphoreSlim"/> when this cache reads
    /// a file something else appends to concurrently.
    /// </param>
    /// <param name="onEvicted">Called with the superseded value when a cache entry is replaced or removed.</param>
    public MtimeValidatedFileCache(Func<string, S>? resolveReadGate = null, Action<T>? onEvicted = null)
    {
        _resolveReadGate = resolveReadGate ?? (_ => default);
        _onEvicted = onEvicted;
    }

    public async Task<T?> GetOrLoadAsync(
        string? absolutePath,
        Func<byte[], CancellationToken, Task<T>> parse,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            return null;

        FileInfo fi;
        try { fi = new FileInfo(absolutePath); }
        catch { return null; }
        var key = fi.FullName;

        if (_entries.TryGetValue(key, out var hit) &&
            hit.Ticks == fi.LastWriteTimeUtc.Ticks && hit.Length == fi.Length)
            return hit.Value;

        var buildGate = _buildLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await buildGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { fi.Refresh(); }
            catch { return null; }

            if (_entries.TryGetValue(key, out hit) &&
                hit.Ticks == fi.LastWriteTimeUtc.Ticks && hit.Length == fi.Length)
                return hit.Value;

            byte[] bytes;
            var readGate = _resolveReadGate(key);
            await readGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bytes = await File.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);
            }
            finally
            {
                readGate.Release();
            }

            var value = await parse(bytes, ct).ConfigureAwait(false);

            if (_entries.TryRemove(key, out var old) && old.Value is not null)
                _onEvicted?.Invoke(old.Value);

            _entries[key] = new Entry
            {
                Ticks = fi.LastWriteTimeUtc.Ticks,
                Length = fi.Length,
                Value = value,
            };
            return value;
        }
        finally
        {
            buildGate.Release();
        }
    }

    /// <summary>Drop a cached entry (e.g. after an out-of-band delete). Not required after a
    /// normal write — the mtime/length check above already self-corrects.</summary>
    public void Invalidate(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        string key;
        try { key = new FileInfo(absolutePath).FullName; }
        catch { return; }
        if (_entries.TryRemove(key, out var old) && old.Value is not null)
            _onEvicted?.Invoke(old.Value);
    }

    public void InvalidateUnder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            var root = Path.GetFullPath(dir);
            foreach (var key in _entries.Keys.ToArray())
            {
                if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    _entries.TryRemove(key, out var old) && old.Value is not null)
                    _onEvicted?.Invoke(old.Value);
            }
        }
        catch { /* best-effort */ }
    }

    private sealed class Entry
    {
        public long Ticks { get; init; }
        public long Length { get; init; }
        public T Value { get; init; } = default!;
    }
}
