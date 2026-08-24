using Microsoft.Extensions.DependencyInjection;

namespace PageToMovie.Cut.Services;

public static class CutServiceCollectionExtensions
{
    public static IServiceCollection AddPageToMovieCut(this IServiceCollection services)
    {
        services.AddScoped<CutFolderService>();
        services.AddScoped<CutComposeService>();
        return services;
    }
}
