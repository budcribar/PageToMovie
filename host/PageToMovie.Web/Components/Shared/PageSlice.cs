using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components;

/// <summary>
/// A page that was split into "slice" child components which share the page's state objects
/// through <c>IsFixed</c> cascading values (Home, Scenes). Blazor never re-renders such a child
/// when the page renders (its parameters are fixed), and an event handled inside a child never
/// re-renders the page — so a click in one slice (open a scene, delete a project) silently failed
/// to update the page or sibling slices. Implementing this on the page and deriving the slices
/// from <see cref="PageSliceComponent"/> restores the pre-split behaviour: any event anywhere on
/// the page re-renders the page and every slice.
/// </summary>
public interface IPageSliceHost
{
    /// <summary>Raised after every render of the host page; slices re-render on it.</summary>
    event Action? Rendered;

    /// <summary>A slice handled a UI event; the host page must re-render its own markup.</summary>
    void RenderRequestedBySlice();
}

/// <summary>
/// Base for page slices (see <see cref="IPageSliceHost"/>). Picks the host up as a cascading
/// value (the page cascades <c>this</c>), re-renders on the host's <c>Rendered</c>, and after any
/// UI event it handles asks the host to render too — the same as if the markup were still inline
/// on the page.
/// </summary>
public abstract class PageSliceComponent : ComponentBase, IHandleEvent, IDisposable
{
    [CascadingParameter] public IPageSliceHost? SliceHost { get; set; }

    private IPageSliceHost? _subscribedHost;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        if (ReferenceEquals(_subscribedHost, SliceHost)) return;
        if (_subscribedHost is not null) _subscribedHost.Rendered -= OnHostRendered;
        _subscribedHost = SliceHost;
        if (_subscribedHost is not null) _subscribedHost.Rendered += OnHostRendered;
    }

    private void OnHostRendered() => StateHasChanged();

    /// <summary>
    /// Mirrors ComponentBase's default event handling (render after the sync part, and again when
    /// an async handler completes) and additionally asks the host page to render at both points.
    /// </summary>
    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        var task = callback.InvokeAsync(arg);
        var shouldAwait = task.Status is not (TaskStatus.RanToCompletion or TaskStatus.Canceled);
        RenderSelfAndHost();
        if (!shouldAwait) return;
        try
        {
            await task;
        }
        catch
        {
            if (!task.IsCanceled) throw;
        }
        RenderSelfAndHost();
    }

    private void RenderSelfAndHost()
    {
        StateHasChanged();
        SliceHost?.RenderRequestedBySlice();
    }

    public virtual void Dispose()
    {
        if (_subscribedHost is not null) _subscribedHost.Rendered -= OnHostRendered;
        _subscribedHost = null;
        GC.SuppressFinalize(this);
    }
}
