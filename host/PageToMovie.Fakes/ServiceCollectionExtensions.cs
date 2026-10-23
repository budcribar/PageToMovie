using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace PageToMovie.Fakes;

public static class ServiceCollectionExtensions
{
    /// <summary>Register fake Grok clients (video/image/chat/vision).</summary>
    public static IServiceCollection AddPageToMovieFakes(this IServiceCollection services)
    {
        services.AddSingleton<IVideoClient, FakeGrokVideoClient>();
        services.AddSingleton<IImageClient, FakeGrokImageClient>();
        services.AddSingleton<IChatClient, FakeGrokChatClient>();
        services.AddSingleton<IVisionClient, FakeGrokVisionClient>();
        services.AddSingleton<IAudioClient, FakeAudioClient>();
        services.AddSingleton<IGeminiVideoAnalysisClient, FakeGeminiChatClient>();
        services.AddSingleton<ILipSyncClient, FakeLipSyncClient>();
        services.AddSingleton<IVoiceCloneClient, FakeVoiceCloneClient>();
        services.AddSingleton<IVideoEditClient, FakeGrokVideoEditClient>();
        services.AddSingleton<IVoiceClient, FakeVoiceClient>();
        services.AddSingleton<ITranscriptionClient, FakeTranscriptionClient>();
        return services;
    }
}
