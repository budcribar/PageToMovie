namespace PageToMovie.Engine;

/// <summary>
/// Bounded stream copy used by zip/upload ingest paths so a large payload cannot fill the disk.
/// </summary>
internal static class StreamCopy
{
    /// <param name="limitNoun">Subject in the overflow message (e.g. "Upload", "Zip file").</param>
    public static async Task CopyWithSizeCapAsync(
        Stream source,
        Stream dest,
        long maxBytes,
        CancellationToken ct,
        string limitNoun)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    $"{limitNoun} exceeds size limit ({maxBytes:N0} bytes).");
            await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }
}
