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

    internal void EnsureDomains()
    {
        _ = Editor; _ = Save; _ = SignOff; _ = Book;
    }

    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        await base.OnInitializedAsync();
        await Editor.LoadEditorDataAsync();
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

    // JS interop must target the component type (DotNetObjectReference).
    [JSInvokable]
    public Task OnEditorChanged(string text, string[] warnings, int sceneCount) =>
        Editor.OnEditorChanged(text, warnings, sceneCount);

    [JSInvokable]
    public Task OnSceneSelected(int line, int sceneIndex, string heading, bool openBookModal = false) =>
        Book.OnSceneSelected(line, sceneIndex, heading, openBookModal);
}
