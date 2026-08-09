using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class TermsAgreementModal
{

    [Parameter] public bool IsOpen { get; set; } = false;
    [Parameter] public string UserId { get; set; } = "";
    [Parameter] public EventCallback OnAccepted { get; set; }

    private bool hasAgreed = false;
    private bool isSubmitting = false;

    private async Task AcceptTerms()
    {
        if (!hasAgreed || string.IsNullOrWhiteSpace(UserId)) return;
        isSubmitting = true;
        try
        {
            await Http.PostAsJsonAsync("/api/users/terms/accept", new { UserId, Version = "1.0" });
            IsOpen = false;
            if (OnAccepted.HasDelegate)
            {
                await OnAccepted.InvokeAsync();
            }
        }
        catch
        {
            // Fallback for offline / direct callback
            IsOpen = false;
            if (OnAccepted.HasDelegate)
            {
                await OnAccepted.InvokeAsync();
            }
        }
        finally
        {
            isSubmitting = false;
        }
    }
}
