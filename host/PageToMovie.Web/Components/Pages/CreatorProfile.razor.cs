using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class CreatorProfile
{
    [Parameter]
    public string? Handle { get; set; }

    private CreatorProfileDto? _profile;
    internal List<DemoListItem>? _demos;
    internal bool _loading = true;
    internal string? _error;

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _error = null;

        if (string.IsNullOrWhiteSpace(Handle))
        {
            _error = "Handle is required.";
            _loading = false;
            return;
        }

        try
        {
            var cleanHandle = Handle.Trim().TrimStart('@');
            _profile = await Engine.GetCreatorProfileAsync(cleanHandle);

            if (_profile is null)
            {
                _error = $"Could not find creator profile for '@{cleanHandle}'.";
            }
            else
            {
                var allDemos = await Engine.ListDemosAsync(take: 100);
                if (allDemos is not null)
                {
                    _demos = allDemos
                        .Where(d => string.Equals(d.CreatedBy, _profile.UserId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(d.CreatedBy, _profile.Username, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }
}
