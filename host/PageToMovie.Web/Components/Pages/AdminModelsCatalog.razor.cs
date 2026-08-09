using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;
using PageToMovie.Core.Localization;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminModelsCatalog
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private AdminModelsList? _list;
    internal AdminModelsList List => _list ??= new AdminModelsList(this);
    private AdminModelsEditor? _editor;
    internal AdminModelsEditor Editor => _editor ??= new AdminModelsEditor(this);
    private AdminModelsRaw? _raw;
    internal AdminModelsRaw Raw => _raw ??= new AdminModelsRaw(this);
    private AdminModelsPersist? _persist;
    internal AdminModelsPersist Persist => _persist ??= new AdminModelsPersist(this);

    internal void EnsureDomains()
    {
        _ = List; _ = Editor; _ = Raw; _ = Persist;
    }

    internal static readonly string[] Capabilities = { "Chat", "Vision", "Video", "Image", "Audio", "Voice", "LipSync" };

    internal bool _busy;

    internal string? _error;

    internal string? _message;

    [Inject] private IAppLocalizer Localizer { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        await LoadCatalogAsync();
    }

    // ── Field forwarders (Host._x for markup) ──
    internal bool _loading
    {
        get => List._loading;
        set => List._loading = value;
    }
    internal List<JsonObject> _modelList
    {
        get => List._modelList;
        set => List._modelList = value;
    }
    internal string _filterQuery
    {
        get => List._filterQuery;
        set => List._filterQuery = value;
    }
    internal string _filterCapability
    {
        get => List._filterCapability;
        set => List._filterCapability = value;
    }
    internal string _filterProvider
    {
        get => List._filterProvider;
        set => List._filterProvider = value;
    }
    internal string _filterStatus
    {
        get => List._filterStatus;
        set => List._filterStatus = value;
    }
    internal JsonObject? _editModel
    {
        get => Editor._editModel;
        set => Editor._editModel = value;
    }
    internal bool _editIsNew
    {
        get => Editor._editIsNew;
        set => Editor._editIsNew = value;
    }
    internal string _editId
    {
        get => Editor._editId;
        set => Editor._editId = value;
    }
    internal string _editDisplayName
    {
        get => Editor._editDisplayName;
        set => Editor._editDisplayName = value;
    }
    internal string _editCapability
    {
        get => Editor._editCapability;
        set => Editor._editCapability = value;
    }
    internal string _editProvider
    {
        get => Editor._editProvider;
        set => Editor._editProvider = value;
    }
    internal bool _editEnabled
    {
        get => Editor._editEnabled;
        set => Editor._editEnabled = value;
    }
    internal bool _editDeprecated
    {
        get => Editor._editDeprecated;
        set => Editor._editDeprecated = value;
    }
    internal bool _editLabMode
    {
        get => Editor._editLabMode;
        set => Editor._editLabMode = value;
    }
    internal string _editLabNotes
    {
        get => Editor._editLabNotes;
        set => Editor._editLabNotes = value;
    }
    internal string _editEndpointPath
    {
        get => Editor._editEndpointPath;
        set => Editor._editEndpointPath = value;
    }
    internal int? _editMaxPromptLength
    {
        get => Editor._editMaxPromptLength;
        set => Editor._editMaxPromptLength = value;
    }
    internal string _editLastVerifiedAt
    {
        get => Editor._editLastVerifiedAt;
        set => Editor._editLastVerifiedAt = value;
    }
    internal string _editPricingLastReviewedAt
    {
        get => Editor._editPricingLastReviewedAt;
        set => Editor._editPricingLastReviewedAt = value;
    }
    internal string _editPricingNotes
    {
        get => Editor._editPricingNotes;
        set => Editor._editPricingNotes = value;
    }
    internal int? _editMaxInputTokens
    {
        get => Editor._editMaxInputTokens;
        set => Editor._editMaxInputTokens = value;
    }
    internal int? _editMaxOutputTokens
    {
        get => Editor._editMaxOutputTokens;
        set => Editor._editMaxOutputTokens = value;
    }
    internal double? _editInputCost
    {
        get => Editor._editInputCost;
        set => Editor._editInputCost = value;
    }
    internal double? _editOutputCost
    {
        get => Editor._editOutputCost;
        set => Editor._editOutputCost = value;
    }
    internal int? _editMinClip
    {
        get => Editor._editMinClip;
        set => Editor._editMinClip = value;
    }
    internal int? _editMaxClip
    {
        get => Editor._editMaxClip;
        set => Editor._editMaxClip = value;
    }
    internal int? _editAbsMaxClip
    {
        get => Editor._editAbsMaxClip;
        set => Editor._editAbsMaxClip = value;
    }
    internal int? _editMaxRefs
    {
        get => Editor._editMaxRefs;
        set => Editor._editMaxRefs = value;
    }
    internal bool _editSupportsContinue
    {
        get => Editor._editSupportsContinue;
        set => Editor._editSupportsContinue = value;
    }
    internal double? _editExtendCost
    {
        get => Editor._editExtendCost;
        set => Editor._editExtendCost = value;
    }
    internal double? _editRefImageCost
    {
        get => Editor._editRefImageCost;
        set => Editor._editRefImageCost = value;
    }
    internal string _editVideoPerSecJson
    {
        get => Editor._editVideoPerSecJson;
        set => Editor._editVideoPerSecJson = value;
    }
    internal string _editVideoBaseJson
    {
        get => Editor._editVideoBaseJson;
        set => Editor._editVideoBaseJson = value;
    }
    internal double? _editImageCost
    {
        get => Editor._editImageCost;
        set => Editor._editImageCost = value;
    }
    internal int? _editMaxAudio
    {
        get => Editor._editMaxAudio;
        set => Editor._editMaxAudio = value;
    }
    internal bool _editSupportsVocals
    {
        get => Editor._editSupportsVocals;
        set => Editor._editSupportsVocals = value;
    }
    internal bool _showRawJson
    {
        get => Raw._showRawJson;
        set => Raw._showRawJson = value;
    }
    internal string _rawJsonText
    {
        get => Raw._rawJsonText;
        set => Raw._rawJsonText = value;
    }
    internal JsonObject? _rootObj
    {
        get => Raw._rootObj;
        set => Raw._rootObj = value;
    }
    internal List<string>? _validationErrors
    {
        get => Persist._validationErrors;
        set => Persist._validationErrors = value;
    }
    internal CatalogUpdateScanClientResult? _scan
    {
        get => Persist._scan;
        set => Persist._scan = value;
    }
    internal bool _scanning
    {
        get => Persist._scanning;
        set => Persist._scanning = value;
    }
}
