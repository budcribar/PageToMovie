using Microsoft.AspNetCore.Components;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Admin_TestEmailModal
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public string? DefaultAddress { get; set; }

    [Inject] private EngineApiClient Api { get; set; } = default!;

    private string _address = "";
    private string? _status;
    private string? _error;
    private bool _busy;
    private TestEmailResponse? _result;
    private bool _wasVisible;

    protected override void OnParametersSet()
    {
        if (Visible && !_wasVisible)
        {
            _status = null;
            _error = null;
            _result = null;
            if (string.IsNullOrWhiteSpace(_address) && !string.IsNullOrWhiteSpace(DefaultAddress) && DefaultAddress.Contains('@'))
                _address = DefaultAddress;
        }
        _wasVisible = Visible;
    }

    private async Task CloseAsync()
    {
        _status = null;
        _error = null;
        _result = null;
        await OnClose.InvokeAsync();
    }

    private async Task SendAsync()
    {
        _status = null;
        _error = null;
        _result = null;

        var to = _address.Trim();
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        {
            _error = "Enter a valid recipient email address.";
            return;
        }

        _busy = true;
        try
        {
            var res = await Api.TestEmailAsync(to);
            _result = res;
            if (res.Ok)
                _status = $"✓ {res.Message ?? "Test email sent successfully."}";
            else
                _error = $"✕ {res.Error ?? res.Message ?? "Failed to send test email."}";
        }
        catch (Exception ex)
        {
            _error = $"✕ Error sending test email: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }
}
