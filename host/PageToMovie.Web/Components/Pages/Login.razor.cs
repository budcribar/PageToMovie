using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Web.Services;
using PageToMovie.Core.Auth;

namespace PageToMovie.Web.Components.Pages;

public partial class Login : IDisposable
{
    private string _username { get; set; } = "";
    private string _email { get; set; } = "";
    private string _password = "";
    private string _confirmPassword = "";
    private string? _resetToken;
    private bool _isSignup;
    private bool _forgotMode;
    private bool _resetTokenMode;
    private bool _showPassword;
    internal bool _needsResend;
    internal string? _error;
    internal string? _info;
    internal string _status = "Checking session…";
    internal bool _busy;
    private bool _started;
    internal bool _checkedSession;

    private string PageTitleText =>
        _resetTokenMode ? "Reset password"
        : _forgotMode ? "Forgot password"
        : _isSignup ? "Sign Up"
        : "Sign In";

    private void ToggleShowPassword() => _showPassword = !_showPassword;

    private void EnterForgotMode()
    {
        _forgotMode = true;
        _resetTokenMode = false;
        _isSignup = false;
        _error = null;
        _info = null;
        _needsResend = false;
        _password = "";
        _confirmPassword = "";
    }

    private void ExitForgotMode()
    {
        _forgotMode = false;
        _error = null;
        _info = null;
    }

    private void ExitResetTokenMode()
    {
        _resetTokenMode = false;
        _resetToken = null;
        _error = null;
        _info = null;
        _password = "";
        _confirmPassword = "";
        // Drop token from URL without reloading the whole app if possible
        try { Nav.NavigateTo("/login", replace: true); } catch { /* ignore */ }
    }

    private void BackToSignIn()
    {
        _forgotMode = false;
        _resetTokenMode = false;
        _resetToken = null;
        _isSignup = false;
        _error = null;
        _info = null;
        _needsResend = false;
        _password = "";
        _confirmPassword = "";
        try { Nav.NavigateTo("/login", replace: true); } catch { /* ignore */ }
    }

