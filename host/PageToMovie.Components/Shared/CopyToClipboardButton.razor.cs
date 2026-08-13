using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public sealed partial class CopyToClipboardButton : IAsyncDisposable, IDisposable
{
    [Parameter] public string Text { get; set; } = "";
    [Parameter] public string Label { get; set; } = "Copy";
    [Parameter] public string CopiedLabel { get; set; } = "Copied";
    [Parameter] public string CssClass { get; set; } = "btn btn-sm btn-outline-secondary";
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? TestId { get; set; }
    [Parameter] public int ResetMs { get; set; } = 1500;

    bool _copied;
    CancellationTokenSource? _resetCts;

    async Task CopyAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", Text);
        }
        catch
        {
            /* clipboard may be blocked — non-fatal */
        }

        if (_resetCts is { } oldCts)
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }
        _resetCts = new CancellationTokenSource();
        var cts = _resetCts;
        var ct = cts.Token;
        _copied = true;
        try
        {
            await Task.Delay(ResetMs, ct);
            _copied = false;
        }
        catch (OperationCanceledException)
        {
            /* superseded by another copy */
        }
        finally
        {
            if (ReferenceEquals(_resetCts, cts))
            {
                cts.Dispose();
                _resetCts = null;
            }
        }
    }

    public void Dispose()
    {
        if (_resetCts is { } oldCts)
        {
            try
            {
                oldCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Cancel on an already-disposed CTS is a no-op; Dispose still runs below.
            }
            oldCts.Dispose();
            _resetCts = null;
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_resetCts is { } oldDisposeCts)
        {
            await oldDisposeCts.CancelAsync();
            oldDisposeCts.Dispose();
        }
        _resetCts = null;
        await ValueTask.CompletedTask;
    }
}
