using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PageToMovie.Core.Utils;

/// <summary>
/// Stream-based generic JSON persistence template for non-blocking file loading and saving.
/// Eliminates repeated FileStream creation, buffer allocation, and JsonSerializer calls.
/// </summary>
public static class StreamJsonStore
{
    private static readonly byte[] NewLineBytes = new byte[] { (byte)'\n' };

    /// <summary>
    /// Writes to a temp file then atomically renames onto <paramref name="path"/> — a crash or
    /// cancelled request mid-write must never leave the target file truncated, since callers
    /// (and mtime-validated caches reading the same path) treat "file exists" as "file is a
    /// complete, valid write."
    /// </summary>
    public static async Task SaveAsync<T>(
        string path,
        T data,
        JsonSerializerOptions? opts = null,
        bool appendNewLine = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, data, opts, ct).ConfigureAwait(false);
            if (appendNewLine)
            {
                await stream.WriteAsync(NewLineBytes, ct).ConfigureAwait(false);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<T?> LoadAsync<T>(
        string path,
        JsonSerializerOptions? opts = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return default;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, opts, ct).ConfigureAwait(false);
        }
        catch
        {
            return default;
        }
    }
}
