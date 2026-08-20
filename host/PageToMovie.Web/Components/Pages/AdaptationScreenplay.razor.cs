using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    public override string StepKey => "screenplay";

    private string ScreenplayPageTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SignOff.StatusTitle) && SignOff.StatusTitle != "Untitled")
                return SignOff.StatusTitle;
            if (!string.IsNullOrWhiteSpace(ProjectLabel))
                return ProjectLabel;
            return L["Adaptation.Screenplay"];
        }
    }

    private ScreenplayEditor? _editorDomain;
    internal ScreenplayEditor Editor => _editorDomain ??= new ScreenplayEditor(this);
    private ScreenplaySave? _saveDomain;
    internal ScreenplaySave Save => _saveDomain ??= new ScreenplaySave(this);
    private ScreenplaySignOff? _signOffDomain;
    internal ScreenplaySignOff SignOff => _signOffDomain ??= new ScreenplaySignOff(this);
    private ScreenplayBook? _bookDomain;
    internal ScreenplayBook Book => _bookDomain ??= new ScreenplayBook(this);
    private ScreenplayTools? _toolsDomain;
    internal ScreenplayTools Tools => _toolsDomain ??= new ScreenplayTools(this);

    /// <summary>Hosted structured editor instance (for Menu actions).</summary>
    private global::PageToMovie.ScreenplayEditor.Components.ScreenplayEditor? _structuredUi = null;

    [Inject] private ClientMediaFolderService MediaFolder { get; set; } = default!;

    /// <summary>
    /// Outline scene drag (B8): BEFORE a shot plan exists a reorder is a pure text operation —
    /// keep the editor's local model move + autosave. AFTER a plan exists, a text-only reorder
    /// silently desyncs the blueprint and every numbered clip file, so route it through the
    /// server's renumber engine (same one the Film page's drag uses): it permutes the screenplay
    /// chunks, blueprint order, media files, registry rows, and the client rename manifest.
    /// </summary>
    internal async Task OnOutlineReorderRequestedAsync((int from, int to) args)
    {
        int planCount = 0;
        try
        {
            var scenes = await Engine.GetScenesAsync(ProjectId);
            planCount = scenes?.Scenes?.Count ?? 0;
        }
        catch
        {
            planCount = 0;
        }

        if (planCount == 0)
        {
            if (_structuredUi is not null)
                await _structuredUi.ReorderScenesLocallyAsync(args);
            return;
        }

        // The engine reorders COMMITTED draft text — flush any pending editor edits first.
        await Save.SaveDraftAsync(manual: false);

        // The order must be a permutation of the BLUEPRINT's scenes — that can include a trailing
        // auto-credits scene the outline doesn't show, so span planCount, not the outline count.
        var outlineCount = Editor._model.Scenes.Count;
        if (args.from < 0 || args.from >= outlineCount || args.to < 0 || args.to >= outlineCount || args.from == args.to)
            return;
        var order = Enumerable.Range(1, planCount).ToList();
        var moved = order[args.from];
        order.RemoveAt(args.from);
        order.Insert(args.to, moved);

        Busy = true;
        Error = null;
        try
        {
            var (ok, error) = await Engine.ReorderScenesAsync(ProjectId, order);
            if (!ok)
            {
                Error = error ?? "Scene reorder failed.";
                return;
            }
            await MediaFolder.ApplyServerRenamesAsync(ProjectId);
            await Editor.LoadEditorDataAsync(); // fresh draft + rebuilt model
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    /// <summary>Deep link from Film: /adaptation/screenplay?scene=N</summary>
    private int? _pendingSceneFromQuery;
    private bool _appliedSceneQuery;

    private bool _menuOpen;

    private void ToggleMenu() => _menuOpen = !_menuOpen;

    private void CloseMenu() => _menuOpen = false;

    private async Task MenuActionAsync(Func<Task> action)
    {
        CloseMenu();
        await action();
    }

    private void MenuOpenCharacters()
    {
        CloseMenu();
        _structuredUi?.OpenCharacterModal();
    }

    private void MenuOpenLocations()
    {
        CloseMenu();
        _structuredUi?.OpenLocationModal();
    }

    private void MenuOpenExport()
    {
        CloseMenu();
        _structuredUi?.OpenExportModal();
    }

    private void MenuOpenExportPdf()
    {
        CloseMenu();
        _structuredUi?.OpenExportPdfModal();
    }

    private async Task MenuCollapseAllAsync()
    {
        CloseMenu();
        if (_structuredUi is not null)
            await _structuredUi.CollapseAllScenes();
    }

    private async Task MenuExpandAllAsync()
    {
        CloseMenu();
        if (_structuredUi is not null)
            await _structuredUi.ExpandAllScenes();
    }

    /// <summary>
    /// Outline ▶ — play the generated scene video on Film (same stitch path as “Play scene”).
    /// </summary>
    private void PlaySceneOnFilmPage(int sceneNumber)
    {
        if (sceneNumber <= 0) return;
        Nav.NavigateTo($"scenes?scene={sceneNumber}&play=1");
    }

    internal void EnsureDomains()
    {
        _ = Editor; _ = Save; _ = SignOff; _ = Book; _ = Tools;
    }

    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        ApplyToolQuery();
        _pendingSceneFromQuery = StudioDeepLinks.QueryInt(Nav, "scene");
        await base.OnInitializedAsync();
        await Editor.LoadEditorDataAsync();
        await Tools.RefreshEstimateAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await Book.AfterRenderDragBindAsync();
        await Editor.TryInitEditorAsync();
        if (!_appliedSceneQuery
            && _pendingSceneFromQuery is int sn
            && Editor._editorReady
            && _structuredUi is not null)
        {
            _appliedSceneQuery = true;
            if (_structuredUi.SelectSceneByNumber(sn))
                StateHasChanged();
        }
    }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        await Editor.LoadEditorDataAsync();
        await Editor.OnHostLoadAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        Save.DisposeSaveCts();
        await Editor.DisposeEditorAsync();
        await base.DisposeAsync();
    }

    /// <summary>Deep links from Film / old routes: ?tool=look|enrich|fit</summary>
    private void ApplyToolQuery()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = uri.Query.TrimStart('?');
            if (string.IsNullOrEmpty(q)) return;
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2
                    && kv[0].Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    Tools.Open(Uri.UnescapeDataString(kv[1]));
                    break;
                }
            }
        }
        catch { /* ignore malformed query */ }
    }

    // JS interop must target the component type (DotNetObjectReference).
    [JSInvokable]
    public Task OnEditorChanged(string text, string[] warnings, int sceneCount) =>
        Editor.OnEditorChanged(text, warnings, sceneCount);

    [JSInvokable]
    public Task OnSceneSelected(int line, int sceneIndex, string heading, bool openBookModal = false) =>
        Book.OnSceneSelected(line, sceneIndex, heading, openBookModal);
}
