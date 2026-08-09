namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationImport
{
    /// <summary>Planning model gate / continue readiness for Adaptation Import.</summary>
    internal sealed class ImportGate
    {
        private readonly AdaptationImport S;
        public ImportGate(AdaptationImport host) => S = host;

        internal string _importBlockedReason = "Choose a Script & planning model in Settings for this project.";

        /// <summary>True when this project has a planning model and a usable AI key.</summary>
        internal bool ImportReady =>
            S.Status is not null
            && S.Status.XaiConfigured
            && IsUsablePlanningModel(S.Status.PlanningModel);

        internal static bool IsUsablePlanningModel(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var s = id.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        internal bool CanContinueToScreenplay =>
            S.Status is not null &&
            (S.Status.Screenplay.DraftExists ||
             (S.Status.Stage1.Present && S.Status.Stage1.SceneCount > 0));

        internal void RefreshImportGate()
        {
            if (S.Status is null)
            {
                _importBlockedReason = "Loading project…";
                return;
            }
            if (!S.Status.XaiConfigured)
            {
                _importBlockedReason =
                    "No AI key on this account. Open Settings and add a key for Script & planning (xAI, OpenAI, Anthropic, Google, …).";
                return;
            }
            if (string.IsNullOrWhiteSpace(S.Status.PlanningModel) || !IsUsablePlanningModel(S.Status.PlanningModel))
            {
                _importBlockedReason =
                    "Script & planning: no model selected for this project. Open Settings → Studio coverage → Script & planning, choose a model, then come back here.";
                return;
            }
            _importBlockedReason = "";
            // Keep base Model in sync so StartBookImportAsync sends the chosen id.
            if (IsUsablePlanningModel(S.Status.PlanningModel))
                S.Model = S.Status.PlanningModel;
        }
    }
}
