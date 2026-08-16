using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Api.Services;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Collaboration;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Fakes;

namespace PageToMovie.Api;

internal static class ApiServiceConfiguration
{
    public static WebApplicationBuilder ConfigureFilmStudioApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddAppLocalization();
        builder.Services.AddSingleton<PageToMovie.Engine.Collaboration.IProjectInviteMailer, PageToMovie.Engine.LoggingEmailSender>();
        // Root matches ProjectStore's own convention (WorkspaceRoot/projects), not IHostEnvironment
        // .ContentRootPath — those differ under PageToMovie__WorkspaceRoot / fakes tests (same class of bug
        // fixed for CostLedgerService/SceneVersionStore below). Using ContentRootPath silently wrote ACL docs
        // under the API project's source tree instead of alongside the actual project files, so grants never
        // took effect for the workspace the rest of the app was reading from.
        builder.Services.AddSingleton<ProjectAclService>(sp =>
        {
            var store = sp.GetRequiredService<ProjectStore>();
            var root = Path.Combine(store.WorkspaceRoot, ApiText.ProjectsFolder);
            var email = sp.GetService<PageToMovie.Engine.Collaboration.IProjectInviteMailer>();
            var users = new PageToMovie.Engine.Collaboration.UserDatabaseProjectUserDirectory(
                sp.GetRequiredService<UserDatabaseService>());
            return new ProjectAclService(root, users, email, store);
        });

