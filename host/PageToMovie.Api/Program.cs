using PageToMovie.Api;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureFilmStudioApi();
var app = builder.Build();
await app.UseFilmStudioPipelineAsync();
app.MapFilmStudioEndpoints();
await app.RunFilmStudioStartupAsync();
await app.RunAsync();

namespace PageToMovie.Api
{
    // Expose entry assembly for WebApplicationFactory integration tests.
    public partial class Program { }
}
