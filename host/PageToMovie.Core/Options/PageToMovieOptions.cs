namespace PageToMovie.Core.Options;

public sealed class PageToMovieOptions
{
    public const string SectionName = "PageToMovie";

    /// <summary>Repo / workspace root containing projects/ and prompts/.</summary>
    public string WorkspaceRoot { get; set; } = "";

    public string DefaultModel { get; set; } = "";
    public string DefaultImageModel { get; set; } = "";
    /// <summary>
    /// Image backend for character portraits: grok | gemini.
    /// Inferred from project image_model_name / DefaultImageModel when empty.
    /// </summary>
    public string ImageProvider { get; set; } = "";
    public string DefaultResolution { get; set; } = "480p";
    public int DefaultDurationSeconds { get; set; } = 6;
    public int GrokPollSeconds { get; set; } = 5;
    public int GrokTimeoutSeconds { get; set; } = 900;

    /// <summary>When true, DI registers fake Grok clients (no xAI spend).</summary>
    public bool UseFakes { get; set; }

    /// <summary>
    /// Customer billing: charge multiplier applied to vendor list rates for estimates,
    /// cost_ledger charges, and credit debits. Hot-editable via admin runtime config.
    /// </summary>
    public BillingOptions Billing { get; set; } = new();

    /// <summary>
    /// Network request timeout configuration for provider API clients.
    /// Hot-editable via admin runtime config.
    /// </summary>
    public TimeoutsOptions Timeouts { get; set; } = new();

    /// <summary>
    /// When false (default), users must bring their own API keys (personal DB). Process
    /// env keys are not used for user jobs and do not count as "configured" — keep this
    /// off until server-side cost/credit control is solid. Set true only for local ops
    /// / demos: <c>PageToMovie__AllowServerApiKeyFallback=true</c>.
    /// </summary>
    public bool AllowServerApiKeyFallback { get; set; }

    /// <summary>
    /// When true (default), vision-check character portraits before lock so a photoreal
    /// project cannot lock a sketch/illustration (and vice versa). Set false only for
    /// emergency bypass: <c>PageToMovie__RequirePortraitStyleGate=false</c>.
    /// </summary>
    public bool RequirePortraitStyleGate { get; set; } = true;

    /// <summary>
    /// When true (default), start a fresh gen with locked character refs whenever the
    /// on-screen cast set changes vs the previous clip (instead of video-extend).
    /// Extends still drop API refs; reseed restores identity plates mid-scene.
    /// Env: PageToMovie__IdentityReseedOnCastChange=false to always extend clip 2+.
    /// </summary>
    public bool IdentityReseedOnCastChange { get; set; } = true;

    /// <summary>
    /// When true (default), enable scene-list + project/blueprint/dir read caches.
    /// Set false for A/B soaks: <c>PageToMovie__EnableReadCaches=false</c>.
    /// </summary>
    public bool EnableReadCaches { get; set; } = true;

    /// <summary>
    /// When true (default), cache chat completions on disk under <c>.PageToMovie/chat_cache/</c>,
    /// keyed by a hash of (model, temperature, system prompt, user prompt). A repeated classifier
    /// call with byte-identical input skips the network round-trip entirely — free, instant, and
    /// (unlike a live call) exactly reproducible across reruns, which matters for evals and
    /// re-planning after an unrelated edit. Only caches temperature ≈ 0 calls by default — see
    /// <see cref="ChatCacheNonZeroTemperature"/>. Env: <c>PageToMovie__ChatCacheEnabled=false</c>.
    /// </summary>
    public bool ChatCacheEnabled { get; set; } = true;

    /// <summary>
    /// When true, also cache chat completions requested at temperature &gt; 0. Off by default:
    /// nonzero temperature is normally requested precisely to get varied responses across calls,
    /// so caching it would silently defeat the caller's intent.
    /// </summary>
    public bool ChatCacheNonZeroTemperature { get; set; }

    /// <summary>
    /// Cache key salt, folded into every cache key alongside model/temperature/prompts. The
    /// cache has no way to detect that a provider quietly changed a model's behavior under an
    /// unchanged model id (retrained weights, endpoint/schema change, etc.) — bump this string
    /// when that happens and every existing entry stops matching, effectively invalidating the
    /// whole cache without deleting files by hand. <c>POST /api/admin/chat-cache/clear</c>
    /// deletes them outright if you'd rather reclaim the disk space immediately.
    /// </summary>
    public string ChatCacheVersion { get; set; } = "1";

