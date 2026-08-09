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

// Forwarders: CharactersLookBook / LookPipeline / LookEditors → Host.*
public partial class Characters
{
    internal Task ToggleBookCandidateGalleryAsync() => LookBook.ToggleBookCandidateGalleryAsync();

    internal Task LoadBookCandidatesAsync() => LookBook.LoadBookCandidatesAsync();

    internal void ToggleBookCandidateSelection(string pathRel) => LookBook.ToggleBookCandidateSelection(pathRel);

    internal Task ApplySelectedBookCandidatesAsync() => LookBook.ApplySelectedBookCandidatesAsync();

    internal static string CandidateKey(Candidate c) => CharactersLookPipeline.CandidateKey(c);

    internal void ResetSeedSelection() => LookBook.ResetSeedSelection();

    internal void PreferBookRefsAsSeeds() => LookBook.PreferBookRefsAsSeeds();

    internal void AddBookRefsToSeedOrder() => LookBook.AddBookRefsToSeedOrder();

    internal int SeedRank(string key) => LookBook.SeedRank(key);

    internal void ToggleSeedKey(string key) => LookBook.ToggleSeedKey(key);

    internal void RequestDeleteImage(string kind, int index) => LookPipe.RequestDeleteImage(kind, index);

    internal void CancelDeleteImage() => LookPipe.CancelDeleteImage();

    internal Task ConfirmDeleteImageAsync() => LookPipe.ConfirmDeleteImageAsync();

    internal static bool IsWeakBookPlate(string? fileName) => CharactersLookBook.IsWeakBookPlate(fileName);

    internal static string BookPlateKindLabel(string? fileName) => CharactersLookBook.BookPlateKindLabel(fileName);

    internal void ChoosePictureRoute(PictureRoute route) => LookPipe.ChoosePictureRoute(route);

    internal Task StartBookGuidedGenerateAsync() => LookPipe.StartBookGuidedGenerateAsync();

    internal void BackToSource() => LookPipe.BackToSource();

    internal void ResetCompare() => LookPipe.ResetCompare();

    internal Task StartRegenerateAsync() => LookPipe.StartRegenerateAsync();

    internal Task<bool> EnsureGalleryBookSelectionAppliedAsync() => LookBook.EnsureGalleryBookSelectionAppliedAsync();

    internal Task StartSortCharacterPlatesAsync(bool useGrok = true) => LookBook.StartSortCharacterPlatesAsync(useGrok);

    internal Task StartGenerateCoreAsync(StartCharacterVariantsRequest req) => LookPipe.StartGenerateCoreAsync(req);

    internal void BeginCompareFromVariants() => LookPipe.BeginCompareFromVariants();

    internal void OpenLookZoom(Candidate c) => LookPipe.OpenLookZoom(c);

    internal void CloseLookZoom() => LookPipe.CloseLookZoom();

    internal void ToggleLookZoomScale() => LookPipe.ToggleLookZoomScale();

    internal void ZoomPrev() => LookPipe.ZoomPrev();

    internal void ZoomNext() => LookPipe.ZoomNext();

    internal Task LockFromZoomAsync() => LookPipe.LockFromZoomAsync();

    internal Task LockCandidateAsync(Candidate c, bool overrideStyle = false, string? overrideReason = null) => LookPipe.LockCandidateAsync(c, overrideStyle, overrideReason);

    internal void DismissStyleReject() => LookPipe.DismissStyleReject();

    internal static bool IsStyleGateRejection(string? message) => CharactersLookPipeline.IsStyleGateRejection(message);

    internal void OnLookDescriptionInput(ChangeEventArgs e) => LookEdit.OnLookDescriptionInput(e);

    internal void OnLookVisualLockInput(ChangeEventArgs e) => LookEdit.OnLookVisualLockInput(e);

    internal Task OnLookDescriptionChanged(string value) => LookEdit.OnLookDescriptionChanged(value);

    internal Task OnLookVisualLockChanged(string value) => LookEdit.OnLookVisualLockChanged(value);

    internal void ScheduleAutoSaveLook() => LookEdit.ScheduleAutoSaveLook();

    internal Task AutoSaveLookDebouncedAsync(CancellationToken token) => LookEdit.AutoSaveLookDebouncedAsync(token);

    internal Task SaveLookAsync(bool silent = false) => LookEdit.SaveLookAsync(silent);

    internal Task UnlockAsync() => LookPipe.UnlockAsync();

    internal Task OnUploadRefAsync(InputFileChangeEventArgs e) => LookPipe.OnUploadRefAsync(e);


    internal int ApiMaxSeedRefs => LookBook.ApiMaxSeedRefs;
    internal int SelectedSeedCount => LookBook.SelectedSeedCount;
    internal bool CanUseBookPictures => LookBook.CanUseBookPictures;
}
