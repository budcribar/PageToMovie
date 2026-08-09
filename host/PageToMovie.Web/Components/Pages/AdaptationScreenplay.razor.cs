using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    public override string StepKey => "screenplay";

    // ── Domain modules (lazy; own their state) ─────────────────────────────
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

    // ── Field forwarders (Host._x for markup) ──
    // Editor
    private string _text
    {
        get => Editor._text;
        set => Editor._text = value;
    }
    private string _loadedText
    {
        get => Editor._loadedText;
        set => Editor._loadedText = value;
    }
    private bool _editorReady
    {
        get => Editor._editorReady;
        set => Editor._editorReady = value;
    }
    private bool _jsInitStarted
    {
        get => Editor._jsInitStarted;
        set => Editor._jsInitStarted = value;
    }
    private bool _editorDataLoaded
    {
        get => Editor._editorDataLoaded;
        set => Editor._editorDataLoaded = value;
    }
    private bool _copied
    {
        get => Editor._copied;
        set => Editor._copied = value;
    }
    private int _sceneCount
    {
        get => Editor._sceneCount;
        set => Editor._sceneCount = value;
    }
    private ElementReference _editorHost
    {
        get => Editor._editorHost;
        set => Editor._editorHost = value;
    }
    private ElementReference _previewEl
    {
        get => Editor._previewEl;
        set => Editor._previewEl = value;
    }
    private ElementReference _scenesEl
    {
        get => Editor._scenesEl;
        set => Editor._scenesEl = value;
    }

    // Save
    private bool _dirtyLocal
    {
        get => Save._dirtyLocal;
        set => Save._dirtyLocal = value;
    }
    private int _saveGeneration
    {
        get => Save._saveGeneration;
        set => Save._saveGeneration = value;
    }
    private DateTime? _lastSavedUtc
    {
        get => Save._lastSavedUtc;
        set => Save._lastSavedUtc = value;
    }

    // SignOff
    private List<string> _signOffWarnings
    {
        get => SignOff._signOffWarnings;
        set => SignOff._signOffWarnings = value;
    }
    private ScreenplayStatus? _screenplayStatus
    {
        get => SignOff._screenplayStatus;
        set => SignOff._screenplayStatus = value;
    }

    // Book
    private bool _showBookModal
    {
        get => Book._showBookModal;
        set => Book._showBookModal = value;
    }
    private bool _bookWindowNeedsDragBind
    {
        get => Book._bookWindowNeedsDragBind;
        set => Book._bookWindowNeedsDragBind = value;
    }
    private bool _bookLoading
    {
        get => Book._bookLoading;
        set => Book._bookLoading = value;
    }
    private int _bookRequestGen
    {
        get => Book._bookRequestGen;
        set => Book._bookRequestGen = value;
    }
    private BookContextDto? _bookContext
    {
        get => Book._bookContext;
        set => Book._bookContext = value;
    }
    private ElementReference _bookWindowEl
    {
        get => Book._bookWindowEl;
        set => Book._bookWindowEl = value;
    }
}
