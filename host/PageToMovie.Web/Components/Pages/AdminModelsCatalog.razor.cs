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

    [Inject] private IAppLocalizer Localizer { get; set; } = default;

    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        await List.LoadCatalogAsync();
    }
}
