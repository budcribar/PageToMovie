using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;

namespace PageToMovie.Web.Components;

public partial class LanguageSelector
{
    [Parameter] public string WrapperClass { get; set; } = "";

    protected override void OnInitialized()
    {
        Localizer.CultureChanged += OnCultureChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var saved = await JS.InvokeAsync<string?>("localStorage.getItem", "pagetomovie_culture");
                if (!string.IsNullOrWhiteSpace(saved) && saved != Localizer.CurrentCulture.Name)
                {
                    Localizer.SetCulture(saved);
                    StateHasChanged();
                }
            }
            catch
            {
                // Ignore JS interop errors during prerendering
            }
        }
    }

    private async Task OnLanguageChangedAsync(ChangeEventArgs e)
    {
        var code = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(code)) return;

        Localizer.SetCulture(code);
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", "pagetomovie_culture", code);
        }
        catch
        {
            // Ignore storage errors
        }
        StateHasChanged();
    }

    private void OnCultureChanged(CultureInfo culture)
    {
        InvokeAsync(StateHasChanged);
    }

    private static string GetLanguageDisplayName(string cultureCode) => cultureCode.ToLowerInvariant() switch
    {
        "en-us" or "en" => "🇺🇸 English",
        "es" or "es-es" or "es-mx" => "🇪🇸 Español",
        "fr" or "fr-fr" => "🇫🇷 Français",
        "de" or "de-de" => "🇩🇪 Deutsch",
        _ => cultureCode,
    };

    public void Dispose()
    {
        Localizer.CultureChanged -= OnCultureChanged;
    }
}
