using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Inlining a combined extend MP4 (C1+C2) as base64 during the next clip's
/// download is how a two-clip same-scene regen OOMs the API host.
/// </summary>
public sealed class ClipInlineMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptm-inline-" + Guid.NewGuid().ToString("N"));

    public ClipInlineMediaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public void EnsureFitsInline_rejects_oversize_without_reading_contents()
    {
        var path = Path.Combine(_dir, "scene_02_clip_02_take_01.mp4");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(ClipInlineMedia.MaxInlineBytes + 1);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => ClipInlineMedia.EnsureFitsInline(path));
        Assert.Contains("Refusing to load", ex.Message);
        Assert.Contains("cannot take down the host", ex.Message);
    }

    /// <summary>
    /// One inline ceiling, not two. The data-URI path (video-extend / video-edit) carried its own
    /// larger cap, so the guard added for the two-clip regen OOM did not actually bound the route
    /// that OOMed.
    /// </summary>
    [Fact]
    public void Inline_cap_is_the_same_everywhere() =>
        Assert.Equal(ClipInlineMedia.MaxInlineBytes, MediaDataUri.MaxBytes);

    [Fact]
    public async Task FileToDataUriAsync_does_not_buffer_an_oversize_video()
    {
        var path = Path.Combine(_dir, "combined-c1-c2.mp4");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(MediaDataUri.MaxBytes + 4096);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaDataUri.FileToDataUriAsync(path, CancellationToken.None));
        Assert.Contains("Refusing to load", ex.Message);
    }

    [Fact]
    public async Task FileToBase64Async_does_not_buffer_an_oversize_video()
    {
        var path = Path.Combine(_dir, "combined-c1-c2.mp4");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(ClipInlineMedia.MaxInlineBytes + 4096);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderMediaHelpers.FileToBase64Async(path, CancellationToken.None, allowVideo: true));
        Assert.Contains("Refusing to load", ex.Message);
    }

    [Fact]
    public async Task FileToBase64Async_inlines_a_small_file()
    {
        var path = Path.Combine(_dir, "tiny.png");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        await File.WriteAllBytesAsync(path, bytes);

        var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(path, CancellationToken.None);
        Assert.Equal("image/png", mime);
        Assert.Equal(Convert.ToBase64String(bytes), b64);
    }

    [Fact]
    public void EnsureFitsInline_rejects_missing_path()
    {
        Assert.Throws<InvalidOperationException>(() => ClipInlineMedia.EnsureFitsInline(""));
        Assert.Throws<InvalidOperationException>(
            () => ClipInlineMedia.EnsureFitsInline(Path.Combine(_dir, "no-such.mp4")));
    }
}
