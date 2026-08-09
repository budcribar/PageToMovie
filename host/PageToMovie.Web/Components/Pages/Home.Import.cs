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

// Forwarders: HomeImport → Host.*
public partial class Home
{
    internal Task BackupProjectAsync() => Import.BackupProjectAsync();

    internal void ToggleImport() => Import.ToggleImport();

    internal void OnImportFileSelected(InputFileChangeEventArgs e) => Import.OnImportFileSelected(e);

    internal static string DefaultNameFromFileName(string? fileName) => HomeImport.DefaultNameFromFileName(fileName);

    internal Task HandleImportAsync() => Import.HandleImportAsync();

}
