using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ActionCameraOverheadLedgerTests
{
    [Fact]
    public void GetOverheadSec_ReturnsEmpiricalBenchmarkValue()
    {
        Assert.Equal(1.6, ActionCameraOverheadLedger.GetOverheadSec("cam_push_in"));
        Assert.Equal(2.0, ActionCameraOverheadLedger.GetOverheadSec("act_knife_pull"));
        Assert.Equal(3.1, ActionCameraOverheadLedger.GetOverheadSec("act_stabbing"));
    }

    [Fact]
    public void GetCompositeEntry_ReturnsDocSection2WorkedExample()
    {
        var ledger = new ActionCameraOverheadLedger();

        // Doc §2 worked example: act_pills_sorting + concurrent -> 2.3s, gamma=0.85
        var entry = ledger.GetCompositeEntry("cam_push_in", "act_pills_sorting", "concurrent");

        Assert.Equal("cam_push_in", entry.CameraId);
        Assert.Equal("act_pills_sorting", entry.ActionId);
        Assert.Equal("concurrent", entry.ConcurrencyMode);
        Assert.Equal(2.3, entry.BaseOverheadSec);
        Assert.Equal(0.85, entry.OverlapRatioGamma);
    }

    [Fact]
    public void CalculateEffectiveSpeechWindowSec_DeductsCameraAndActionOverheads_SerialMode()
    {
        var ledger = new ActionCameraOverheadLedger();

        // 5.0s clip - 1.6s push-in - (1.0 * 2.0s knife pull) = 1.4s remaining for speech
        double speechWindow = ledger.CalculateEffectiveSpeechWindowSec(
            totalClipDurationSec: 5.0,
            cameraCategoryId: "cam_push_in",
            actionCategoryId: "act_knife_pull",
            concurrencyFactorGamma: 0.0);

        Assert.Equal(1.4, speechWindow, 1);
    }

    [Fact]
    public void CalculateEffectiveSpeechWindowSec_AppliesConcurrencyFactor_ConcurrentMode()
    {
        var ledger = new ActionCameraOverheadLedger();

        // 5.0s clip - 1.6s push-in - ((1 - 0.85) * 2.3s pills sorting) = 5.0 - 1.6 - 0.345 = 3.055s
        double speechWindow = ledger.CalculateEffectiveSpeechWindowSec(
            totalClipDurationSec: 5.0,
            cameraCategoryId: "cam_push_in",
            actionCategoryId: "act_pills_sorting",
            concurrencyFactorGamma: 0.85);

        Assert.Equal(3.055, speechWindow, 3);
    }

    [Fact]
    public void ActionConcurrencyAnalyzer_ExtractsCameraActionModeAndGamma()
    {
        var concurrentResult = ActionConcurrencyAnalyzer.AnalyzeBeat("Pacing nervously across the room", "(while sorting pills)");
        Assert.Equal("cam_push_in", concurrentResult.CameraId);
        Assert.Equal("act_pills_sorting", concurrentResult.ActionId);
        Assert.Equal("concurrent", concurrentResult.Mode);
        Assert.Equal(0.85, concurrentResult.OverlapRatioGamma);

        var serialResult = ActionConcurrencyAnalyzer.AnalyzeBeat("Fast whip pan to character as he reaches into jacket and pulls out switchblade", "(pauses, then speaks)");
        Assert.Equal("cam_whip_pan", serialResult.CameraId);
        Assert.Equal("act_knife_pull", serialResult.ActionId);
        Assert.Equal("serial", serialResult.Mode);
        Assert.Equal(0.0, serialResult.OverlapRatioGamma);
    }
}
