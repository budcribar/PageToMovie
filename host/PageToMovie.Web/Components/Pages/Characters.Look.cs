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

// Forwarders: CharactersLook → Host.*
public partial class Characters
{
    internal Task ToggleBookCandidateGalleryAsync() => Look.ToggleBookCandidateGalleryAsync();

    internal Task LoadBookCandidatesAsync() => Look.LoadBookCandidatesAsync();

    internal void ToggleBookCandidateSelection(string pathRel) => Look.ToggleBookCandidateSelection(pathRel);

    internal Task ApplySelectedBookCandidatesAsync() => Look.ApplySelectedBookCandidatesAsync();

    internal static string CandidateKey(Candidate c) => CharactersLook.CandidateKey(c);

    internal void ResetSeedSelection() => Look.ResetSeedSelection();

    internal void PreferBookRefsAsSeeds() => Look.PreferBookRefsAsSeeds();

    internal void AddBookRefsToSeedOrder() => Look.AddBookRefsToSeedOrder();

    internal int SeedRank(string key) => Look.SeedRank(key);

    internal void ToggleSeedKey(string key) => Look.ToggleSeedKey(key);

    internal void RequestDeleteImage(string kind, int index) => Look.RequestDeleteImage(kind, index);

    internal void CancelDeleteImage() => Look.CancelDeleteImage();

    internal Task ConfirmDeleteImageAsync() => Look.ConfirmDeleteImageAsync();

    internal static bool IsWeakBookPlate(string? fileName) => CharactersLook.IsWeakBookPlate(fileName);

    internal static string BookPlateKindLabel(string? fileName) => CharactersLook.BookPlateKindLabel(fileName);

    internal void ChoosePictureRoute(PictureRoute route) => Look.ChoosePictureRoute(route);

    internal Task StartBookGuidedGenerateAsync() => Look.StartBookGuidedGenerateAsync();

    internal void BackToSource() => Look.BackToSource();

    internal void ResetCompare() => Look.ResetCompare();

    internal Task StartRegenerateAsync() => Look.StartRegenerateAsync();

    internal Task<bool> EnsureGalleryBookSelectionAppliedAsync() => Look.EnsureGalleryBookSelectionAppliedAsync();

    internal Task StartSortCharacterPlatesAsync(bool useGrok = true) => Look.StartSortCharacterPlatesAsync(useGrok);

    internal Task StartGenerateCoreAsync(StartCharacterVariantsRequest req) => Look.StartGenerateCoreAsync(req);

    internal void BeginCompareFromVariants() => Look.BeginCompareFromVariants();

    internal void OpenLookZoom(Candidate c) => Look.OpenLookZoom(c);

    internal void CloseLookZoom() => Look.CloseLookZoom();

    internal void ToggleLookZoomScale() => Look.ToggleLookZoomScale();

    internal void ZoomPrev() => Look.ZoomPrev();

    internal void ZoomNext() => Look.ZoomNext();

    internal Task LockFromZoomAsync() => Look.LockFromZoomAsync();

    internal Task LockCandidateAsync(Candidate c, bool overrideStyle = false, string? overrideReason = null) => Look.LockCandidateAsync(c, overrideStyle, overrideReason);

    internal void DismissStyleReject() => Look.DismissStyleReject();

    internal static bool IsStyleGateRejection(string? message) => CharactersLook.IsStyleGateRejection(message);

    internal void OnLookDescriptionInput(ChangeEventArgs e) => Look.OnLookDescriptionInput(e);

    internal void OnLookVisualLockInput(ChangeEventArgs e) => Look.OnLookVisualLockInput(e);

    internal Task OnLookDescriptionChanged(string value) => Look.OnLookDescriptionChanged(value);

    internal Task OnLookVisualLockChanged(string value) => Look.OnLookVisualLockChanged(value);

    internal void ScheduleAutoSaveLook() => Look.ScheduleAutoSaveLook();

    internal Task AutoSaveLookDebouncedAsync(CancellationToken token) => Look.AutoSaveLookDebouncedAsync(token);

    internal Task SaveLookAsync(bool silent = false) => Look.SaveLookAsync(silent);

    internal Task UnlockAsync() => Look.UnlockAsync();

    internal Task OnUploadRefAsync(InputFileChangeEventArgs e) => Look.OnUploadRefAsync(e);


    internal int ApiMaxSeedRefs => Look.ApiMaxSeedRefs;
        internal int SelectedSeedCount => Look.SelectedSeedCount;
        internal bool CanUseBookPictures => Look.CanUseBookPictures;
}
