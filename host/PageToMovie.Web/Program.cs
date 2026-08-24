using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using PageToMovie.Web.Services;
using PageToMovie.Cut.Services;

using PageToMovie.Core.Localization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAppLocalization();
builder.Services.AddPageToMovieCut();

builder.Services.Configure<EngineApiOptions>(
    builder.Configuration.GetSection(EngineApiOptions.SectionName));

builder.Services.AddScoped<AdminSessionService>();
// Server up/down prognosis. Fed by ServerHealthHandler (every HTTP call) and JobHubClient
// (SignalR lifecycle); probes /health with backoff only while down.
builder.Services.AddScoped(sp =>
{
    var health = new ServerHealthState();
    health.Probe = ct => sp.GetRequiredService<EngineApiClient>().ProbeHealthAsync(ct);
    return health;
});
builder.Services.AddScoped<ActiveProjectState>();
builder.Services.AddScoped<StudioCapabilityState>();
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<StudioUserPrefsService>();
builder.Services.AddScoped<ClientVideoStitchService>();
builder.Services.AddScoped<ClientMediaFolderService>();
builder.Services.AddScoped<ClientVoiceSubstitutionService>();
builder.Services.AddScoped<ClientVoiceCaptureService>();
builder.Services.AddScoped<ClientDialogueTimingService>();

// Same-origin by default (Api hosts this WASM). Override EngineApi:BaseUrl only when
// the API is on a different origin (local split ports).
builder.Services.AddScoped(sp =>
{
    var opts = sp.GetRequiredService<IOptions<EngineApiOptions>>().Value;
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
        ? nav.BaseUri
        : opts.BaseUrl.TrimEnd('/') + "/";
    var minutes = opts.TimeoutMinutes > 0 ? opts.TimeoutMinutes : 30;
    var handler = new ServerHealthHandler(sp.GetRequiredService<ServerHealthState>(), new HttpClientHandler());
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(baseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 120)),
    };
});

builder.Services.AddScoped(sp =>
{
    var http = sp.GetRequiredService<HttpClient>();
    return new EngineApiClient(
        http,
        sp.GetRequiredService<AdminSessionService>(),
        sp.GetRequiredService<IOptions<EngineApiOptions>>());
});

builder.Services.AddScoped(sp =>
{
    var opts = sp.GetRequiredService<IOptions<EngineApiOptions>>();
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new JobHubClient(opts, sp.GetRequiredService<AdminSessionService>(), nav, sp.GetRequiredService<ServerHealthState>());
});

builder.Services.AddScoped(sp =>
{
    var opts = sp.GetRequiredService<IOptions<EngineApiOptions>>();
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new ProjectCollabHubClient(opts, sp.GetRequiredService<AdminSessionService>(), nav);
});

await builder.Build().RunAsync();
