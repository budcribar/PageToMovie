using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Shared;

public partial class VoiceCaptureStep_Controls
{
    [CascadingParameter] public required VoiceCaptureStep Host { get; set; }

    private Task OnRecordAsync() => Host.RecordAsync();
}
