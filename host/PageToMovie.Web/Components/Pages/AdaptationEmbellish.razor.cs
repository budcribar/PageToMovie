using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationEmbellish
{

    public override string StepKey => "embellish";

    private async Task EmbellishAsync()
    {
        Busy = true;
        BusyMessage = "Enriching the screenplay…";
        Error = null;
        Message = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Engine.EmbellishScreenplayAsync(ProjectId);
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
