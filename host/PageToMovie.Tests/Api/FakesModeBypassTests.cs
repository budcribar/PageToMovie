using Microsoft.Extensions.DependencyInjection;
using PageToMovie.Core.Abstractions;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.VoiceApply;
using PageToMovie.Fakes;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// Locks in the spend-prevention guarantees for fakes mode: every provider-facing capability is
/// swapped to a fake by AddPageToMovieFakes, and no book-file session reaches api.x.ai when
/// UseFakes is on.
/// </summary>
public class FakesModeBypassTests : IClassFixture<PageToMovieApiFactory>
{
    private readonly PageToMovieApiFactory _factory;

    public FakesModeBypassTests(PageToMovieApiFactory factory) => _factory = factory;

    [Fact]
    public void AddPageToMovieFakes_swaps_every_provider_interface_to_a_fake()
    {
        var services = new ServiceCollection();
        services.AddPageToMovieFakes();

        // Each provider-facing capability must resolve to a deterministic offline fake. If a new
        // provider interface is added without a fake, this list should grow with it.
        AssertFake<IVideoClient, FakeGrokVideoClient>(services);
        AssertFake<IImageClient, FakeGrokImageClient>(services);
        AssertFake<IChatClient, FakeGrokChatClient>(services);
        AssertFake<IVisionClient, FakeGrokVisionClient>(services);
        AssertFake<IAudioClient, FakeAudioClient>(services);
        AssertFake<IGeminiVideoAnalysisClient, FakeGeminiChatClient>(services);
        AssertFake<ILipSyncClient, FakeLipSyncClient>(services);
        AssertFake<IVoiceCloneClient, FakeVoiceCloneClient>(services);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(XaiVoiceCloneClient));
        // Previously wired to the real ElevenLabs client even under fakes — now faked too.
        AssertFake<IVoiceClient, FakeVoiceClient>(services);
        AssertFake<IVideoEditClient, FakeGrokVideoEditClient>(services);
        AssertFake<ITranscriptionClient, FakeTranscriptionClient>(services);
    }

    private static void AssertFake<TInterface, TFake>(IServiceCollection services)
    {
        Assert.Contains(services, d =>
            d.ServiceType == typeof(TInterface) && d.ImplementationType == typeof(TFake));
    }

    [Fact]
    public void Fakes_host_registers_voice_strategies_without_the_xai_http_client()
    {
        Assert.Null(_factory.Services.GetService<XaiVoiceCloneClient>());
        Assert.IsType<FakeVoiceCloneClient>(_factory.Services.GetRequiredService<IVoiceCloneClient>());

        var strategies = _factory.Services.GetServices<IVoiceApplyStrategy>().ToList();
        Assert.Equal(3, strategies.Count);
        Assert.IsType<FalVoiceApplyStrategy>(strategies[0]);
        Assert.IsType<XaiVoiceApplyStrategy>(strategies[1]);
        Assert.IsType<ElevenLabsVoiceApplyStrategy>(strategies[2]);
    }

    [Fact]
    public async Task BookFileSession_factory_is_disabled_in_fakes_mode()
    {
        var factory = _factory.Services.GetRequiredService<IBookFileSessionFactory>();
        // Even for an xAI/Grok model with real text, no session is created (no upload to api.x.ai).
        var session = await factory.TryCreateAsync("book-1", "Once upon a time.", "grok-4-fast-reasoning");
        Assert.Null(session);
    }

    [Fact]
    public void FountainFileSession_factory_is_disabled_in_fakes_mode()
    {
        var factory = _factory.Services.GetRequiredService<IFountainFileSessionFactory>();
        var session = factory.TryCreate(Path.GetTempPath(), "grok-4.6");
        Assert.Null(session);
    }
}
