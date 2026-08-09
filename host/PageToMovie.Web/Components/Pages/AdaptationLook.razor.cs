using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationLook
{

    public override string StepKey => "look";

    private async Task OnLookChangedAsync()
    {
        // Saving the medium also re-applies the look to an existing screenplay draft — one action, no
        // second button. Nothing to re-apply if the screenplay hasn't been drafted yet (medium is just
        // stored and applied when it is written).
        if (Status?.Screenplay.DraftExists == true)
        {
            await ReskinAsync();
            return;
        }
        try { await SoftLoadAsync(); } catch { /* ignore */ }
    }

    private async Task ReskinAsync()
    {
        Busy = true;
        BusyMessage = "Applying the look to your screenplay…";
        Error = null;
        Message = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Engine.ReskinScreenplayAsync(ProjectId);
            Message = result?.Message ?? "Done.";
            await SoftLoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }
}
