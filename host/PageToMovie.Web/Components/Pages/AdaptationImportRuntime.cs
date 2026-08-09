namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport
{
    /// <summary>Film length / runtime knobs for Adaptation Import.</summary>
    internal sealed class ImportRuntime
    {
        private readonly AdaptationImport S;
        public ImportRuntime(AdaptationImport host) => S = host;

        internal int _targetMinutesEdit = 5;
        internal bool _savingRuntime;
        internal string? _runtimeMessage;

        internal void SyncTargetEditFromStatus()
        {
            var tmin = S.Status?.Book.TargetRuntimeMinutes
                ?? S.Status?.Book.SuggestedTotalMinutes
                ?? S.Status?.Book.NaturalRuntimeMinutes
                ?? S.TotalMinutes;
            _targetMinutesEdit = Math.Clamp(tmin, 2, 180);
        }

        internal async Task SaveFilmRuntimeAsync()
        {
            if (string.IsNullOrWhiteSpace(S.ProjectId)) return;
            _savingRuntime = true;
            _runtimeMessage = null;
            S.Error = null;
            try
            {
                var dto = await S.Engine.SetFilmRuntimeAsync(S.ProjectId, _targetMinutesEdit);
                if (dto is null || !dto.Ok)
                    throw new InvalidOperationException("Could not save film length.");
                _runtimeMessage = dto.Message ?? $"Target set to {dto.TargetMinutes} min.";
                S.TotalMinutes = dto.TargetMinutes;
                await S.LoadAsync();
                SyncTargetEditFromStatus();
            }
            catch (Exception ex)
            {
                S.Error = ex.Message;
            }
            finally
            {
                _savingRuntime = false;
            }
        }

        internal async Task ResetFilmRuntimeNaturalAsync()
        {
            var natural = S.Status?.Book.NaturalRuntimeMinutes
                ?? S.Status?.Book.SuggestedTotalMinutes
                ?? _targetMinutesEdit;
            _targetMinutesEdit = Math.Clamp(natural, 2, 180);
            await SaveFilmRuntimeAsync();
        }

        internal async Task OnFilmLengthChangedAsync()
        {
            try
            {
                await S.LoadAsync();
                SyncTargetEditFromStatus();
            }
            catch { /* ignore */ }
        }
    }
}
