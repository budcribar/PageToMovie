using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Pages;

public partial class ScenesJoinRow
{
    [Parameter] public int BeforeScene { get; set; }
    [Parameter] public string Kind { get; set; } = "cut";
    [Parameter] public string? Card { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback<(int BeforeScene, string Kind, string? Card)> OnChanged { get; set; }

    private Task OnKindChanged(ChangeEventArgs e)
    {
        Kind = Convert.ToString(e.Value) ?? "cut";
        return OnChanged.InvokeAsync((BeforeScene, Kind, Card));
    }

    private Task OnCardChanged(ChangeEventArgs e)
    {
        Card = Convert.ToString(e.Value);
        return OnChanged.InvokeAsync((BeforeScene, Kind, Card));
    }
}
