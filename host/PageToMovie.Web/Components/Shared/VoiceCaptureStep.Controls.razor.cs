using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components.Shared;

public partial class VoiceCaptureStep_Controls
{
    [CascadingParameter] public required VoiceCaptureStep Host { get; set; }

    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool Recording { get; set; }
    [Parameter] public int Light { get; set; }
    [Parameter] public bool Listening { get; set; }
    [Parameter] public bool HasTake { get; set; }
    [Parameter] public int KeptCount { get; set; }
    [Parameter] public int PhraseIndex { get; set; }
    [Parameter] public int PhraseCount { get; set; }

    private Task OnRecordAsync() => Host.RecordAsync();
    private Task OnStopAsync() => Host.StopAndScoreAsync();
    private Task OnListenAsync() => Host.ListenAsync();
    private Task OnCancelAsync() => Host.CancelRecordAsync();
    private Task OnKeepAsync() => Host.KeepAndNextAsync();
    private Task OnFinishAsync() => Host.FinishAsync();
}
