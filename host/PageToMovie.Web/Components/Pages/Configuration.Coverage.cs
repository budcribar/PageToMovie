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

// Forwarders: ConfigurationCoverage → Host.*
public partial class Configuration
{
    internal void ParseFocusFromUri() => Coverage.ParseFocusFromUri();

    internal static string? NormalizeFocus(string? raw) => ConfigurationCoverage.NormalizeFocus(raw);

    internal IReadOnlyList<ConfigurationCoverage.CoverageRow> BuildCoverageRows() => Coverage.BuildCoverageRows();

    internal ConfigurationCoverage.CoverageRow MakeCoverage(
            string id,
            string label,
            string hint,
            string modelId,
            string capability,
            bool required,
            bool preferVideoReview = false,
            string? forceProviderId = null) => Coverage.MakeCoverage(id, label, hint, modelId, capability, required, preferVideoReview, forceProviderId);

    internal string ResolveProviderIdForModel(string modelId, string capability, bool preferVideoReview) => Coverage.ResolveProviderIdForModel(modelId, capability, preferVideoReview);

    internal List<string> CoverageUsesForProvider(string providerId) => Coverage.CoverageUsesForProvider(providerId);

    internal Task ChooseProviderForCoverageAsync(string providerId, string coverageId) => Coverage.ChooseProviderForCoverageAsync(providerId, coverageId);

    internal Task UseSavedProviderForCoverageAsync(string providerId, string coverageId) => Coverage.UseSavedProviderForCoverageAsync(providerId, coverageId);

    internal void AlignCoverageModelToProvider(string coverageId, string providerId) => Coverage.AlignCoverageModelToProvider(coverageId, providerId);

    internal string GetCoverageModelId(string coverageId) => Coverage.GetCoverageModelId(coverageId);

    internal void SetCoverageModelId(string coverageId, string modelId) => Coverage.SetCoverageModelId(coverageId, modelId);

    internal IReadOnlyList<SupportedModelDto> ModelsForCoverage(string coverageId) => Coverage.ModelsForCoverage(coverageId);

    internal IReadOnlyList<SupportedModelDto> ModelsForCoverageProvider(string coverageId, string? providerId) => Coverage.ModelsForCoverageProvider(coverageId, providerId);

    internal Task OnCoverageProviderChangedAsync(string coverageId, string? providerId) => Coverage.OnCoverageProviderChangedAsync(coverageId, providerId);

    internal IEnumerable<ProviderKeyStatusDto> ProvidersForCoverage(string coverageId) => Coverage.ProvidersForCoverage(coverageId);

    internal Task OnCoverageModelChangedAsync(string coverageId, string? modelId) => Coverage.OnCoverageModelChangedAsync(coverageId, modelId);

    internal Task TurnOffOptionalAsync(string coverageId) => Coverage.TurnOffOptionalAsync(coverageId);

    internal static string ModelOptionLabel(SupportedModelDto m) => ConfigurationCoverage.ModelOptionLabel(m);

    internal void EnsureVoiceModelForProvider(string providerId) => Coverage.EnsureVoiceModelForProvider(providerId);

    internal void EnsureMusicModelForProvider(string providerId) => Coverage.EnsureMusicModelForProvider(providerId);


    internal bool FocusActive => Coverage.FocusActive;
}