        var listenPorts = new HashSet<string> { "5088", "8080", "80" };
        // Testability/deploy override: replace the default bind ports entirely (comma-separated). Lets a
        // second local instance (e.g. UI tests with capabilities forced off) bind a distinct port without
        // colliding on 5088. Unset in normal runs → the defaults above apply.
        var bindPortsOverride = Environment.GetEnvironmentVariable("PAGETOMOVIE_BIND_PORTS");
        if (!string.IsNullOrWhiteSpace(bindPortsOverride))
            listenPorts = new HashSet<string>(
                bindPortsOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var railwayEnvPort = Environment.GetEnvironmentVariable("PORT");
        if (!string.IsNullOrWhiteSpace(railwayEnvPort))
        {
            listenPorts.Add(railwayEnvPort.Trim());
        }
        var bindUrls = string.Join(";", listenPorts.Select(p => $"http://0.0.0.0:{p}"));
        builder.WebHost.UseUrls(bindUrls);

        builder.Services.Configure<PageToMovieOptions>(
            builder.Configuration.GetSection(PageToMovieOptions.SectionName));

        // Optional ThreadPool pre-warm (PageToMovie:ThreadPool:MinWorkerThreads) for 100-VU ramps.
        // 0 / unset = CLR defaults. Apply before host starts accepting requests.
        ApplyThreadPoolPrewarm(builder);

        // Default workspace = repo root (two levels up from host/PageToMovie.Api)
        var repoGuess = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
        var isDevelopment = builder.Environment.IsDevelopment();
        builder.Services.PostConfigure<PageToMovieOptions>(o =>
            ApplyWorkspaceAndJwtDefaults(o, repoGuess, isDevelopment));

        builder.Services.AddSingleton<MediaDurationProbe>();
        builder.Services.AddSingleton<SceneListCache>();
        builder.Services.AddSingleton<ProjectReadCache>();
        builder.Services.AddSingleton<ProjectStore>();
        // CostLedgerService takes a plain projects-root string, so it needs a factory rather than a bare
        // AddSingleton<T>() — and without ANY registration, Minimal API's parameter-source inference can't
        // recognize it as a service; it falls back to inferring [FromBody], which .NET disallows on GET
        // endpoints and throws at route-table build time, failing every request through the host (see
        // /api/projects/{id}/costs/summary). Root matches ProjectStore's own convention (WorkspaceRoot/projects),
        // not IHostEnvironment.ContentRootPath — those differ under PageToMovie__WorkspaceRoot / fakes tests.
        builder.Services.AddSingleton(sp =>
            new CostLedgerService(Path.Combine(sp.GetRequiredService<ProjectStore>().WorkspaceRoot, ApiText.ProjectsFolder)));
        // Same unregistered-string-ctor issue as CostLedgerService above (SceneVersionHistory.razor's
        // /versions endpoints, used by the Scenes-page scene-history panel).
        builder.Services.AddSingleton(sp =>
            new PageToMovie.Engine.Collaboration.SceneVersionStore(
                Path.Combine(sp.GetRequiredService<ProjectStore>().WorkspaceRoot, ApiText.ProjectsFolder)));

        builder.Services.AddSingleton<IProjectAclService>(sp => sp.GetRequiredService<ProjectAclService>());
        builder.Services.AddSingleton<IProjectLeaseService, ProjectLeaseService>();
        builder.Services.AddSingleton<IProjectPresenceService, ProjectPresenceService>();
        builder.Services.AddSingleton<IAutoProjectMerger, AutoProjectMerger>();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IJobStore, JobStore>();
        builder.Services.AddSingleton<ILockService, InMemoryLockService>();
        builder.Services.AddSingleton<IServerMetricsService, ServerMetricsService>();
        builder.Services.AddSingleton<IRuntimeConfigStore, RuntimeConfigStore>();
        builder.Services.AddSingleton<ApiWorkerPool>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<CreditService>();
        builder.Services.AddSingleton<ProjectArchiveService>();
        builder.Services.AddSingleton<CostReportService>();
        builder.Services.AddSingleton<CatalogUpdateProbeService>();
        builder.Services.AddHttpClient("catalog-probe", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(45);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PageToMovie-CatalogProbe/1.0");
        });
        builder.Services.AddSingleton<CharacterDesignService>();
        builder.Services.AddSingleton<LocationDesignService>();
        builder.Services.AddSingleton<CharacterBookPlateService>();
        builder.Services.AddSingleton<CastVisualLiteralizeService>();
        builder.Services.AddSingleton<CastFromScreenplayService>();
        builder.Services.AddSingleton<BookPrepareService>();
        builder.Services.AddSingleton<Stage1Service>();
        builder.Services.AddSingleton<SilentBeatActionClassifier>();
        builder.Services.AddSingleton<AmbientSfxClassifier>();
        builder.Services.AddSingleton<OnScreenCastClassifier>();
        builder.Services.AddSingleton<ExtendCutClassifier>();
        builder.Services.AddSingleton<SpeciesKindClassifier>();
        builder.Services.AddSingleton<PlateRankClassifier>();
        builder.Services.AddSingleton<ShotPlanRefiningClassifier>();
        builder.Services.AddSingleton<BeatPacingClassifier>();
        builder.Services.AddSingleton<CinematicLightingClassifier>();
        builder.Services.AddSingleton<CameraDirectorClassifier>();
        builder.Services.AddSingleton<NegativePromptClassifier>();
        builder.Services.AddSingleton<WardrobeContinuityClassifier>();
        builder.Services.AddSingleton<CharacterEmotionArcClassifier>();
        builder.Services.AddSingleton<SoundDesignComposerClassifier>();
        builder.Services.AddSingleton<SceneMusicCompositionService>();
        builder.Services.AddSingleton<DepthOfFieldClassifier>();
        builder.Services.AddSingleton<ColorPaletteGradingClassifier>();
        builder.Services.AddSingleton<Stage2PlannerService>();
        builder.Services.AddSingleton<VoicePreviewService>();
        builder.Services.AddHttpClient(ApiText.ElevenLabsClient, c =>
        {
            c.BaseAddress = TrailingSlashUri(SupportedModelCatalog.ElevenLabsApiBase);
            c.Timeout = TimeSpan.FromMinutes(3);
        });
        // Real IVoiceClient (ElevenLabs) is registered only in the !useFakes branch below, alongside the
        // other provider clients — under PageToMovie:UseFakes it resolves to FakeVoiceClient (via
        // AddPageToMovieFakes) so voice clone / dialogue TTS never reaches ElevenLabs even when a key is set.
        builder.Services.AddSingleton<PageToMovie.Engine.VoiceApply.VoicePreviewStore>();
        // Strategy order: Fal first (specific CanHandle), ElevenLabs last (default / mock fallback).
        builder.Services.AddSingleton<IVoiceApplyStrategy, PageToMovie.Engine.VoiceApply.FalVoiceApplyStrategy>();
        builder.Services.AddSingleton<IVoiceApplyStrategy, PageToMovie.Engine.VoiceApply.ElevenLabsVoiceApplyStrategy>();
        builder.Services.AddSingleton<VoiceCloneApplyService>();
        builder.Services.AddSingleton<ReviewEventStore>();
        builder.Services.AddSingleton<ProjectRulesService>();
        builder.Services.AddSingleton<LearningProposalService>();
        builder.Services.AddSingleton<ProposalChecklistService>();
        builder.Services.AddSingleton<EditLogService>();
        builder.Services.AddSingleton<ProjectTelemetryService>();
        builder.Services.AddSingleton<AiCallAnalyticsService>();
        builder.Services.AddSingleton<ReviewIndexService>();
        builder.Services.AddSingleton<ClipAutoReviewService>();
        builder.Services.AddSingleton<ClipDialogueVerificationService>();
        builder.Services.AddSingleton<ProjectArtifactIndexService>();
        builder.Services.AddSingleton<MediaShareService>();
        builder.Services.AddSingleton<DemoCatalogService>();
        builder.Services.AddSingleton<DemoUpvoteService>();
        builder.Services.AddHostedService<ServerMediaPruningService>();
        builder.Services.AddSingleton<MediaRegistryService>();
        builder.Services.AddSingleton<MediaSyncLocator>();
        builder.Services.AddSingleton<MediaProxyTicketStore>();
        builder.Services.AddSingleton<ClipSidecarService>();
        builder.Services.AddSingleton<VoiceAlignmentStore>();
        builder.Services.AddSingleton<MusicSidecarService>();
        builder.Services.AddSingleton<ProjectMigrationService>();
        builder.Services.AddSingleton<VolumeDiskTelemetryService>();
        builder.Services.AddSingleton<ProjectArchiveService>();
        builder.Services.AddSingleton<ServerLogExportService>();
        builder.Services.AddSingleton<YouTubeAuthService>();
        builder.Services.AddSingleton<DemoYouTubePublisherService>();
        builder.Services.AddSingleton<YouTubeChannelGallerySync>();
        builder.Services.AddSingleton<ProjectGitRepositoryService>();
        builder.Services.AddSingleton<ProjectAutoGitService>();
        builder.Services.AddSingleton<MovieAutoReviewService>();
        builder.Services.AddSingleton<ProjectInviteService>();
        builder.Services.AddSingleton<CreatorProfileService>();
        builder.Services.AddSingleton<ProjectContributionService>();
        var configuredWorkspaceRoot = builder.Configuration
            .GetSection(PageToMovieOptions.SectionName)
            .GetValue<string>(nameof(PageToMovieOptions.WorkspaceRoot));
        var dataRoot = UserDatabaseService.ResolveDataDirectory(
            string.IsNullOrWhiteSpace(configuredWorkspaceRoot) ? repoGuess : configuredWorkspaceRoot);
        var dpKeysDir = Path.Combine(dataRoot, "keys");
        MigrateLegacyDataProtectionKeys(dpKeysDir);
        Directory.CreateDirectory(dpKeysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
        builder.Services.AddSingleton<UserDatabaseService>();
        builder.Services.AddSingleton<BookTextRegistryService>();
        builder.Services.AddSingleton<GenerationErrorLogger>();
        builder.Services.AddHttpContextAccessor();

        // Blazor Web UI — Interactive WebAssembly (client DI lives in PageToMovie.Web Program.cs)
        builder.Services.AddRazorComponents()
            .AddInteractiveWebAssemblyComponents();

        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "PageToMovie.Antiforgery";
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        builder.Services.AddSingleton<IUserContext, HttpUserContext>();
        builder.Services.AddSingleton<IUserApiKeyProvider, DbUserApiKeyProvider>();
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient("resend", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PageToMovie/1.0");
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient("media-proxy", c => c.Timeout = TimeSpan.FromMinutes(10)));
        builder.Services.AddSingleton<IEmailSender>(CreateEmailSender);
        builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
        builder.Services.AddSingleton<FilmJobService>();
        builder.Services.AddSingleton<IJobProgressSink, SignalRJobProgressSink>();
        builder.Services.AddSingleton<AdminMetricsPushService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AdminMetricsPushService>());
        builder.Services.AddSingleton<HttpRequestMetrics>();
        builder.Services.AddSingleton<LoadSimLiveStore>();
        builder.Services.AddSingleton<ProcessHistoryStore>();

