using Microsoft.AspNetCore.Components;
using PageToMovie.Core.Models;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes_VerificationReport
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public int SceneNumber { get; set; }
    [Parameter] public int ClipNumber { get; set; }
    [Parameter] public ClipDialogueVerificationResult? Report { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
}
