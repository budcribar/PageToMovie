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

// Forwarders: ReviewShare → Host.*
public partial class Review
{
    internal void PrepopulateDemoFields() => Share.PrepopulateDemoFields();

    internal static string FormatDisplayTitle(string? rawProjectId) => ReviewShare.FormatDisplayTitle(rawProjectId);

    internal static string BuildSmartDescription(string? rawProjectId, string title) => ReviewShare.BuildSmartDescription(rawProjectId, title);

    internal void ReportPublishProgress(int pct, string status) => Share.ReportPublishProgress(pct, status);

    internal Task RefreshYouTubeStatusAsync() => Share.RefreshYouTubeStatusAsync();

    internal void HandleYouTubeOAuthRedirect() => Share.HandleYouTubeOAuthRedirect();

    internal void CheckIncompleteMovieState() => Share.CheckIncompleteMovieState();

    internal Task ConfirmIncompleteAndPublishAsync() => Share.ConfirmIncompleteAndPublishAsync();

    internal void CancelIncompleteWarning() => Share.CancelIncompleteWarning();

    internal Task PublishDemoAsync() => Share.PublishDemoAsync();

    internal Task<string?> EnsureShareableMovieUrlAsync() => Share.EnsureShareableMovieUrlAsync();

    internal Task ConnectYouTubeAsync() => Share.ConnectYouTubeAsync();

    internal Task DisconnectYouTubeAsync() => Share.DisconnectYouTubeAsync();

    internal Task StartYouTubeUploadAsync() => Share.StartYouTubeUploadAsync();


    internal bool CanShareMovie => Share.CanShareMovie;
}
