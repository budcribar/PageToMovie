using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public sealed class CutFolderChromeTests
{
    [Fact]
    public void Standalone_cut_always_shows_pick_buttons()
    {
        Assert.False(CutFolderChrome.IsHosted(autoAttachHostFolder: false, hostProjectPrefix: null));
        Assert.False(CutFolderChrome.IsHosted(autoAttachHostFolder: true, hostProjectPrefix: " "));
        Assert.True(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: false,
            hostProjectPrefix: null,
            hasFolder: false,
            attaching: false,
            hostAttachTried: false,
            hostFolderUnavailable: false));
        Assert.True(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: false,
            hostProjectPrefix: "proj",
            hasFolder: true,
            attaching: true,
            hostAttachTried: true,
            hostFolderUnavailable: true));
        Assert.False(CutFolderChrome.ShowAttachRetry(
            autoAttachHostFolder: false,
            hostProjectPrefix: null,
            hasFolder: false,
            attaching: false,
            hostAttachFailed: true));
    }

    [Fact]
    public void Hosted_review_hides_pick_buttons_while_attached_or_attaching()
    {
        Assert.True(CutFolderChrome.IsHosted(autoAttachHostFolder: true, hostProjectPrefix: "proj"));
        Assert.False(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: true,
            attaching: false,
            hostAttachTried: true,
            hostFolderUnavailable: false));
        Assert.False(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: true,
            hostAttachTried: true,
            hostFolderUnavailable: false));
        Assert.False(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: false,
            hostAttachTried: false,
            hostFolderUnavailable: false));
        Assert.False(CutFolderChrome.ShowAttachRetry(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: true,
            attaching: false,
            hostAttachFailed: false));
        Assert.False(CutFolderChrome.ShowAttachRetry(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: true,
            hostAttachFailed: false));
    }

    [Fact]
    public void Hosted_attach_failure_shows_retry_and_last_resort_picker_only_when_host_has_no_folder()
    {
        Assert.True(CutFolderChrome.ShowAttachRetry(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: false,
            hostAttachFailed: true));
        Assert.False(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: false,
            hostAttachTried: true,
            hostFolderUnavailable: false));
        Assert.True(CutFolderChrome.ShowPickButtons(
            autoAttachHostFolder: true,
            hostProjectPrefix: "proj",
            hasFolder: false,
            attaching: false,
            hostAttachTried: true,
            hostFolderUnavailable: true));
        Assert.Equal("Could not open project media.", CutFolderChrome.AttachFailedMessage);
    }

    [Fact]
    public void Cut_editor_toolbar_uses_hosted_folder_chrome()
    {
        var markup = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Pages", "CutEditor.razor")));
        var code = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Pages", "CutEditor.razor.cs")));

        Assert.Contains("@if (ShowFolderPickButtons)", markup, StringComparison.Ordinal);
        Assert.Contains("Pick folder", markup, StringComparison.Ordinal);
        Assert.Contains("Choose MP4s", markup, StringComparison.Ordinal);
        Assert.Contains("Save cut", markup, StringComparison.Ordinal);
        Assert.Contains("@if (ShowHostAttachRetry)", markup, StringComparison.Ordinal);
        Assert.Contains("Try again", markup, StringComparison.Ordinal);
        Assert.Contains("CutFolderChrome.ShowPickButtons", code, StringComparison.Ordinal);
        Assert.Contains("CutFolderChrome.ShowAttachRetry", code, StringComparison.Ordinal);
        Assert.Contains("RetryHostAttachAsync", code, StringComparison.Ordinal);
        Assert.Contains("PageToMovieCut.attachHostMediaFolderAsync", File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Services", "CutFolderService.cs"))), StringComparison.Ordinal);
    }
}
