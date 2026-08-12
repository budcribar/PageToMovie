using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminUsers
{
    private AdminCreditsOverviewDto? _overview;
    internal string? _error;
    internal string? _message;
    internal bool _busy;
    private bool _started;

    private UserCreditSummaryDto? _grantUser;
    private double _grantUsd;
    private int _grantCredits;
    private string? _grantNote;
    private int _lastSyncedCredits;

    private UserCreditSummaryDto? _deleteUser;
    private string _deleteConfirmUsername = "";
    private string _deleteAdminPassword = "";
    private bool _deleteOwnedProjects = true;
    private bool _showDeleteAdminPassword;

    private UserCreditSummaryDto? _resetUser;
    private string _resetNewPassword = "";
    private string _resetAdminPassword = "";
    private bool _showResetNewPassword;
    private bool _showResetAdminPassword;

    private bool IsSelf(UserCreditSummaryDto u) =>
        string.Equals(u.UserId, Session.UserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(u.Username, Session.UserId, StringComparison.OrdinalIgnoreCase);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started) return;
        _started = true;

        await Session.EnsureHydratedAsync();
        if (!Session.IsAdmin)
        {
            Nav.NavigateTo("/admin/login", forceLoad: true);
            return;
        }

        await LoadAsync();
        StateHasChanged();
    }

    protected override void OnParametersSet()
    {
        // Keep USD and credit fields in sync when the user types credits.
        if (_grantCredits != _lastSyncedCredits)
        {
            _lastSyncedCredits = _grantCredits;
            _grantUsd = Math.Round(_grantCredits * 0.01, 4);
        }
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            _overview = await Api.GetAdminUsersCreditsAsync();
            if (_overview is null)
                _error = "Could not load users & credits (null response).";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _overview = null;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenGrant(UserCreditSummaryDto u)
    {
        _deleteUser = null;
        _resetUser = null;
        _grantUser = u;
        _grantUsd = 5;
        _grantCredits = 500;
        _lastSyncedCredits = 500;
        _grantNote = null;
        _message = null;
    }

    private void CloseGrant()
    {
        _grantUser = null;
        _message = null;
    }

    private void OpenResetPassword(UserCreditSummaryDto u)
    {
        _grantUser = null;
        _deleteUser = null;
        _resetUser = u;
        _resetNewPassword = "";
        _resetAdminPassword = "";
        _message = null;
        _error = null;
    }

    private void CloseResetPassword()
    {
        _resetUser = null;
        _resetNewPassword = "";
        _resetAdminPassword = "";
    }

    private async Task SubmitResetPasswordAsync()
    {
        if (_resetUser is null) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await Api.AdminSetUserPasswordAsync(
                _resetUser.UserId,
                _resetNewPassword,
                _resetAdminPassword);
            _message = $"Password updated for {_resetUser.Username}.";
            CloseResetPassword();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenDelete(UserCreditSummaryDto u)
    {
        _grantUser = null;
        _resetUser = null;
        _deleteUser = u;
        _deleteConfirmUsername = "";
        _deleteAdminPassword = "";
        _deleteOwnedProjects = true;
        _message = null;
        _error = null;
    }

    private void CloseDelete()
    {
        _deleteUser = null;
        _deleteConfirmUsername = "";
        _deleteAdminPassword = "";
    }

    private async Task SetDisabledAsync(UserCreditSummaryDto u, bool disabled)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var result = await Api.SetAdminUserDisabledAsync(new AdminSetUserDisabledRequest
            {
                UserId = u.UserId,
                Disabled = disabled,
            });
            if (result is null || !result.Ok)
            {
                _error = result?.Error ?? (disabled ? "Disable failed." : "Enable failed.");
            }
            else
            {
                _message = result.Message
                           ?? (disabled ? $"Disabled {u.Username}." : $"Re-enabled {u.Username}.");
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SubmitDeleteAsync()
    {
        if (_deleteUser is null) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            var result = await Api.DeleteAdminUserAsync(new AdminDeleteUserRequest
            {
                UserId = _deleteUser.UserId,
                ConfirmUsername = _deleteConfirmUsername,
                AdminPassword = _deleteAdminPassword,
                DeleteOwnedProjects = _deleteOwnedProjects,
            });
            if (result is null || !result.Ok)
            {
                _error = result?.Error ?? "Delete failed.";
            }
            else
            {
                _message = result.Message
                           ?? $"Deleted {_deleteUser.Username}.";
                if (result.ProjectErrors is { Count: > 0 })
                    _message += " Some projects could not be deleted: " + string.Join("; ", result.ProjectErrors);
                CloseDelete();
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void QuickGrant(double usd)
    {
        _grantUsd = usd;
        _grantCredits = (int)Math.Round(usd / 0.01);
        _lastSyncedCredits = _grantCredits;
    }

    private async Task SubmitGrantAsync()
    {
        if (_grantUser is null) return;
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            // Prefer explicit USD; if user only set credits, OnParametersSet already mirrored.
            var amount = _grantUsd;
            if (Math.Abs(amount) < 0.0001 && _grantCredits != 0)
                amount = _grantCredits * 0.01;

            var updated = await Api.GrantAdminCreditsAsync(new AdminGrantCreditsRequest
            {
                UserId = _grantUser.UserId,
                AmountUsd = amount,
                Note = _grantNote,
            });
            if (updated is null)
            {
                _error = "Grant failed (user not found or request rejected).";
            }
            else
            {
                _message = $"Updated {_grantUser.Username}: balance {updated.CreditsBalance} credits (${updated.CreditsBalanceUsd:F2}).";
                _grantUser = null;
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}

