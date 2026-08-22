using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PageToMovie.Cut;
using PageToMovie.Cut.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<CutFolderService>();
builder.Services.AddScoped<CutComposeService>();

await builder.Build().RunAsync();
