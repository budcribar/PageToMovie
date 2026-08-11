using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class ProjectCollaboratorsModal : IDisposable
{

    [Parameter] public bool IsOpen { get; set; }
    [Parameter] public string ProjectId { get; set; } = "";
    [Parameter] public EventCallback OnClose { get; set; }

    private string searchHandle = "";
    private string emailInput = "";
    private string statusMessage = "";
    private bool isError;
    private bool isSending;
    private bool _searching;
    private bool _searchAttempted;
    private List<string> _suggestions = new();
    private int _highlight = -1;
    private int _searchSeq;
    private CancellationTokenSource? _searchCts;

    private async Task Close()
    {
        statusMessage = "";
        isError = false;
        searchHandle = "";
        emailInput = "";
        _suggestions.Clear();
        _highlight = -1;
        _searchAttempted = false;
        CancelSearch();
        if (OnClose.HasDelegate)
            await OnClose.InvokeAsync();
    }

    private void CancelSearch()
    {
        try { _searchCts?.Cancel(); } catch { /* */ }
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private async Task OnHandleInput(ChangeEventArgs e)
    {
        searchHandle = e.Value?.ToString() ?? "";
        _suggestions.Clear();
        _highlight = -1;
        _searchAttempted = false;
        await DebouncedSearchAsync();
    }

    private async Task OnHandleFocus()
    {
        // If they already typed something, refresh the list on focus
        if (searchHandle.Trim().Length >= 1 && _suggestions.Count == 0)
            await DebouncedSearchAsync();
    }

    private async Task OnHandleKeyDown(KeyboardEventArgs e)
    {
        if (_suggestions.Count == 0) return;
        if (e.Key == "ArrowDown")
        {
            _highlight = _highlight < 0 ? 0 : Math.Min(_highlight + 1, _suggestions.Count - 1);
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Key == "ArrowUp")
        {
            _highlight = _highlight <= 0 ? _suggestions.Count - 1 : _highlight - 1;
            await InvokeAsync(StateHasChanged);
        }
        else if (e.Key is "Enter" or "Tab")
        {
            if (_highlight >= 0 && _highlight < _suggestions.Count)
            {
                PickHandle(_suggestions[_highlight].TrimStart('@'));
            }
        }
        else if (e.Key == "Escape")
        {
            _suggestions.Clear();
            _highlight = -1;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void PickHandle(string bare)
    {
        searchHandle = bare ?? "";
        _suggestions.Clear();
        _highlight = -1;
        _searchAttempted = false;
        StateHasChanged();
    }

    private async Task DebouncedSearchAsync()
    {
        CancelSearch();
        var q = searchHandle.Trim().TrimStart('@');
        // One letter is enough — "b" → all handles starting with b
        if (q.Length < 1)
        {
            _searching = false;
            _suggestions.Clear();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var seq = ++_searchSeq;
        _searching = true;
        try
        {
            await Task.Delay(180, cts.Token);
            if (seq != _searchSeq) return;
            var found = await Engine.SearchUserHandlesAsync(q, cts.Token);
            if (seq != _searchSeq) return;

            // Hide self from the picker
            var me = (Session.UserId ?? "").Trim();
            var meHandle = (Session.DisplayHandle ?? "").Trim().TrimStart('@');
            _suggestions = found
                .Select(h => h.Trim())
                .Where(h =>
                {
                    var bare = h.TrimStart('@');
                    if (meHandle.Length > 0 &&
                        string.Equals(bare, meHandle, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (me.Length > 0 &&
                        string.Equals(bare, me, StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .ToList();
            _highlight = _suggestions.Count > 0 ? 0 : -1;
            _searchAttempted = true;
        }
        catch (OperationCanceledException)
        {
            // newer keystroke
        }
        catch
        {
            if (seq == _searchSeq)
            {
                _suggestions.Clear();
                _searchAttempted = true;
            }
        }
        finally
        {
            if (seq == _searchSeq)
                _searching = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SendInvite()
    {
        isSending = true;
        statusMessage = "";
        isError = false;
        _suggestions.Clear();
        try
        {
            var handle = string.IsNullOrWhiteSpace(searchHandle) ? null : searchHandle.Trim().TrimStart('@');
            var email = string.IsNullOrWhiteSpace(emailInput) ? null : emailInput.Trim();
            var res = await Engine.SendProjectInviteAsync(ProjectId, handle, email);

            if (res is { Ok: true })
            {
                statusMessage = res.Delivered
                    ? "Invite sent — expires in 48 hours."
                    : "Invite created. Share this link: " + res.InviteUrl;
                isError = !res.Delivered;
                searchHandle = "";
                emailInput = "";
            }
            else
            {
                statusMessage = res?.Error ?? "Could not send the invite.";
                isError = true;
            }
        }
        catch (Exception ex)
        {
            statusMessage = ex.Message;
            isError = true;
        }
        finally
        {
            isSending = false;
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }
}