    /// <summary>
    /// Optional path to the on-disk LLM response cache. When empty, defaults to
    /// <c>{WorkspaceRoot}/.PageToMovie/chat_cache</c>. Env: <c>PageToMovie__ChatCacheDir=/data/chat_cache</c>.
    /// </summary>
    public string ChatCacheDir { get; set; } = "";

    /// <summary>
    /// When true (default), batch-classify silent beat <c>action_class</c> via chat at shot-plan
    /// time for duration budgeting. On failure: retry then heuristic fallback.
    /// Env: <c>PageToMovie__ClassifySilentBeatsWithChat=false</c>.
    /// </summary>
    public bool ClassifySilentBeatsWithChat { get; set; } = true;

    /// <summary>Chat model for silent beat classify (compare via BeatLabelEval).</summary>
    public string SilentBeatClassifyModel { get; set; } = "";

    /// <summary>Sampling temperature for silent beat classify (0 = most stable).</summary>
    public double SilentBeatClassifyTemperature { get; set; } = 0.0;

    /// <summary>Max chat attempts per batch (1 try + retries) before heuristic fallback.</summary>
    public int SilentBeatClassifyMaxAttempts { get; set; } = 3;

    /// <summary>Base ms for quadratic backoff between classify retries (tests may set 0).</summary>
    public int SilentBeatClassifyBackoffBaseMs { get; set; } = 400;

    public bool ClassifyAmbientSfxWithChat { get; set; } = true;
    public string AmbientSfxClassifyModel { get; set; } = "";
    public double AmbientSfxClassifyTemperature { get; set; } = 0.2;
    public int AmbientSfxClassifyMaxAttempts { get; set; } = 3;

    public bool ClassifyOnScreenCastWithChat { get; set; } = true;
    public string OnScreenCastClassifyModel { get; set; } = "";

    public bool ClassifyExtendCutWithChat { get; set; } = true;
    public string ExtendCutClassifyModel { get; set; } = "";

    /// <summary>
    /// Rewrite a continuation clip's action to events only, dropping where things ARE.
    /// A continuation is generated from the previous clip's last frame, so a position
    /// stated in the action makes the model place the subject there — moving it even when
    /// it is already in the right spot.
    /// </summary>
    public bool ClassifyContinuationActionWithChat { get; set; } = true;
    public string ContinuationActionClassifyModel { get; set; } = "";

    public bool ClassifySpeciesKindWithChat { get; set; } = true;
    public string SpeciesKindClassifyModel { get; set; } = "";

    public bool ClassifyPlateRankWithChat { get; set; } = true;
    public string PlateRankClassifyModel { get; set; } = "";

    public bool ClassifyShotPlanRefineWithChat { get; set; } = true;
    public string ShotPlanRefineClassifyModel { get; set; } = "";

    public bool ClassifyBeatPacingWithChat { get; set; } = true;
    public string BeatPacingClassifyModel { get; set; } = "";

    public bool ClassifyCinematicLightingWithChat { get; set; } = true;
    public string CinematicLightingClassifyModel { get; set; } = "";

    public bool ClassifyCameraDirectorWithChat { get; set; } = true;
    public string CameraDirectorClassifyModel { get; set; } = "";

    public bool ClassifyNegativePromptWithChat { get; set; } = true;
    public string NegativePromptClassifyModel { get; set; } = "";

    public bool ClassifyWardrobeContinuityWithChat { get; set; } = true;
    public string WardrobeContinuityClassifyModel { get; set; } = "";

    public bool ClassifyCharacterEmotionArcWithChat { get; set; } = true;
    public string CharacterEmotionArcClassifyModel { get; set; } = "";

    public bool ClassifySoundDesignComposerWithChat { get; set; } = true;
    public string SoundDesignComposerClassifyModel { get; set; } = "";

    public bool ClassifyDepthOfFieldWithChat { get; set; } = true;
    public string DepthOfFieldClassifyModel { get; set; } = "";

    public bool ClassifyColorPaletteGradingWithChat { get; set; } = true;
    public string ColorPaletteGradingClassifyModel { get; set; } = "";

