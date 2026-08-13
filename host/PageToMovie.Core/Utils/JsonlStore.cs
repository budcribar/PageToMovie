using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PageToMovie.Core.Utils;

/// <summary>
/// Thread-safe generic template for atomic JSONL (JSON Lines) file append operations.
/// Replaces duplicated directory creation, JSON serialization, and gate locking across log stores.
/// </summary>
public static class JsonlStore
{
    private static readonly KeyedAsyncLock<string> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Appends a single JSONL record asynchronously with path-level lock synchronization.</summary>
    public static async Task AppendAsync<T>(
        string path,
        T record,
        JsonSerializerOptions? opts = null,
        SemaphoreSlim? gate = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var line = JsonSerializer.Serialize(record, opts) + "\n";

        if (gate is not null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            using (await FileLocks.LockAsync(path, ct).ConfigureAwait(false))
            {
                await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
            }
        }
    }
}
