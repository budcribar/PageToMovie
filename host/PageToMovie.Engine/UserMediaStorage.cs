namespace PageToMovie.Engine;

/// <summary>
/// Manages user media storage options, tiering, and resolution defaults using strongly-typed engine enums.
/// </summary>
public class UserMediaStorage
{
    public StorageTier Tier { get; set; } = StorageTier.Hot;
    public VideoResolution DefaultResolution { get; set; } = VideoResolution.Res1080p;

    public UserMediaStorage(StorageTier tier = StorageTier.Hot, VideoResolution resolution = VideoResolution.Res1080p)
    {
        Tier = tier;
        DefaultResolution = resolution;
    }

    public string GetTierName() => Tier.ToApiString();
    public string GetResolutionString() => DefaultResolution.ToApiString();
}
