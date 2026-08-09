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

// Forwarders: ReviewListState → Host.*
public partial class Review
{
    internal void ToggleSceneSort(string column) => List.ToggleSceneSort(column);

    internal Task ToggleTabAsync(string tab) => List.ToggleTabAsync(tab);

    internal Task SetTabReview() => List.SetTabReview();

    internal Task SetTabShare() => List.SetTabShare();

    internal bool IsSceneGroupExpanded(string rangeStr) => List.IsSceneGroupExpanded(rangeStr);

    internal void ToggleSceneGroupExpand(string rangeStr) => List.ToggleSceneGroupExpand(rangeStr);

    internal void ToggleAllSceneGroups(bool expand) => List.ToggleAllSceneGroups(expand);

    internal Task LoadAsync() => List.LoadAsync();

    internal Task SoftLoadAsync() => List.SoftLoadAsync();

    internal Task TryLoadDraftsForSceneAsync(int scene) => List.TryLoadDraftsForSceneAsync(scene);

    internal Task LoadSelectedDetailAsync(int sn) => List.LoadSelectedDetailAsync(sn);

    internal Task SelectSceneAsync(int scene) => List.SelectSceneAsync(scene);

    internal int ClipCountFor(int scene) => List.ClipCountFor(scene);

    internal int ClipCountOnDisk(int scene) => List.ClipCountOnDisk(scene);

    internal bool SceneHasComposite(int scene) => List.SceneHasComposite(scene);

    internal bool ClipOnDisk(int scene, int clip) => List.ClipOnDisk(scene, clip);

    internal Task ApproveAsync(int scene) => List.ApproveAsync(scene);

}
