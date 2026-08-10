using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    public override string StepKey => "screenplay";

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

    internal void EnsureDomains()
    {
        _ = Editor; _ = Save; _ = SignOff; _ = Book; _ = Tools;
    }

    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        ApplyToolQuery();
        await base.OnInitializedAsync();
        await Editor.LoadEditorDataAsync();
        await Tools.RefreshEstimateAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await Book.AfterRenderDragBindAsync();
        await Editor.TryInitEditorAsync();
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
