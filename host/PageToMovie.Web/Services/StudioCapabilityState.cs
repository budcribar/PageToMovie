namespace PageToMovie.Web.Services;

/// <summary>
/// JIT, capability-focused readiness for the model/key-dependent studio actions (video, image,
/// video review, music, voice). A capability is "ready" when at least one provider that offers it
/// is configured right now (a key, or fakes) — sourced live from <c>GET /api/capabilities</c>.
/// NOT per-project (a project's required capabilities change as it develops) and never cached onto
/// a project; refresh whenever keys may have changed. Used to disable actions with a "Set up →"
/// hint (see <c>CapabilityLockedControl</c>) rather than showing them and failing on click.
/// </summary>
public sealed class StudioCapabilityState
{
    public event Action? Changed;

    public bool Loaded { get; private set; }

    public bool VideoReady { get; private set; }
    public bool ImageReady { get; private set; }
    public bool VideoReviewReady { get; private set; }
    public bool MusicReady { get; private set; }
    public bool VoiceCloneReady { get; private set; }

    public static string VideoBlockedReason => "Set up video in Settings to enable this.";
    public static string ImageBlockedReason => "Set up pictures in Settings to enable this.";
    public static string VideoReviewBlockedReason => "Set up review in Settings to enable this.";
    public static string MusicBlockedReason => "Set up music in Settings to enable this.";
    public static string VoiceCloneBlockedReason => "Set up voice in Settings to enable this.";

    public static string VideoSettingsHref => "/configuration?focus=video#api-keys";
    public static string ImageSettingsHref => "/configuration?focus=image#api-keys";
    public static string VideoReviewSettingsHref => "/configuration?focus=review#api-keys";
    public static string MusicSettingsHref => "/configuration?focus=music#api-keys";
    public static string VoiceCloneSettingsHref => "/configuration?focus=voice#api-keys";

    public async Task RefreshAsync(EngineApiClient engine, CancellationToken ct = default)
    {
        bool video = VideoReady, image = ImageReady, review = VideoReviewReady,
             music = MusicReady, voice = VoiceCloneReady;
        try
        {
            var resp = await engine.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            var caps = resp?.Capabilities;
            if (caps is not null)
            {
                video = Has(caps, "video");
                image = Has(caps, "image");
                review = Has(caps, "review");
                music = Has(caps, "music");
                voice = Has(caps, "voice");
            }
        }
        catch { /* offline → keep prior flags */ }

        var changed = !Loaded
            || video != VideoReady || image != ImageReady || review != VideoReviewReady
            || music != MusicReady || voice != VoiceCloneReady;

        VideoReady = video;
        ImageReady = image;
        VideoReviewReady = review;
        MusicReady = music;
        VoiceCloneReady = voice;
        Loaded = true;

        if (changed) Changed?.Invoke();
    }

    private static bool Has(IReadOnlyDictionary<string, bool> caps, string key) =>
        caps.TryGetValue(key, out var v) && v;
}

/// <summary>Response for <c>GET /api/capabilities</c>.</summary>
public sealed class CapabilitiesResponse
{
    public bool Ok { get; set; }
    public Dictionary<string, bool> Capabilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