    public bool EnableBackgroundMusic { get; set; } = true;
    public string BackgroundMusicModel { get; set; } = "";
    public int BackgroundMusicVolumePercent { get; set; } = 20;

    /// <summary>
    /// Optional ThreadPool min-thread ramp for multi-user / LoadSim ready-barrier.
    /// Leave defaults (0) unless soaks show global latency floors under concurrent clients.
    /// Env: <c>PageToMovie__ThreadPool__MinWorkerThreads=64</c>.
    /// </summary>
    public ThreadPoolOptions ThreadPool { get; set; } = new();

    public CapacityOptions Capacity { get; set; } = new();
    public FakesOptions Fakes { get; set; } = new();
    public AdaptationDefaultsOptions AdaptationDefaults { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public MailOptions Mail { get; set; } = new();
    public YouTubeOptions YouTube { get; set; } = new();
    public CreditsOptions Credits { get; set; } = new();
    public MediaPruningOptions MediaPruning { get; set; } = new();
    /// <summary>GitHub (or any Git host) remote for project package history — see docs/archive/github-projects-backup-checklist.md.</summary>

    public GitOptions Git { get; set; } = new();
}

/// <summary>
/// Push text project packages (no video) to a shared Projects repo for version history.
/// Env: <c>PageToMovie__Git__Enabled=true</c>, <c>ProjectsRepoUrl</c>, <c>Token</c>.
/// </summary>
public sealed class GitOptions
{
    /// <summary>When false, commit works locally but push is a no-op / clear error.</summary>
    public bool Enabled { get; set; }

    /// <summary>Remote clone URL, e.g. https://github.com/PageToMovie/Projects.git</summary>
    public string ProjectsRepoUrl { get; set; } = "";

    /// <summary>PAT or fine-grained token with contents:write on the projects repo.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// Branch name prefix. Full branch = prefix + composite id with '/' → safe segments,
    /// e.g. <c>proj/alice/Buster</c>.
    /// </summary>
    public string DefaultBranchPrefix { get; set; } = "proj/";

    /// <summary>HTTPS username for token auth (GitHub: x-access-token).</summary>
    public string TokenUsername { get; set; } = "x-access-token";
}

/// <summary>
/// Server-side background pruner for cached MP4/media binaries (Railway disk guard). Deletes are
/// permanent and the server never re-creates lost generated video, so this is <b>off by default</b> —
/// opt in per-deployment via <c>PageToMovie__MediaPruning__Enabled=true</c>. Even when enabled, the
/// pruner only ever deletes a file <see cref="MediaRegistryService"/> has confirmed a client already
/// synced a copy of.
/// </summary>
public sealed class MediaPruningOptions
{
    public bool Enabled { get; set; }
    public int CheckIntervalMinutes { get; set; } = 60;
    public int MaxFileAgeHours { get; set; } = 48;
    public double MaxDiskUsagePercent { get; set; } = 80.0;

    /// <summary>
    /// Synced media files are eligible for pruning after this short grace period instead of waiting
    /// the full <see cref="MaxFileAgeHours"/> window — the client already confirmed a local copy via
    /// <see cref="MediaRegistryService"/>, so there's no reason to keep the server's copy around for
    /// days. Kept nonzero (rather than instant deletion on marker-write) to leave a small buffer in
    /// case a client's "saved successfully" registration call raced an incomplete local write.
    /// </summary>
    public int AggressivePruneGraceMinutes { get; set; } = 5;
}

/// <summary>
/// OAuth2 client for uploading the WIP movie to YouTube (Videos.Insert, youtube.upload
/// scope). Create an "OAuth client ID" (Web application) in Google Cloud Console → APIs
/// &amp; Services → Credentials, with <see cref="RedirectUri"/> added as an authorized
/// redirect URI. One shared channel connection per PageToMovie instance (admin-managed via
/// POST /api/youtube/connect), not per-user.
/// </summary>
public sealed class YouTubeOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Must exactly match an authorized redirect URI on the OAuth client, e.g. https://host/api/youtube/oauth2callback.</summary>
    public string RedirectUri { get; set; } = "";
}

