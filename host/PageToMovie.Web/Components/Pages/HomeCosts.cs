using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Home
{
    /// <summary>Costs domain for the Home page. Owns related UI state and behavior.</summary>
    public sealed class HomeCosts
    {
        private readonly Home S;
        public HomeCosts(Home host) => S = host;

        /// <summary>Ledger actual spend for this project (catalog list rates).</summary>
            internal double? _costActualUsd;

        /// <summary>Planning estimate for the full film (draft path) from cost report.</summary>
            internal double? _costEstimateUsd;

        internal bool _costLoading;

        /// <summary>True when no generation model is chosen yet, so an estimate can't be produced.</summary>
            internal bool _costNeedsModels;

        internal string? _costResolution;

        internal double? _costVideoRate;

        /// <summary>Titles of real public demos (never invented marketing names).</summary>
            internal string _demoShowcaseHint = "Loading gallery…";

        internal List<DemoListItem> _publicDemos = new();


        internal static string FormatUsd(double? amount)
        {
            if (amount is null) return "—";
            return amount.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        }


        internal async Task RefreshProjectCostAsync()
        {
            var pid = S.ActiveProject.ProjectId ?? S.Projects._projects?.Active?.Id;
            if (string.IsNullOrWhiteSpace(pid) || !S.Session.IsLoggedIn)
            {
                _costEstimateUsd = null;
                _costActualUsd = null;
                _costResolution = null;
                _costVideoRate = null;
                _costLoading = false;
                S.Jobs._packageStatus = null;
                return;
            }

            _costLoading = true;
            try
            {
                // Match film resolution so Home estimate tracks 480p / 720p / 1080p rates.
                string? draftRes = null;
                var hasModel = true; // assume yes unless the config clearly says no model is chosen
                try
                {
                    var cfg = await S.Engine.GetConfigAsync(pid);
                    if (cfg?.Config is not null)
                    {
                        hasModel = cfg.Config.TryGetValue("model_name", out var mnEl)
                            && mnEl.ValueKind == System.Text.Json.JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(mnEl.GetString());
                        if (cfg.Config.TryGetValue("resolution", out var resEl) &&
                            resEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            draftRes = resEl.GetString();
                    }
                }
                catch { /* config unavailable → assume a model may exist and let the fetch decide */ }

                if (!hasModel)
                {
                    // No generation model chosen yet (set on the Configuration page). The estimate fails
                    // fast by design — skip the call entirely so it never errors, and show a setup hint.
                    _costNeedsModels = true;
                    _costEstimateUsd = null;
                    _costActualUsd = null;
                    _costResolution = null;
                    _costVideoRate = null;
                    return;
                }
                _costNeedsModels = false;

                var dto = await S.Engine.GetCostAsync(pid, draftResolution: draftRes);
                var s = dto?.Cost?.Summary;
                _costEstimateUsd = s?.FullFilmAllDraftUsd;
                _costActualUsd = s?.ActualUsd;
                _costResolution = dto?.Cost?.DraftResolution ?? draftRes;
                _costVideoRate = dto?.Cost?.OutputRateDraft;
            }
            catch
            {
                // Keep prior numbers if refresh fails mid-session
            }
            finally
            {
                _costLoading = false;
            }

            await S.Jobs.RefreshPackageStatusAsync();
        }



        /// <summary>Fill the home gallery blurb from real public demos only.</summary>
        internal async Task LoadDemoShowcaseAsync()
        {
            try
            {
                // ListDemos triggers throttled channel sync — cards are real YouTube uploads only.
                var all = await S.Engine.ListDemosAsync(12, "new");
                _publicDemos = all
                    .Where(d => !string.IsNullOrWhiteSpace(d.YoutubeId) && !string.IsNullOrWhiteSpace(d.Title))
                    .Take(4)
                    .ToList();
                if (_publicDemos.Count == 0)
                {
                    _demoShowcaseHint = "No films on the Page to Movie channel yet";
                }
                else
                {
                    var titles = _publicDemos
                        .Select(d => (d.Title ?? d.ProjectId ?? "").Trim())
                        .Where(s => s.Length > 0)
                        .Take(4)
                        .ToList();
                    if (titles.Count == 0)
                        _demoShowcaseHint = $"{_publicDemos.Count} film{(_publicDemos.Count == 1 ? "" : "s")} on the wall";
                    else if (_publicDemos.Count > titles.Count)
                        _demoShowcaseHint = string.Join(", ", titles) + $" · +{_publicDemos.Count - titles.Count} more";
                    else
                        _demoShowcaseHint = string.Join(", ", titles);
                }
            }
            catch
            {
                _demoShowcaseHint = "Community films made with PageToMovie";
            }
        }

    }
}
