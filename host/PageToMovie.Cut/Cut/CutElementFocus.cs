using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PageToMovie.Cut.Cut;

/// <summary>
/// Focus a timeline control only after its <see cref="ElementReference"/> is bound.
/// Unmounted @ref values throw <see cref="InvalidOperationException"/>, not
/// <see cref="JSException"/> — skip those instead of crashing.
/// </summary>
public static class CutElementFocus
{
    public static bool IsReady(in ElementReference element) =>
        element.Context is not null;

    public static async Task TryFocusAsync(ElementReference element)
    {
        if (!IsReady(element))
            return;
        try
        {
            await element.FocusAsync();
        }
        catch (JSException)
        {
            // Element may have been removed on the same render.
        }
        catch (InvalidOperationException)
        {
            // Context can go stale between the ready check and FocusAsync.
        }
    }
}
