namespace PageToMovie.Engine;

public enum LightingCondition
{
    Daylight,
    NightInterior,
    GoldenHour,
    NeonLight
}

public enum CameraAngle
{
    LowAngle,
    HighAngle,
    EyeLevel,
    BirdEye
}

public enum CacheInvalidationReason
{
    JobCompleted,
    UserEdit,
    ProjectDeleted
}