        // Grok clients: real HttpClient or fakes (PageToMovie:UseFakes)
        var useFakes = ResolveUseFakes(builder);
        ApiRuntime.UseFakes = useFakes;
        RegisterAiClients(builder, useFakes);

        // xAI Files + Responses — Stage‑1 multi-turn (file_id + previous_response_id).
        // Registered in both real and fakes mode so DI always resolves IBookFileSessionFactory for
        // FilmJobService / Stage1Service: TryCreateAsync returns null when xAI is unconfigured, and is
        // hard-disabled under PageToMovie:UseFakes (disableForFakes) so no book is ever uploaded to
        // api.x.ai in fakes mode — Stage 1 falls back to the fake IChatClient instead.
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<XaiResponsesClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(SupportedModelCatalog.XaiApiBase);
            c.Timeout = TimeSpan.FromMinutes(20);
        }));
        builder.Services.AddSingleton<PageToMovie.Core.Abstractions.IBookFileSessionFactory, BookFileSessionFactory>();
        builder.Services.AddSingleton<PageToMovie.Core.Abstractions.IFountainFileSessionFactory, FountainFileSessionFactory>();

        // Provider-agnostic telemetry/scoring services — registered regardless of PageToMovie:UseFakes
        // so admin route handlers that take them as parameters resolve as DI services rather than
        // being misinferred as request-body parameters, and so they're available at all in fakes mode.
        builder.Services.AddSingleton<SceneMusicScoringService>();
        builder.Services.AddSingleton<SmartClassifierModelRouter>();
        builder.Services.AddSingleton<ActionCameraOverheadLedger>();
        builder.Services.AddSingleton<AiActionOverheadClassifier>();
        builder.Services.AddSingleton<JitBenchmarkService>();
        builder.Services.AddSingleton<ClipTimingTelemetryRepository>(sp =>
        {
            var dbPath = Environment.GetEnvironmentVariable("PAGETOMOVIE_DB_PATH")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "pagetomovie.db");
            return new ClipTimingTelemetryRepository(dbPath, sp.GetService<ILogger<ClipTimingTelemetryRepository>>());
        });
        builder.Services.AddSingleton<GlobalTimingCalibrationService>();

        builder.Services.AddSignalR();
        builder.Services.AddCors(o =>
        {
            o.AddDefaultPolicy(p =>
                p.AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .SetIsOriginAllowed(_ => true));
        });

        // Large picture-book PDFs + full project zip import (book_images + assets).
        // Blazor InputFile / admin import allow up to 512MB; match server form + Kestrel limits.
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = 512L * 1024 * 1024;
            o.ValueLengthLimit = int.MaxValue;
            o.MultipartHeadersLengthLimit = 64 * 1024;
        });
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
        });

        return builder;
    }

    private static void ApplyWorkspaceAndJwtDefaults(PageToMovieOptions o, string repoGuess, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(o.WorkspaceRoot) || !Directory.Exists(o.WorkspaceRoot))
            o.WorkspaceRoot = repoGuess;

        o.Auth ??= new AuthOptions();
        var envKey = Environment.GetEnvironmentVariable("PageToMovie_JWT_KEY")
                     ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_JWT_KEY")
                     ?? Environment.GetEnvironmentVariable("PageToMovie__Auth__JwtSigningKey")
                     ?? Environment.GetEnvironmentVariable("FILMSTUDIO_JWT_KEY");

        var effective = !string.IsNullOrWhiteSpace(envKey) ? envKey.Trim() : o.Auth.JwtSigningKey;
        if (!isDevelopment && AuthOptions.IsInsecureDefaultJwtSigningKey(effective))
        {
            var secureKey = System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*", 64);
            o.Auth.JwtSigningKey = secureKey;
        }
    }

    private static void MigrateLegacyDataProtectionKeys(string dpKeysDir)
    {
        var legacyDpKeysDir = Path.Combine(Path.GetTempPath(), "ptm-dp-keys");
        if (Directory.Exists(dpKeysDir) ||
            string.Equals(dpKeysDir, legacyDpKeysDir, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(legacyDpKeysDir))
            return;
        Directory.CreateDirectory(dpKeysDir);
        foreach (var keyFile in Directory.EnumerateFiles(legacyDpKeysDir, "*", SearchOption.TopDirectoryOnly))
            File.Copy(keyFile, Path.Combine(dpKeysDir, Path.GetFileName(keyFile)), overwrite: false);
    }

    private static IEmailSender CreateEmailSender(IServiceProvider sp)
    {
        var mail = sp.GetRequiredService<IOptions<PageToMovieOptions>>().Value.Mail;
        if (!string.IsNullOrWhiteSpace(MailOptions.ResolveResendApiKey(mail)))
            return ActivatorUtilities.CreateInstance<ResendEmailSender>(sp);
        if (!string.IsNullOrWhiteSpace(mail?.SmtpHost))
            return ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp);
        return ActivatorUtilities.CreateInstance<LoggingEmailSender>(sp);
    }

    private static bool ResolveUseFakes(WebApplicationBuilder builder) =>
        builder.Configuration.GetValue("PageToMovie:UseFakes", false)
        || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "true", StringComparison.OrdinalIgnoreCase);

    private static void RegisterAiClients(WebApplicationBuilder builder, bool useFakes)
    {
        if (useFakes)
        {
            builder.Services.AddPageToMovieFakes();
            // Propagate the resolved UseFakes to an env var so PageToMovie.Core (which has no config
            // access) merges the fake test-vendor catalog — regardless of whether UseFakes came from
            // config/appsettings or an env var. See SupportedModelCatalog.FakeCatalogEnabled.
            Environment.SetEnvironmentVariable("PAGETOMOVIE_USE_FAKES", "1");
            return;
        }

        // Concrete provider clients — each gets its own named HttpClient + base address + connection pooling.
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVideoClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GrokVideoClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(15);
        }));
        // Single provider (xAI only) — bind IVideoEditClient straight to the concrete client, same
        // pattern as ILipSyncClient/FalLipSyncClient below (no MultiProvider* dispatcher needed).
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVideoEditClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GrokVideoEditClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(15);
        }));
        builder.Services.AddSingleton<IVideoEditClient>(sp => sp.GetRequiredService<GrokVideoEditClient>());
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiVideoClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GeminiVideoClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(15);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalVideoClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(FalVideoClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(15);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokImageClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GrokImageClient.ApiBase);
            c.Timeout = TimeSpan.FromSeconds(90);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiImageClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GeminiImageClient.ApiBase);
            c.Timeout = TimeSpan.FromSeconds(90);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalImageClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(FalImageClient.ApiBase);
            c.Timeout = TimeSpan.FromSeconds(90);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVisionClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GrokVisionClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(5);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokChatClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GrokChatClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(20);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<AnthropicChatClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(AnthropicChatClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(20);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiChatClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(GeminiChatClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(20);
        }));
        // ClipDialogueVerificationService needs Gemini's real native-video capability specifically
        // (not whatever IVisionClient's routing config points at) — see IGeminiVideoAnalysisClient.
        builder.Services.AddSingleton<IGeminiVideoAnalysisClient>(sp => sp.GetRequiredService<GeminiChatClient>());
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalAudioClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(FalAudioClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(5);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<SunoClient>(c =>
        {
            c.BaseAddress = new Uri(SunoClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(2); // each submit/poll call is short; overall wait spans many such calls
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<AiMusicApiClient>(c =>
        {
            c.BaseAddress = new Uri(AiMusicApiClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(2);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<ElevenLabsMusicClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(SupportedModelCatalog.ElevenLabsApiBase);
            c.Timeout = TimeSpan.FromMinutes(5); // composing a full-scene track can take a while
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<ElevenLabsScribeClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(SupportedModelCatalog.ElevenLabsApiBase);
            c.Timeout = TimeSpan.FromMinutes(3); // STT on a short dialogue segment
        }));
        builder.Services.AddSingleton<IAudioClient, MultiProviderAudioClient>();
        // Lip-sync and voice-clone narration: explicit, human-triggered actions only (never wired
        // into any automatic job/pipeline — see the lip-sync / voice/clone / voice/speak routes).
        // Fal.ai is the only provider today, so these bind straight to the concrete client (no
        // MultiProvider* dispatcher yet — same pattern as IGeminiVideoAnalysisClient below).
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalLipSyncClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(FalLipSyncClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(6);
        }));
        ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalVoiceCloneClient>(c =>
        {
            c.BaseAddress = TrailingSlashUri(FalVoiceCloneClient.ApiBase);
            c.Timeout = TimeSpan.FromMinutes(4);
        }));
        builder.Services.AddSingleton<ILipSyncClient>(sp => sp.GetRequiredService<FalLipSyncClient>());
        builder.Services.AddSingleton<IVoiceCloneClient>(sp => sp.GetRequiredService<FalVoiceCloneClient>());

        // Dispatchers: every existing caller keeps depending on IChatClient / IImageClient /
        // IVideoClient / IVisionClient and is routed to the right concrete provider client
        // per-call based on the requested model (see SupportedModelCatalog). Book-page OCR / cast
        // classify (TranscribePageAsync / ClassifyCharactersOnImageAsync) still only run on Grok in
        // practice — routing one of those to Anthropic or Gemini surfaces the NotSupportedException
        // those clients already throw for them — but clip/frame review (CompleteWithImagesAsync) is
        // real on all three and now follows the configured quality model.
        builder.Services.AddSingleton<IVideoClient, MultiProviderVideoClient>();
        builder.Services.AddSingleton<IImageClient, MultiProviderImageClient>();
        builder.Services.AddSingleton<MultiProviderChatClient>();
        // CachingChatClient wraps the real dispatcher so every classifier / planning call gets an
        // on-disk response cache for free (see CachingChatClient for why this beats local
        // tokenization for "speed"). Registered as itself too so admin endpoints (cache clear) can
        // reach it directly. Fakes mode below stays undecorated — tests assert call counts against
        // the fakes directly.
        builder.Services.AddSingleton(sp => new CachingChatClient(
            sp.GetRequiredService<MultiProviderChatClient>(),
            sp.GetRequiredService<IOptions<PageToMovieOptions>>(),
            sp.GetRequiredService<ILogger<CachingChatClient>>()));
        builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<CachingChatClient>());
        builder.Services.AddSingleton<IVisionClient, MultiProviderVisionClient>();

        // Voice clone + TTS (ElevenLabs). Real client only in the non-fakes branch; the fakes branch
        // above binds IVoiceClient to FakeVoiceClient so no clone/TTS call reaches ElevenLabs.
        builder.Services.AddSingleton<IVoiceClient>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiText.ElevenLabsClient);
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ElevenLabsVoiceClient>>();
            return new ElevenLabsVoiceClient(http, log, allowMockFallback: true);
        });
    }

    private static void ConfigurePooledSocketsHandler(IHttpClientBuilder b)
    {
        b.SetHandlerLifetime(TimeSpan.FromMinutes(15))
         .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
         {
             PooledConnectionLifetime = TimeSpan.FromMinutes(15),
             PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
             EnableMultipleHttp2Connections = true,
         });
    }

    private static Uri TrailingSlashUri(string apiBase) =>
        new(apiBase.TrimEnd(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) + Path.AltDirectorySeparatorChar);
    private static void ApplyThreadPoolPrewarm(WebApplicationBuilder builder)
    {
        var tp = builder.Configuration.GetSection(PageToMovieOptions.SectionName)
            .GetSection("ThreadPool");
        var minWorkers = tp.GetValue("MinWorkerThreads", 0);
        var minIo = tp.GetValue("MinIoThreads", 0);
        if (minWorkers <= 0 && minIo <= 0) return;
        ThreadPool.GetMinThreads(out var curW, out var curIo);
        ThreadPool.GetMaxThreads(out var maxW, out var maxIo);
        var w = minWorkers > 0 ? Math.Clamp(minWorkers, 1, maxW) : curW;
        // After the early return, minIo <= 0 implies minWorkers > 0, so fall back to that.
        var io = minIo > 0
            ? Math.Clamp(minIo, 1, maxIo)
            : Math.Clamp(minWorkers, 1, maxIo);
        if (w < curW) w = curW;
        if (io < curIo) io = curIo;
        if (ThreadPool.SetMinThreads(w, io))
            Console.WriteLine($"ThreadPool min threads set: workers={w} io={io} (was {curW}/{curIo})");
        else
            Console.WriteLine($"ThreadPool SetMinThreads failed (requested workers={w} io={io})");
    }

}
