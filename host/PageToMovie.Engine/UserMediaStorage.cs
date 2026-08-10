namespace PageToMovie.Engine;

/// <summary>
/// Manages user media storage options, tiering, resolution, and quality defaults using strongly-typed engine enums.
/// </summary>
public class UserMediaStorage
{
    public StorageTier Tier { get; set; } = StorageTier.Hot;
    public StorageTierKind StorageTierKind { get; set; } = StorageTierKind.Hot;
    public VideoResolution DefaultResolution { get; set; } = VideoResolution.Res1080p;
    public VideoResolutionPreset ResolutionPreset { get; set; } = VideoResolutionPreset.Res1080p;
    public ExportQualityLevel ExportQuality { get; set; } = ExportQualityLevel.High;

    public UserMediaStorage(StorageTier tier = StorageTier.Hot, VideoResolution resolution = VideoResolution.Res1080p)
    {
        Tier = tier;
        DefaultResolution = resolution;
        StorageTierKind = tier.ParseStorageTierKind();
        ResolutionPreset = resolution.ParseVideoResolutionPreset();
    }

    public UserMediaStorage(StorageTierKind tierKind, VideoResolutionPreset resolutionPreset, ExportQualityLevel exportQuality = ExportQualityLevel.High)
    {
        StorageTierKind = tierKind;
        ResolutionPreset = resolutionPreset;
        ExportQuality = exportQuality;
        Tier = tierKind.ToStorageTier();
        DefaultResolution = resolutionPreset.ToVideoResolution();
    }

    public string GetTierName() => StorageTierKind.ToApiString();
    public string GetResolutionString() => ResolutionPreset.ToApiString();
    public string GetExportQualityString() => ExportQuality.ToApiString();
}

public static class UserMediaStorageEnumConversions
{
    public static StorageTierKind ParseStorageTierKind(this StorageTier tier) => tier switch
    {
        StorageTier.Hot => StorageTierKind.Hot,
        StorageTier.Cold => StorageTierKind.Cold,
        StorageTier.Archive => StorageTierKind.Archive,
        _ => StorageTierKind.Hot
    };

    public static StorageTier ToStorageTier(this StorageTierKind tierKind) => tierKind switch
    {
        StorageTierKind.Hot => StorageTier.Hot,
        StorageTierKind.Warm => StorageTier.Hot,
        StorageTierKind.Cold => StorageTier.Cold,
        StorageTierKind.Archive => StorageTier.Archive,
        StorageTierKind.Temporary => StorageTier.Hot,
        _ => StorageTier.Hot
    };

    public static VideoResolutionPreset ParseVideoResolutionPreset(this VideoResolution res) => res switch
    {
        VideoResolution.Res720p => VideoResolutionPreset.Res720p,
        VideoResolution.Res1080p => VideoResolutionPreset.Res1080p,
        VideoResolution.Res4k => VideoResolutionPreset.Res4k,
        _ => VideoResolutionPreset.Res1080p
    };

    public static VideoResolution ToVideoResolution(this VideoResolutionPreset preset) => preset switch
    {
        VideoResolutionPreset.Res720p => VideoResolution.Res720p,
        VideoResolutionPreset.Res1080p => VideoResolution.Res1080p,
        VideoResolutionPreset.Res1440p => VideoResolution.Res1080p,
        VideoResolutionPreset.Res4k => VideoResolution.Res4k,
        VideoResolutionPreset.Res8k => VideoResolution.Res4k,
        _ => VideoResolution.Res1080p
    };
}
