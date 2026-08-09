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

public partial class Configuration
{
    // ── Domain modules (lazy; own their state) ─────────────────────────────
    private ConfigurationCatalog? _catalog;
    internal ConfigurationCatalog Catalog => _catalog ??= new ConfigurationCatalog(this);
    private ConfigurationKeys? _keys;
    internal ConfigurationKeys Keys => _keys ??= new ConfigurationKeys(this);
    private ConfigurationCoverage? _coverage;
    internal ConfigurationCoverage Coverage => _coverage ??= new ConfigurationCoverage(this);
    private ConfigurationProjectForm? _form;
    internal ConfigurationProjectForm Form => _form ??= new ConfigurationProjectForm(this);
    private ConfigurationMediaTheme? _media;
    internal ConfigurationMediaTheme Media => _media ??= new ConfigurationMediaTheme(this);

    internal void EnsureDomains()
    {
        _ = Catalog; _ = Keys; _ = Coverage; _ = Form; _ = Media;
    }


    internal bool _busy;

    internal string? _error;

    internal string? _message;

    internal string _projectId = "";

    internal List<string> _projectIds = new();

    internal Dictionary<string, JsonElement>? _cfg;


    protected override async Task OnInitializedAsync()
    {
        EnsureDomains();
        Coverage.ParseFocusFromUri();
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }
            if (!Session.IsLoggedIn)
            {
                _error = "Sign in required to view or save API keys.";
                Nav.NavigateTo("/login?returnUrl=/configuration");
                return;
            }

            try { Keys._userSettings = await Engine.GetUserSettingsAsync(); } catch { }
            await Catalog.LoadCatalogAsync();

            var projs = await Engine.GetProjectsAsync();
            _projectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                          ?? new List<string>();

            var candidateId = ActiveProject.ProjectId;
            if (string.IsNullOrWhiteSpace(candidateId))
                candidateId = projs?.Active?.Id;

            if (!string.IsNullOrWhiteSpace(candidateId))
            {
                _projectId = candidateId;
                if (!_projectIds.Contains(_projectId, StringComparer.OrdinalIgnoreCase))
                    _projectIds.Insert(0, _projectId);
            }
            else if (_projectIds.Count > 0)
            {
                _projectId = _projectIds[0];
            }
            else
            {
                _projectId = "";
            }

            if (!string.IsNullOrEmpty(_projectId))
                await Form.LoadAsync();

            // Deep-link ?focus=voice|music|… opens just-in-time key panel for that coverage row.
            if (Coverage.FocusActive)
                Keys.BeginAddKey(Coverage._focusCapability!);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }


    protected override void OnInitialized()
    {
        MediaFolder.Changed += Media.OnMediaFolderChanged;
    }


    public void Dispose()
    {
        MediaFolder.Changed -= Media.OnMediaFolderChanged;
        try { Form._autoSaveCts?.Cancel(); } catch { /* ignore */ }
        Form._autoSaveCts?.Dispose();
    }


    public async ValueTask DisposeAsync()
    {
        MediaFolder.Changed -= Media.OnMediaFolderChanged;
        try { Form._autoSaveCts?.Cancel(); } catch { /* ignore */ }
        Form._autoSaveCts?.Dispose();
        // Flush any pending edits when leaving the page.
        if (Form._dirty && !string.IsNullOrWhiteSpace(_projectId) && _cfg is not null)
        {
            try { await Form.PersistProjectConfigAsync(); }
            catch { /* best-effort on navigate away */ }
            Form._dirty = false;
        }
    }


}
