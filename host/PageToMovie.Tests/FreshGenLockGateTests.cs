using System;
using System.Collections.Generic;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The fresh-video-gen locked-reference gate (FilmJobService.OnScreenVisualKeys, behind
/// EnsureFreshGenHasLockedRefs / MissingOnScreenLockKeys) decides which on-screen characters must
/// have a locked reference image. It must exclude voice-only roles AND group/ensemble cast — a group
/// has no single portrait identity to lock, so requiring a ref for it blocks generation forever.
/// Regression for the batch that generated clip 1 (Mary+Lamb) then failed every clip with "Children"
/// on screen: "Locked character reference images required for fresh video gen … Missing ref for:
/// Character_Children". (The pre-flight cast gate was exempted earlier; this deeper gate was missed.)
/// </summary>
public class FreshGenLockGateTests
{
    private static ClipVideoPromptBuilder.CharacterProfile Profile(
        string key, bool voiceOnly = false, string castKind = "", string display = "", string description = "") =>
        new() { Key = key, VoiceOnly = voiceOnly, CastKind = castKind, DisplayName = display, Description = description };

    [Fact]
    public void OnScreenVisualKeys_excludes_groups_and_voice_only_keeps_individuals()
    {
        var built = new ClipVideoPromptBuilder.PromptBuildResult
        {
            OnScreenKeys = new[] { "Character_Mary", "Character_Children", "Character_Narrator" },
        };
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = Profile("Character_Mary"),
            ["Character_Children"] = Profile("Character_Children"),
            ["Character_Narrator"] = Profile("Character_Narrator", voiceOnly: true),
        };

        var keys = FilmJobService.OnScreenVisualKeys(built, profiles);

        Assert.Contains("Character_Mary", keys);          // individual → still needs a locked ref
        Assert.DoesNotContain("Character_Children", keys); // group → exempt (no lockable identity)
        Assert.DoesNotContain("Character_Narrator", keys); // voice-only → exempt
    }

    [Fact]
    public void OnScreenVisualKeys_group_only_scene_needs_no_refs()
    {
        // A crowd/ensemble-only shot must not demand any locked reference (nothing to lock).
        var built = new ClipVideoPromptBuilder.PromptBuildResult
        {
            OnScreenKeys = new[] { "Character_Children", "Character_Crowd" },
        };
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Children"] = Profile("Character_Children"),
            ["Character_Crowd"] = Profile("Character_Crowd"),
        };

        Assert.Empty(FilmJobService.OnScreenVisualKeys(built, profiles));
    }

    [Fact]
    public void OnScreenVisualKeys_uses_full_signal_cast_kind_for_non_token_group_key()
    {
        // A group whose KEY is not a plural token (Character_The_Choir) is only recognizable as a
        // group via its explicit cast_kind. The gen gate must read that full signal (cast_kind +
        // display + description) exactly like the cast gates do — key-only detection would let it
        // slip through and hard-fail generation demanding a portrait a chorus can never have.
        var built = new ClipVideoPromptBuilder.PromptBuildResult
        {
            OnScreenKeys = new[] { "Character_The_Choir", "Character_Ebenezer" },
        };
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_The_Choir"] = Profile("Character_The_Choir", castKind: "chorus", display: "The Choir"),
            ["Character_Ebenezer"] = Profile("Character_Ebenezer", castKind: "individual", display: "Ebenezer"),
        };

        var keys = FilmJobService.OnScreenVisualKeys(built, profiles);

        Assert.DoesNotContain("Character_The_Choir", keys); // cast_kind:"chorus" → exempt at gen gate
        Assert.Contains("Character_Ebenezer", keys);        // real individual → still needs a locked ref
    }

    [Fact]
    public void ClipReferenceImagesForSubmit_returns_null_when_extending_via_file_id_or_path()
    {
        var built = new ClipVideoPromptBuilder.PromptBuildResult
        {
            ReferenceImagePaths = new[] { "/path/to/char_ref.png", "/path/to/loc_ref.png" },
        };

        // Fresh gen: attachments preserved
        var freshRefs = FilmJobService.ClipReferenceImagesForSubmit(null, null, built);
        Assert.NotNull(freshRefs);
        Assert.Equal(2, freshRefs.Count);

        // Extend via previous local video path: attachments suppressed
        var pathExtendRefs = FilmJobService.ClipReferenceImagesForSubmit("/path/to/prev.mp4", null, built);
        Assert.Null(pathExtendRefs);

        // Extend via server cached file_id: attachments suppressed
        var fileIdExtendRefs = FilmJobService.ClipReferenceImagesForSubmit(null, "file_abc123", built);
        Assert.Null(fileIdExtendRefs);
    }
}