    private async Task SubmitForgotAsync()
    {
        _error = null;
        _info = null;
        var id = _username.Trim();
        if (id.Length < 3 && !id.Contains('@'))
        {
            _error = "Enter your username (at least 3 characters) or email.";
            return;
        }

        _busy = true;
        _status = "Submitting request…";
        try
        {
            _info = await Api.ForgotPasswordAsync(id);
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

    private async Task SubmitResetWithTokenAsync()
    {
        _error = null;
        _info = null;
        if (string.IsNullOrWhiteSpace(_resetToken))
        {
            _error = "Missing reset token. Open the link from your email again.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_password) || _password.Length < 4)
        {
            _error = "Password must be at least 4 characters long.";
            return;
        }
        if (_password != _confirmPassword)
        {
            _error = "Passwords do not match.";
            return;
        }

        _busy = true;
        _status = "Updating password…";
        try
        {
            var (ok, msg) = await Api.ResetPasswordWithTokenAsync(_resetToken, _password);
            if (!ok)
            {
                _error = msg;
                return;
            }
            _info = msg;
            _resetTokenMode = false;
            _resetToken = null;
            _password = "";
            _confirmPassword = "";
            _isSignup = false;
            _forgotMode = false;
            try { Nav.NavigateTo("/login", replace: true); } catch { /* ignore */ }
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

    private async Task ResendConfirmationAsync()
    {
        var id = !string.IsNullOrWhiteSpace(_username) ? _username.Trim() : _email.Trim();
        if (id.Length < 3)
        {
            _error = "Enter your username or email first.";
            return;
        }
        _busy = true;
        try
        {
            _info = await Api.ResendConfirmationAsync(id);
            _error = null;
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

    protected override void OnInitialized()
    {
        L.CultureChanged += OnCultureChanged;
        var relative = Nav.ToBaseRelativePath(Nav.Uri);
        _isSignup = relative.StartsWith("signup", StringComparison.OrdinalIgnoreCase);
    }

    private void OnCultureChanged(System.Globalization.CultureInfo _culture) => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            L.CultureChanged -= OnCultureChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _started) return;
        _started = true;

        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = QueryHelpers.ParseQuery(uri.Query);

            // Explain failed ?me= bootstrap (never put the secret itself on this page).
            if (q.TryGetValue("overrideError", out var errVals))
            {
                _error = (errVals.FirstOrDefault() ?? "").Trim().ToLowerInvariant() switch
                {
                    "short" =>
                        $"Operator override secret is too short (need at least {AuthOptions.MinOperatorOverrideSecretLength} characters). " +
                        "Set Railway variable PageToMovie_LOGIN_OVERRIDE to a longer secret, or sign in below with that secret as the password.",
                    "missing" =>
                        "Operator override (?me=) was empty. Use ?me=YOUR_SECRET or sign in below.",
                    "failed" =>
                        "Operator override failed. Check Railway PageToMovie_LOGIN_OVERRIDE matches your ?me= secret " +
                        $"(min {AuthOptions.MinOperatorOverrideSecretLength} chars), then try again. " +
                        "Or sign in with username admin and that secret as the password.",
                    _ =>
                        "Could not complete operator login. Sign in below, or fix PageToMovie_LOGIN_OVERRIDE on Railway.",
                };
                StateHasChanged();
            }

            // Flash after confirm redirect
            if (q.TryGetValue("emailConfirmed", out var confFlash) &&
                string.Equals(confFlash.FirstOrDefault(), "1", StringComparison.Ordinal))
            {
                _info = "Email confirmed. You can sign in now.";
                _isSignup = false;
                StateHasChanged();
            }

            // Email confirmation link: /login?confirmEmail=TOKEN
            if (q.TryGetValue("confirmEmail", out var confirmVals))
            {
                var token = (confirmVals.FirstOrDefault() ?? "").Trim();
                if (token.Length >= 10)
                {
                    _busy = true;
                    _status = "Confirming email…";
                    StateHasChanged();
                    try
                    {
                        var (ok, msg) = await Api.ConfirmEmailAsync(token);
                        if (ok)
                        {
                            _info = string.IsNullOrWhiteSpace(msg) ? "Email confirmed. You can sign in now." : msg;
                            _isSignup = false;
                            _error = null;
                            try { Nav.NavigateTo("/login?emailConfirmed=1", replace: true); } catch { /* ignore */ }
                        }
                        else
                        {
                            _error = msg;
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

            // Password reset link: /login?resetToken=TOKEN
            if (q.TryGetValue("resetToken", out var resetVals))
            {
                var token = (resetVals.FirstOrDefault() ?? "").Trim();
                if (token.Length >= 10)
                {
                    _resetToken = token;
                    _resetTokenMode = true;
                    _forgotMode = false;
                    _isSignup = false;
                    StateHasChanged();
                }
            }

            await Session.EnsureHydratedAsync();
            if (Session.IsLoggedIn && !_resetTokenMode)
            {
                Nav.NavigateTo(ResolveReturnUrl(), forceLoad: false);
                return;
            }
            _checkedSession = true;
            StateHasChanged();
        }
        catch
        {
            _checkedSession = true;
            StateHasChanged();
        }
    }

    private string ResolveReturnUrl()
    {
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("returnUrl", out var ret) &&
                !string.IsNullOrWhiteSpace(ret))
            {
                var path = Uri.UnescapeDataString(ret.ToString());
                if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
                    return path.Split('?', '#')[0]; // never re-open with me= in returnUrl
            }
        }
        catch { /* home */ }
        return "/";
    }

    private void SetMode(bool isSignup)
    {
        _isSignup = isSignup;
        _forgotMode = false;
        _resetTokenMode = false;
        _error = null;
        _info = null;
        _needsResend = false;
        if (_isSignup && !Nav.Uri.EndsWith("/signup", StringComparison.OrdinalIgnoreCase))
            Nav.NavigateTo("/signup", replace: true);
        else if (!_isSignup && !Nav.Uri.EndsWith("/login", StringComparison.OrdinalIgnoreCase))
            Nav.NavigateTo("/login", replace: true);
    }

    private async Task SubmitAsync()
    {
        _error = null;
        _info = null;
        _needsResend = false;

        if (string.IsNullOrWhiteSpace(_username) || _username.Trim().Length < 3)
        {
            _error = "Username must be at least 3 characters long.";
            return;
        }

        if (_isSignup)
        {
            var email = (_email ?? "").Trim();
            if (email.Length < 5 || !email.Contains('@') || !email.Contains('.'))
            {
                _error = "Enter a valid email address.";
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_password) || _password.Length < 4)
        {
            _error = "Password must be at least 4 characters long.";
            return;
        }

        if (_isSignup && _password != _confirmPassword)
        {
            _error = "Passwords do not match.";
            return;
        }

        _busy = true;
        _status = _isSignup ? "Creating account…" : "Signing in…";
        StateHasChanged();

        try
        {
            var resp = _isSignup
                ? await Api.SignupAsync(_username.Trim(), _password, (_email ?? "").Trim())
                : await Api.LoginAsync(_username.Trim(), _password);

            if (resp is null)
            {
                _error = _isSignup ? "Sign up failed." : "Sign in failed.";
                return;
            }

            // Signup (or login) that still needs email confirmation — no session yet
            if (resp.RequiresEmailConfirmation || (_isSignup && resp.Ok && string.IsNullOrWhiteSpace(resp.Token)))
            {
                _needsResend = true;
                if (resp.Ok || !string.IsNullOrWhiteSpace(resp.Message))
                {
                    _info = resp.Message
                            ?? "Account created. Check your email for a confirmation link before signing in.";
                    _error = null;
                    _isSignup = false;
                }
                else
                {
                    _error = resp.Error
                             ?? "Confirm your email before signing in. Check your inbox (or the API log in development).";
                }
                return;
            }

            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Token))
            {
                _error = resp.Error ?? (_isSignup ? "Sign up failed." : "Sign in failed.");
                return;
            }

            // Await storage write so forceLoad cannot rehydrate a previous user id (e.g. renamed account).
            await Session.SetSessionAsync(
                resp.Token!,
                resp.UserId ?? _username.Trim(),
                resp.Roles,
                resp.ExpiresAt);

            _status = "Redirecting…";
            StateHasChanged();

            Nav.NavigateTo(ResolveReturnUrl(), forceLoad: true);
        }
        catch (HttpRequestException ex)
        {
            _error = $"Network error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }
}