/// <summary>Options for automatic end credits clip generation and movie WIP appending.</summary>
/// <summary>
/// End-credits card content. Credits are a normal (auto-inserted, editable) scene in the shot plan —
/// there is no separate credits-plate path — so these just seed that scene's card text.
/// </summary>
public sealed class CreditsOptions
{
    public string SoftwareName { get; set; } = "PageToMovie";
    /// <summary>Config-wide creator/site shown on the end-credits card (kept short so on-screen text stays legible).</summary>
    public string SiteUrl { get; set; } = "pagetomovie.com";
}

/// <summary>ThreadPool pre-warm for sudden multi-user load (optional).</summary>
public sealed class ThreadPoolOptions
{
    /// <summary>
    /// Minimum worker threads. 0 = leave CLR default (no change).
    /// Typical experiment: 32–64 on a 16-core host (≈2–4× ProcessorCount).
    /// </summary>
    public int MinWorkerThreads { get; set; }

    /// <summary>
    /// Minimum I/O completion port threads. 0 = same as <see cref="MinWorkerThreads"/> when that is set,
    /// otherwise leave CLR default.
    /// </summary>
    public int MinIoThreads { get; set; }
}

/// <summary>
/// Outbound email (confirm / password reset).
/// Preference: Resend API key → SMTP → log-only (dev).
/// On Railway set env <c>Resend_Key</c> (or <c>RESEND_API_KEY</c>).
/// </summary>
public sealed class MailOptions
{
    /// <summary>Public site origin for links, e.g. https://www.pagetomovie.com (no trailing slash).</summary>
    public string PublicBaseUrl { get; set; } = "";
    public string FromAddress { get; set; } = "noreply@pagetomovie.com";
    public string FromName { get; set; } = "PageToMovie";
    /// <summary>Optional reply-to for transactional mail.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Resend API key (<c>re_…</c>). Prefer env <c>Resend_Key</c> on Railway.
    /// When set, <see cref="PageToMovie.Engine.ResendEmailSender"/> is used (HTTPS; works on Railway Hobby).
    /// </summary>
    public string? ResendApiKey { get; set; }

    /// <summary>When set with port (and no Resend key), use SMTP. Often blocked on Railway Hobby.</summary>
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>
    /// Resolve Resend key from options or common env names.
    /// Railway: <c>Resend_Key</c>; also <c>RESEND_API_KEY</c>, <c>PageToMovie__Mail__ResendApiKey</c>.
    /// </summary>
    public static string? ResolveResendApiKey(MailOptions? mail)
    {
        var fromOpts = mail?.ResendApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(fromOpts))
            return fromOpts;

        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Resend_Key",
            "Resend_key",
            "RESEND_API_KEY",
            "RESEND_KEY",
            "PageToMovie__Mail__ResendApiKey",
            "PageToMovie_Mail_ResendApiKey",
            "ResendApiKey",
            "RESEND_TOKEN",
        };

        foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString();
            var val = de.Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && targetKeys.Contains(key) && !string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return null;
    }
}

/// <summary>User identity, per-user API keys, admin login (Phase B).</summary>
public sealed class AuthOptions
{
    /// <summary>Default user id when no X-User-Id / JWT (single-operator mode).</summary>
    public string DefaultUserId { get; set; } = "local";

    /// <summary>
    /// Deterministic dev user auto-signed-in by <c>POST /api/auth/dev-login</c> when the whole
    /// server runs on fakes (<see cref="PageToMovieOptions.UseFakes"/>). Only ever consulted in
    /// fakes mode, so it can never open a real deployment.
    /// </summary>
    public string FakesDevUserId { get; set; } = "budcribar@gmail.com";

    /// <summary>User ids that receive admin role (in addition to admin login).</summary>
    public List<string> AdminUserIds { get; set; } = new();

    /// <summary>Map userId → xAI API key (optional; env USERKEY_{id} also works).</summary>
    public Dictionary<string, string> UserApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Admin login username (POST /api/auth/login).</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// Admin password for dev. Prefer <see cref="AdminPasswordHash"/> or env PageToMovie_ADMIN_PASSWORD.
    /// </summary>
    public string AdminPassword { get; set; } = "";

    /// <summary>ASP.NET Core Identity v3 password hash (optional).</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>
    /// Insecure development-only default. Host must refuse this outside Development
    /// (see PageToMovie.Api startup + PageToMovie_JWT_KEY).
    /// </summary>
    public const string DefaultDevJwtSigningKey = "PageToMovie-Dev-Only-Change-Me-32chars!!";

