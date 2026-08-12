namespace PageToMovie.Core.Utils;

/// <summary>
/// Shared JSON / seed field names. Use these instead of repeating string literals (S1192).
/// </summary>
public static class JsonKeys
{
    public const string SceneNumber = "scene_number";
    public const string ClipNumber = "clip_number";
    public const string Dialogue = "dialogue";
    public const string Description = "description";
    public const string Speaker = "speaker";
    public const string MovieTitle = "movie_title";
    public const string AudioPayload = "audio_payload";
    public const string ApplicationJson = "application/json";
    public const string CharacterPrefix = "Character_";
    public const string LocationPrefix = "Loc_";
}
