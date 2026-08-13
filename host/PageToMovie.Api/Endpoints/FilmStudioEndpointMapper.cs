using PageToMovie.Api.Collaboration;

namespace PageToMovie.Api;

public static class FilmStudioEndpointMapper
{
    public static WebApplication MapFilmStudioEndpoints(this WebApplication app)
    {
        app.MapAuthEndpoints();
        app.MapAdminEndpoints();
        app.MapYouTubeEndpoints();
        app.MapHealthEndpoints();
        app.MapJobEndpoints();
        app.MapModelEndpoints();
        app.MapSystemEndpoints();
        app.MapProjectEndpoints();
        app.MapLocationEndpoints();
        app.MapCharacterEndpoints();
        app.MapVoiceEndpoints();
        app.MapAdaptationEndpoints();
        app.MapGitVersionEndpoints();
        app.MapSceneClipEndpoints();
        app.MapCostEndpoints();
        app.MapDemoEndpoints();
        app.MapMediaEndpoints();
        app.MapUserEndpoints();
        app.MapMiscEndpoints();
        app.MapCollaborationEndpoints();
        app.MapMergeEndpoints();
        app.MapHub<ProjectHub>("/hubs/project");
        app.MapInviteEndpoints();
        return app;
    }
}