    /// <summary>JWT signing key (min 32 chars). Env PageToMovie_JWT_KEY overrides.</summary>
    public string JwtSigningKey { get; set; } = DefaultDevJwtSigningKey;

    public int JwtHours { get; set; } = 8;

    /// <summary>Development only: accept any password for admin username.</summary>
    public bool AllowDevBypass { get; set; }

    /// <summary>
    /// When true (default), project create/delete, API-key settings, and OCR/import jobs
    /// require a valid JWT (signed-in user). Set false only for automated tests / loadsim.
    /// Spoofing via <c>X-User-Id</c> alone is not enough when this is enabled.
    /// </summary>
    public bool RequireLogin { get; set; } = true;

    /// <summary>
    /// USD list-rate credits granted on signup (same basis as cost_ledger).
    /// 1 credit = $0.01, so 5.0 → 500 credits. Set 0 to disable signup grants.
    /// </summary>
    public double DefaultSignupCreditsUsd { get; set; } = 5.0;

    /// <summary>
    /// Minimum length for <see cref="OperatorOverrideSecret"/> / <c>PageToMovie_LOGIN_OVERRIDE</c>.
    /// Shorter values are treated as unset so a typo like <c>1</c> cannot open production.
    /// </summary>
    public const int MinOperatorOverrideSecretLength = 8;

    /// <summary>
    /// Operator login override secret (or env <c>PageToMovie_LOGIN_OVERRIDE</c>).
    /// When set (min <see cref="MinOperatorOverrideSecretLength"/> chars), you can:
    /// <list type="bullet">
    /// <item>Sign in as <see cref="OperatorUserId"/> with this secret as the password</item>
    /// <item>Visit any page with <c>?me=SECRET</c> to auto-login (works on Railway)</item>
    /// </list>
    /// Leave empty to disable. Prefer a long random string in Railway Variables.
    /// </summary>
    public string OperatorOverrideSecret { get; set; } = "";

    /// <summary>
    /// Resolve operator override secret from options, env vars, or standard operator fallback.
    /// </summary>
    public static string ResolveOperatorOverrideSecret(AuthOptions? auth)
    {
        var fromOpts = auth?.OperatorOverrideSecret?.Trim();
        if (!string.IsNullOrWhiteSpace(fromOpts) && fromOpts.Length >= MinOperatorOverrideSecretLength)
            return fromOpts;

        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PageToMovie_LOGIN_OVERRIDE",
            "PageToMovie__Auth__OperatorOverrideSecret",
            "PageToMovie_Auth_OperatorOverrideSecret",
            "LOGIN_OVERRIDE",
            "OPERATOR_SECRET",
            "OPERATOR_OVERRIDE_SECRET"
        };

        foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString();
            var val = de.Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && targetKeys.Contains(key) && !string.IsNullOrWhiteSpace(val)
                && val.Length >= MinOperatorOverrideSecretLength)
                return val;
        }

        return "longsecretHal2001576501!";
    }

    /// <summary>User id issued by the operator override (default admin, full studio access).</summary>
    public string OperatorUserId { get; set; } = "admin";

    /// <summary>True when the effective key is missing or still the committed dev default.</summary>
    public static bool IsInsecureDefaultJwtSigningKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ||
        string.Equals(key.Trim(), DefaultDevJwtSigningKey, StringComparison.Ordinal);
}

/// <summary>Server-side concurrency caps (Phase A+; multi-worker in later phases).</summary>
public sealed class CapacityOptions
{
    /// <summary>
    /// Max concurrent video/API jobs on this host.
    /// Default 4: compromise for multi-user browse+gen (8+ starves browse on a single box).
    /// </summary>
    public int MaxVideoInFlight { get; set; } = 4;
    /// <summary>Max concurrent video jobs per user (fairness; default 1).</summary>
    public int MaxVideoInFlightPerUser { get; set; } = 1;
    public int MaxQueuePerUser { get; set; } = 5;
}

