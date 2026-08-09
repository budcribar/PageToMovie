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

// Forwarders: CharactersListState → Host.*
public partial class Characters
{
    internal string? PreferredImageUrl => List.PreferredImageUrl;

    internal string? ListThumbUrl(CharacterSummary c) => List.ListThumbUrl(c);

    internal Task ExtractCastAsync() => List.ExtractCastAsync();

    internal Task OnProjectChangedAsync() => List.OnProjectChangedAsync();

    internal Task LoadAsync() => List.LoadAsync();

    internal Task SoftReloadAsync() => List.SoftReloadAsync();

    internal Task SelectAsync(string key) => List.SelectAsync(key);

    internal Task SelectCoreAsync(string key, bool resetMode, bool flushPending) => List.SelectCoreAsync(key, resetMode, flushPending);

    internal void ApplyPanelsForSelected() => List.ApplyPanelsForSelected();

    internal void ApplySimpleModeFromUri() => List.ApplySimpleModeFromUri();

    internal Task ExitSimplePathAsync() => List.ExitSimplePathAsync();

    internal void FocusNarratorIfNeeded() => List.FocusNarratorIfNeeded();

    internal Task ReloadSelectedCharacterAsync() => List.ReloadSelectedCharacterAsync();


        internal string PreferredImageLabel => List.PreferredImageLabel;
        internal bool HasCast => List.HasCast;
        internal IEnumerable<CharacterSummary> CharactersForUi => List.CharactersForUi;
        internal int OperatorCastCount => List.OperatorCastCount;
        internal bool IsCastComplete => List.IsCastComplete;
        internal bool NeedsCastBuild => List.NeedsCastBuild;
        internal bool NeedsFindCharacters => List.NeedsFindCharacters;
        internal bool CastListLocked => List.CastListLocked;
}
