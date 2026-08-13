namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    /// <summary>Look / Enrich / Fit length tools hosted on the screenplay card.</summary>
    public sealed class ScreenplayTools
    {
        private readonly AdaptationScreenplay S;
        public ScreenplayTools(AdaptationScreenplay host) => S = host;

        /// <summary>null | look | enrich | fit</summary>
        internal string? OpenPanel;

        internal double? EstimateUsd;

        internal bool LookDone => S.Status?.BookSubsteps?.LookDone ?? false;
        internal bool EnrichDone => S.Status?.BookSubsteps?.EnrichDone ?? false;
        internal bool FitDone => S.Status?.BookSubsteps?.FitLengthDone ?? false;
        internal double? FitMins => S.Status?.BookSubsteps?.FitLengthTargetMinutes;
        internal bool DraftExists =>
            (S.Status?.Screenplay.DraftExists ?? false)
            || !string.IsNullOrWhiteSpace(S.Editor._text);

        internal void Toggle(string panel)
        {
            OpenPanel = string.Equals(OpenPanel, panel, StringComparison.OrdinalIgnoreCase)
                ? null
                : panel;
        }

        internal void Open(string? panel)
        {
            if (string.IsNullOrWhiteSpace(panel)) return;
            OpenPanel = panel.Trim().ToLowerInvariant() switch
            {
                "look" or "medium" => "look",
                "enrich" or "embellish" => "enrich",
                "fit" or "trim" or "length" => "fit",
                _ => OpenPanel
            };
        }

        internal async Task OnLookChangedAsync()
        {
            // Saving the medium re-applies look to an existing draft (one action).
            if (S.Status?.Screenplay.DraftExists ?? false)
            {
                await ReskinAsync();
                return;
            }
            try { await S.SoftLoadAsync(); } catch { /* ignore */ }
        }

        internal async Task ReskinAsync()
        {
            S.Busy = true;
            S.BusyMessage = "Applying the look to your screenplay…";
            S.Error = null;
            S.Message = null;
            await S.InvokeAsync(S.StateHasChanged);
            try
            {
                var result = await S.Engine.ReskinScreenplayAsync(S.ProjectId);
                S.Message = result?.Message ?? "Done.";
                await S.SoftLoadAsync();
                await S.Editor.LoadEditorDataAsync();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                S.Busy = false;
                S.BusyMessage = null;
            }
        }

        internal async Task EmbellishAsync()
        {
            S.Busy = true;
            S.BusyMessage = "Enriching the screenplay…";
            S.Error = null;
            S.Message = null;
            await S.InvokeAsync(S.StateHasChanged);
            try
            {
                var result = await S.Engine.EmbellishScreenplayAsync(S.ProjectId);
                S.Message = result?.Message ?? "Done.";
                await S.SoftLoadAsync();
                await S.Editor.LoadEditorDataAsync();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                S.Busy = false;
                S.BusyMessage = null;
            }
        }

        internal async Task OnLengthChangedAsync()
        {
            try { await S.SoftLoadAsync(); } catch { /* ignore */ }
            await RefreshEstimateAsync();
        }

        internal async Task RefreshEstimateAsync()
        {
            if (string.IsNullOrWhiteSpace(S.ProjectId)) return;
            try
            {
                var dto = await S.Engine.GetCostAsync(S.ProjectId);
                EstimateUsd = dto?.Cost?.Summary?.FullFilmAllDraftUsd;
            }
            catch
            {
                EstimateUsd = null;
            }
            await S.InvokeAsync(S.StateHasChanged);
        }

        internal async Task TrimAsync()
        {
            S.Busy = true;
            S.BusyMessage = "Fitting the screenplay to length…";
            S.Error = null;
            S.Message = null;
            await S.InvokeAsync(S.StateHasChanged);
            try
            {
                var result = await S.Engine.TrimScreenplayAsync(S.ProjectId);
                S.Message = result?.Message ?? "Done.";
                await S.SoftLoadAsync();
                await S.Editor.LoadEditorDataAsync();
                await RefreshEstimateAsync();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                S.Busy = false;
                S.BusyMessage = null;
            }
        }
    }
}