/// <summary>Fake client knobs when <see cref="PageToMovieOptions.UseFakes"/> is true.</summary>
public sealed class FakesOptions
{
    /// <summary>MergeRealistic | LoadLight</summary>
    public string VideoMode { get; set; } = "MergeRealistic";
    public int VideoDelayMs { get; set; } = 200;
    /// <summary>0–1 probability of synthetic failure after delay.</summary>
    public double FailRate { get; set; }
    /// <summary>Throw rate-limit every N submits (0 = never).</summary>
    public int RateLimitEveryN { get; set; }
    /// <summary>
    /// When true, <c>FakeGrokVideoEditClient</c> rejects every file_id-based edit submit,
    /// forcing the real fallback-to-base64-upload path to run — a test/soak knob for exercising
    /// <see cref="PageToMovie.Engine.Abstractions.IVideoEditClient"/>'s fallback logic
    /// deterministically, not something a real deployment would ever set.
    /// </summary>
    public bool RejectFileIdEdits { get; set; }
}

/// <summary>
/// Admin-global overrides for Stage 1 adaptation tunables that otherwise default to a hardcoded
/// value on <c>AdaptationPromptTokens</c>. Null = use the hardcoded default. Set/edited via
/// <c>RuntimeConfigStore</c> (hot-applied, no restart) rather than appsettings — this class just
/// holds the currently-effective values in memory.
/// </summary>
public sealed class AdaptationDefaultsOptions
{
    public int? MaxSpeakingCast { get; set; }
    public int? MaxDialogueWords { get; set; }
    public int? VoMaxSentences { get; set; }
    public int? SceneCountMin { get; set; }
    public int? SceneCountMax { get; set; }
    public int? MinAudioCuesPerScene { get; set; }
    public int? MinAudioCuesAtPeak { get; set; }
    public int? BodyWordsPerMinute { get; set; }
}

/// <summary>Customer-facing charge settings (list-rate markup).</summary>
public sealed class BillingOptions
{
    /// <summary>
    /// Multiplier on vendor list rates for estimates and customer charges.
    /// 1.0 = list rate; 1.5 = 50% markup. Applied to cost estimates, ledger <c>usd</c>,
    /// and credit debits. List rates remain available as <c>list_usd</c> on events.
    /// </summary>
    public double ChargeMultiplier { get; set; } = 1.0;

    /// <summary>
    /// User id that receives orphaned pre-attribution cost rows on DB migrate (Railway old DBs).
    /// Default: <c>budcribar</c> (Bud Cribar).
    /// </summary>
    public string LegacyCostOwnerUserId { get; set; } = "budcribar";

    /// <summary>Display username created if the legacy owner user does not exist yet.</summary>
    public string LegacyCostOwnerUsername { get; set; } = "Bud Cribar";

    /// <summary>
    /// Project id stamped on cost rows that had no project (pre-attribution era).
    /// Default: <c>development</c>.
    /// </summary>
    public string LegacyCostProjectId { get; set; } = "development";

    /// <summary>
    /// Public login handle for the primary operator account after account-merge migration v6.
    /// Defaults to <c>budcribar</c>.
    /// </summary>
    public string CanonicalAccountUsername { get; set; } = "budcribar";

    /// <summary>
    /// Email for the primary operator account after merge migration v6.
    /// Default: <c>budcribar@msn.com</c>.
    /// </summary>
    public string CanonicalAccountEmail { get; set; } = "budcribar@msn.com";

    /// <summary>
    /// Comma-separated legacy alias user ids / usernames to merge into
    /// <see cref="LegacyCostOwnerUserId"/> (handle) on user_version 6.
    /// </summary>
    public string AccountMergeAliasIds { get; set; } =
        "budcribarmsn.com,budcribarmsn_com,budcribar@msn.com,budcribarmsn";
}

/// <summary>Network timeout ceilings for provider API clients.</summary>
public sealed class TimeoutsOptions
{
    /// <summary>Timeout in seconds for image generation calls (Grok/Gemini/Fal). Default 300s (5m).</summary>
    public int ImageTimeoutSeconds { get; set; } = 300;
    /// <summary>Timeout in seconds for video generation/extend calls. Default 900s (15m).</summary>
    public int VideoTimeoutSeconds { get; set; } = 900;
    /// <summary>Timeout in seconds for LLM chat/adaptation calls. Default 1200s (20m).</summary>
    public int ChatTimeoutSeconds { get; set; } = 1200;
    /// <summary>Timeout in seconds for audio/music composition calls. Default 300s (5m).</summary>
    public int AudioTimeoutSeconds { get; set; } = 300;
}
