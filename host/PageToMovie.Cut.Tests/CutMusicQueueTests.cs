using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutMusicQueueTests
{
    [Fact]
    public void Adding_music_while_composing_does_not_forget_or_clear_the_merge()
    {
        var queue = new CutMusicQueue();
        var forgot = false;

        queue.AttachFile(composing: true, () => forgot = true);

        Assert.False(forgot);
        Assert.False(CutMusicQueue.ShouldForgetPreview(composing: true));
        Assert.False(CutMusicQueue.ShouldCancelCompose(composing: true));
        Assert.False(CutMusicQueue.ShouldClearMerge(composing: true));
        Assert.False(CutMusicQueue.ShouldRebuildPictureOnMixEdit(composing: true));
        Assert.True(queue.IsQueued);
        Assert.Equal(CutMusicQueue.QueuedMessage, queue.Status);
        Assert.True(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: true));
        Assert.False(queue.ShouldMixAfterCompose(composeSucceeded: false, hasAudio: true));
        Assert.False(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: false));
    }

    [Fact]
    public void Idle_add_is_the_forget_preview_mix_now_path()
    {
        var queue = new CutMusicQueue();
        var forgot = 0;
        queue.AttachFile(composing: false, () => forgot++);

        Assert.Equal(1, forgot);
        Assert.True(CutMusicQueue.ShouldForgetPreview(composing: false));
        Assert.True(CutMusicQueue.ShouldRebuildPictureOnMixEdit(composing: false));
        Assert.False(queue.IsQueued);
        Assert.Null(queue.Status);
        Assert.False(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: true));
    }

    [Fact]
    public void Replace_while_queued_keeps_one_track_and_the_mix_after_compose_path()
    {
        var queue = new CutMusicQueue();
        var forgot = 0;
        queue.AttachFile(composing: true, () => forgot++);
        queue.ReplaceFile(composing: true, () => forgot++);

        Assert.Equal(0, forgot);
        Assert.True(queue.IsQueued);
        Assert.True(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: true));
        Assert.Equal(CutMusicQueue.QueuedMessage, queue.StatusAfterCompose(true));
    }

    [Fact]
    public void Clear_during_compose_drops_the_queue_and_does_not_mix()
    {
        var queue = new CutMusicQueue();
        var forgot = 0;
        queue.AttachFile(composing: true, () => forgot++);
        queue.Remove(composing: true, () => forgot++);

        Assert.Equal(0, forgot);
        Assert.False(queue.IsQueued);
        Assert.False(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: false));
        Assert.Null(queue.Status);
    }

    [Fact]
    public void Volume_or_fade_during_combine_waits_for_the_mix_pass()
    {
        var queue = new CutMusicQueue();
        var forgot = 0;
        queue.ChangeMix(composing: true, () => forgot++);

        Assert.Equal(0, forgot);
        Assert.True(queue.IsQueued);
        Assert.False(CutMusicQueue.ShouldRebuildPictureOnMixEdit(composing: true));
        Assert.True(queue.ShouldMixAfterCompose(composeSucceeded: true, hasAudio: true));
    }

    [Fact]
    public void Failed_combine_keeps_the_file_and_says_mix_is_waiting()
    {
        var queue = new CutMusicQueue();
        queue.AttachFile(composing: true);
        Assert.Equal(CutMusicQueue.WaitingMessage, queue.StatusAfterCompose(false));
        Assert.True(queue.IsQueued);
        Assert.False(queue.ShouldMixAfterCompose(composeSucceeded: false, hasAudio: true));
    }

    [Fact]
    public void Successful_mix_clears_the_queue()
    {
        var queue = new CutMusicQueue();
        queue.AttachFile(composing: true);
        Assert.True(queue.ShouldMixAfterCompose(true, true));
        queue.BeginMix();
        Assert.Null(queue.Status);
        queue.MarkMixed();
        Assert.False(queue.IsQueued);
        Assert.False(queue.IsMixing);
        Assert.False(queue.ShouldMixAfterCompose(true, true));
    }

    [Fact]
    public void Stopping_compose_drops_the_queue_so_next_play_uses_the_idle_path()
    {
        var queue = new CutMusicQueue();
        queue.AttachFile(composing: true);
        queue.OnComposeCancelled();
        Assert.False(queue.IsQueued);
        Assert.False(queue.ShouldMixAfterCompose(true, true));
    }
}
