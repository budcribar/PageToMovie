using System.ComponentModel;

using PageToMovie.Core.Models;

namespace PageToMovie.ScreenplayEditor.Models;

public enum BeatType
{
    Action,
    Dialogue,
    Parenthetical,
    Transition,
    Note,
    Centered,
    /// <summary>Audio-only cue written as (SOUND: …) in Fountain — not visual action.</summary>
    Sound
}

public enum SceneEnvironment
{
    [Description("INT.")]
    INT,
    [Description("EXT.")]
    EXT,
    [Description("INT./EXT.")]
    INT_EXT
}

public enum TimeOfDay
{
    [Description("DAY")]
    DAY,
    [Description("NIGHT")]
    NIGHT,
    [Description("CONTINUOUS")]
    CONTINUOUS,
    [Description("MOMENTS LATER")]
    MOMENTS_LATER,
    [Description("DAWN")]
    DAWN,
    [Description("DUSK")]
    DUSK
}

public enum SpeakerExtension
{
    [Description("")]
    None,
    [Description("V.O.")]
    VO,
    [Description("O.S.")]
    OS,
    [Description("CONT'D")]
    CONTD
}

public enum TransitionPreset
{
    [Description("CUT TO:")]
    CutTo,
    [Description("FADE IN:")]
    FadeIn,
    [Description("FADE OUT.")]
    FadeOut,
    [Description("DISSOLVE TO:")]
    DissolveTo,
    [Description("SMASH CUT TO:")]
    SmashCutTo,
    [Description("BLACKOUT")]
    Blackout
}

public enum ComponentVariant
{
    Primary,
    Secondary,
    Success,
    Info,
    Warning,
    Danger,
    Dark,
    OutlinePrimary,
    OutlineSecondary,
    OutlineSuccess,
    OutlineInfo,
    OutlineDanger,
    OutlineLight
}

public static class EnumExtensions
{
    public static string ToHeadingPrefix(this SceneEnvironment env) => env switch
    {
        SceneEnvironment.INT => "INT.",
        SceneEnvironment.EXT => "EXT.",
        SceneEnvironment.INT_EXT => "INT./EXT.",
        _ => "INT."
    };

    public static SceneEnvironment ParseEnvironment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SceneEnvironment.INT;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.StartsWith("INT./EXT") || upper.StartsWith("I/E") || upper.StartsWith("INT/EXT")) return SceneEnvironment.INT_EXT;
        if (upper.StartsWith("EXT")) return SceneEnvironment.EXT;
        return SceneEnvironment.INT;
    }

    public static string ToDisplayString(this TimeOfDay time) => time switch
    {
        TimeOfDay.DAY => "DAY",
        TimeOfDay.NIGHT => "NIGHT",
        TimeOfDay.CONTINUOUS => "CONTINUOUS",
        TimeOfDay.MOMENTS_LATER => "MOMENTS LATER",
        TimeOfDay.DAWN => "DAWN",
        TimeOfDay.DUSK => "DUSK",
        _ => "DAY"
    };

    public static string ToDisplayString(this SpeakerExtension ext) => ext switch
    {
        SpeakerExtension.VO => "V.O.",
        SpeakerExtension.OS => "O.S.",
        SpeakerExtension.CONTD => "CONT'D",
        _ => ""
    };

    public static string ToDisplayString(this TransitionPreset preset) => preset switch
    {
        TransitionPreset.CutTo => "CUT TO:",
        TransitionPreset.FadeIn => "FADE IN:",
        TransitionPreset.FadeOut => "FADE OUT.",
        TransitionPreset.DissolveTo => "DISSOLVE TO:",
        TransitionPreset.SmashCutTo => "SMASH CUT TO:",
        TransitionPreset.Blackout => "BLACKOUT",
        _ => "CUT TO:"
    };

    public static string GetJargonHint(this SceneEnvironment env) => env switch
    {
        SceneEnvironment.INT => "INT. — Interior. The scene is set inside (room, car, building).",
        SceneEnvironment.EXT => "EXT. — Exterior. The scene is set outdoors.",
        SceneEnvironment.INT_EXT => "INT./EXT. — Interior and exterior. The action crosses or straddles both.",
        _ => "Scene environment (INT. / EXT.) — where the camera is for this scene."
    };

    public static string GetJargonHint(this TimeOfDay time) => time switch
    {
        TimeOfDay.DAY => "DAY — Scene plays in daytime lighting.",
        TimeOfDay.NIGHT => "NIGHT — Scene plays at night.",
        TimeOfDay.CONTINUOUS => "CONTINUOUS — Same continuous moment as the previous scene (no time jump).",
        TimeOfDay.MOMENTS_LATER => "MOMENTS LATER — A short jump forward in time from the previous scene.",
        TimeOfDay.DAWN => "DAWN — Early morning light, sunrise.",
        TimeOfDay.DUSK => "DUSK — Evening light, sunset.",
        _ => "Time of day on the scene heading (after the location)."
    };

    public static string GetJargonHint(this SpeakerExtension ext) => ext switch
    {
        SpeakerExtension.VO => "V.O. (Voice Over) — Character is heard but not speaking on-camera (narration, phone, thoughts).",
        SpeakerExtension.OS => "O.S. (Off Screen) — Character is in the scene space but not visible in the frame.",
        SpeakerExtension.CONTD => "CONT'D (Continued) — Same character keeps talking after an action or interruption.",
        _ => "No extension — standard on-screen dialogue."
    };

    public static string GetJargonHint(this TransitionPreset preset) => preset switch
    {
        TransitionPreset.CutTo => "CUT TO: — Hard cut to the next shot or scene.",
        TransitionPreset.FadeIn => "FADE IN: — Image fades up from black (often the start of a sequence).",
        TransitionPreset.FadeOut => "FADE OUT. — Image fades to black (often the end of a sequence).",
        TransitionPreset.DissolveTo => "DISSOLVE TO: — One image melts into the next (soft time/place change).",
        TransitionPreset.SmashCutTo => "SMASH CUT TO: — Abrupt, jarring cut for emphasis or shock.",
        TransitionPreset.Blackout => "BLACKOUT — Screen goes black; a hard stop or blackout beat.",
        _ => "Transition — how we leave this moment and enter the next."
    };

    public static string GetJargonHint(this BeatType type) => type switch
    {
        BeatType.Action => "Action (visual) — what the audience sees on screen.",
        BeatType.Sound => "Sound — what the audience hears (room tone, SFX, off-screen audio). Not a character.",
        BeatType.Dialogue => "Dialogue — A character speaks. Name above, optional (parenthetical), then the line.",
        BeatType.Parenthetical => "Parenthetical — Brief direction under a name, e.g. (whispering). Prefer the field on Dialogue.",
        BeatType.Transition => "Transition — How we cut or fade between scenes (CUT TO:, FADE OUT., …).",
        BeatType.Note => "Note — Script note / [[comment]]; usually not spoken or shown on screen.",
        BeatType.Centered => "Centered — Centered title or intertitle text on the page.",
        _ => "Beat — one unit of the scene (action, dialogue, sound, or transition)."
    };

    public static TimeOfDay ParseTimeOfDay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeOfDay.DAY;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("NIGHT")) return TimeOfDay.NIGHT;
        if (upper.Contains("MOMENT")) return TimeOfDay.MOMENTS_LATER;
        if (upper.Contains("CONTINUOUS")) return TimeOfDay.CONTINUOUS;
        if (upper.Contains("DAWN")) return TimeOfDay.DAWN;
        if (upper.Contains("DUSK")) return TimeOfDay.DUSK;
        return TimeOfDay.DAY;
    }

    public static SpeakerExtension ParseSpeakerExtension(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SpeakerExtension.None;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("V.O")) return SpeakerExtension.VO;
        if (upper.Contains("O.S")) return SpeakerExtension.OS;
        if (upper.Contains("CONT")) return SpeakerExtension.CONTD;
        return SpeakerExtension.None;
    }

    public static TransitionPreset ParseTransitionPreset(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TransitionPreset.CutTo;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("FADE IN")) return TransitionPreset.FadeIn;
        if (upper.Contains("FADE OUT")) return TransitionPreset.FadeOut;
        if (upper.Contains("DISSOLVE")) return TransitionPreset.DissolveTo;
        if (upper.Contains("SMASH")) return TransitionPreset.SmashCutTo;
        if (upper.Contains("BLACK")) return TransitionPreset.Blackout;
        return TransitionPreset.CutTo;
    }

    public static string ToCssClass(this ComponentVariant variant) => variant switch
    {
        ComponentVariant.Primary => "btn-primary",
        ComponentVariant.Secondary => "btn-secondary",
        ComponentVariant.Success => "btn-success",
        ComponentVariant.Info => "btn-info",
        ComponentVariant.Warning => "btn-warning",
        ComponentVariant.Danger => "btn-danger",
        ComponentVariant.Dark => "btn-dark",
        ComponentVariant.OutlinePrimary => "btn-outline-primary",
        ComponentVariant.OutlineSecondary => "btn-outline-secondary",
        ComponentVariant.OutlineSuccess => "btn-outline-success",
        ComponentVariant.OutlineInfo => "btn-outline-info",
        ComponentVariant.OutlineDanger => "btn-outline-danger",
        ComponentVariant.OutlineLight => "btn-outline-light",
        _ => "btn-secondary"
    };

    public static string ToBadgeCssClass(this ComponentVariant variant) => variant switch
    {
        ComponentVariant.Primary => "text-bg-primary",
        ComponentVariant.Secondary => "text-bg-secondary",
        ComponentVariant.Success => "text-bg-success",
        ComponentVariant.Info => "text-bg-info",
        ComponentVariant.Warning => "text-bg-warning",
        ComponentVariant.Danger => "text-bg-danger",
        ComponentVariant.Dark => "text-bg-dark",
        _ => "text-bg-secondary"
    };
}

public class ScreenplayMetadata
{
    public string Title { get; set; } = "UNTITLED SCREENPLAY";
    public string Author { get; set; } = "";
    public string Credit { get; set; } = "Written by";
    public string Source { get; set; } = "";
    public string DraftDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string Contact { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class ScreenplayCredits
{
    public string Director { get; set; } = "";
    public string Producer { get; set; } = "";
    public string CastCredits { get; set; } = "";
    public string MusicCredits { get; set; } = "";
    public string SpecialThanks { get; set; } = "";
    public string CopyrightNotice { get; set; } = $"© {DateTime.Today.Year} PageToMovie Studios. All Rights Reserved.";
}

public class ScreenplayBeat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BeatType Type { get; set; } = BeatType.Action;
    public BeatType BeatType { get => Type; set => Type = value; }

    public string Speaker { get; set; } = "";
    public string Extension { get; set; } = "";
    public SpeakerExtension SpeakerExt
    {
        get => EnumExtensions.ParseSpeakerExtension(Extension);
        set => Extension = value.ToDisplayString();
    }
    public string Parenthetical { get; set; } = "";
    public string Text { get; set; } = "";

    public TransitionPreset TransitionPreset
    {
        get => EnumExtensions.ParseTransitionPreset(Text);
        set => Text = value.ToDisplayString();
    }

    public string ActionText { get => Text; set => Text = value; }
    public string SpokenText { get => Text; set => Text = value; }
    public string TransitionText { get => Text; set => Text = value; }

    public ScreenplayBeat Clone()
    {
        return new ScreenplayBeat
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = Type,
            Speaker = Speaker,
            Extension = Extension,
            Parenthetical = Parenthetical,
            Text = Text
        };
    }
}

public class ScreenplayScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SceneNumber { get; set; } = 1;
    public bool HasExplicitSceneNumber { get; set; } = false;
    public string Environment { get; set; } = "INT.";
    public SceneEnvironment Env
    {
        get => EnumExtensions.ParseEnvironment(Environment);
        set => Environment = value.ToHeadingPrefix();
    }
    public string Location { get; set; } = "NEW LOCATION";
    public string TimeOfDay { get; set; } = "DAY";
    public TimeOfDay TimeOfDayEnum
    {
        get => EnumExtensions.ParseTimeOfDay(TimeOfDay);
        set => TimeOfDay = value.ToDisplayString();
    }
    public string SceneTitle { get; set; } = "";
    /// <summary>
    /// Collapsible sequence label in the outline (e.g. location run or user rename).
    /// Consecutive scenes with the same GroupTitle form one group.
    /// </summary>
    public string GroupTitle { get; set; } = "";
    public bool IsGroupCollapsed { get; set; } = false;
    public bool IsCollapsed { get; set; } = false;
    public bool IsSelected { get; set; } = false;

    public List<ScreenplayBeat> Beats { get; set; } = new();

    public string HeaderText => $"{Environment} {Location} - {TimeOfDay}".Trim();

    public ScreenplayScene Clone()
    {
        var copy = new ScreenplayScene
        {
            Id = Guid.NewGuid().ToString("N"),
            SceneNumber = SceneNumber,
            HasExplicitSceneNumber = HasExplicitSceneNumber,
            Environment = Environment,
            Location = Location,
            TimeOfDay = TimeOfDay,
            SceneTitle = SceneTitle,
            GroupTitle = GroupTitle,
            IsGroupCollapsed = IsGroupCollapsed,
            IsCollapsed = IsCollapsed,
            IsSelected = IsSelected
        };
        foreach (var b in Beats)
        {
            copy.Beats.Add(b.Clone());
        }
        return copy;
    }
}

public class ScreenplayLocationProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string VisualLock { get; set; } = "";
}

public class ScreenplayCharacterProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string VoiceProvider { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string VoiceLabel { get; set; } = "";
    public string VoiceProfile { get; set; } = "";
    public bool IsVoiceLocked { get; set; } = true;
    public string VisualLockPrompt { get; set; } = "";
    public string WardrobeAlways { get; set; } = "";
    public bool IsImageLocked { get; set; } = true;
    public VisualMedium VisualMedium { get; set; } = VisualMedium.LiveAction;
    public bool Speaks { get; set; }
    public string? SpeciesKind { get; set; }
    public int ReferenceImageCount { get; set; } = 1;
    /// <summary>True when this row came from Stage‑1 cast classifier / characters API.</summary>
    public bool FromClassifier { get; set; }
}

public class ScreenplayModel
{
    public ScreenplayMetadata Metadata { get; set; } = new();
    public ScreenplayCredits Credits { get; set; } = new();
    public List<ScreenplayScene> Scenes { get; set; } = new();

    public List<ScreenplayLocationProfile> LocationProfiles { get; set; } = new();
    public List<ScreenplayCharacterProfile> CharacterProfiles { get; set; } = new();

    public List<string> GetAllLocations()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in LocationProfiles.Where(loc => !string.IsNullOrWhiteSpace(loc.Name)))
            set.Add(loc.Name.Trim().ToUpperInvariant());
        foreach (var scene in Scenes.Where(scene => !string.IsNullOrWhiteSpace(scene.Location)))
            set.Add(scene.Location.Trim().ToUpperInvariant());
        return set.OrderBy(x => x).ToList();
    }

    public List<string> GetAllCharacters()
    {
        // Cast list = character classifier output (CharacterProfiles seeded from Stage‑1),
        // plus rare manual adds. Never invent names from dialogue cues or ALL CAPS action.
        return CharacterProfiles
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    public ScreenplayLocationProfile GetOrCreateLocationProfile(string name)
    {
        string upper = name.Trim().ToUpperInvariant();
        var existing = LocationProfiles.FirstOrDefault(l => l.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new ScreenplayLocationProfile { Name = upper };
            LocationProfiles.Add(existing);
        }
        return existing;
    }

    public ScreenplayCharacterProfile GetOrCreateCharacterProfile(string name)
    {
        string upper = name.Trim().ToUpperInvariant();
        var existing = CharacterProfiles.FirstOrDefault(c => c.Name.Equals(upper, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new ScreenplayCharacterProfile { Name = upper };
            CharacterProfiles.Add(existing);
        }
        return existing;
    }

    /// <summary>
    /// Auto-group consecutive scenes that share the same location (no extra classifier).
    /// Preserves user renames when the location run is unchanged. Call after parse/import.
    /// </summary>
    public void AutoGroupScenesByLocation(bool force = false)
    {
        if (Scenes.Count == 0) return;
        string? prevLoc = null;
        string? prevGroup = null;
        var seq = 0;
        foreach (var scene in Scenes)
        {
            var loc = (scene.Location ?? "").Trim();
            var locKey = loc.ToUpperInvariant();
            var sameRun = prevLoc is not null && locKey == prevLoc;
            if (!sameRun)
            {
                seq++;
                prevLoc = locKey;
                // Default title = location; fall back to Sequence N
                var defaultTitle = string.IsNullOrWhiteSpace(loc) ? $"Sequence {seq}" : loc;
                if (force || string.IsNullOrWhiteSpace(scene.GroupTitle))
                    scene.GroupTitle = defaultTitle;
                prevGroup = scene.GroupTitle;
            }
            else
            {
                // Continue previous group title (prefer prior scene's title so renames stick)
                if (force || string.IsNullOrWhiteSpace(scene.GroupTitle))
                    scene.GroupTitle = prevGroup ?? (string.IsNullOrWhiteSpace(loc) ? $"Sequence {seq}" : loc);
                else if (!string.IsNullOrWhiteSpace(prevGroup)
                         && !scene.GroupTitle.Equals(prevGroup, StringComparison.OrdinalIgnoreCase))
                {
                    // Keep user's per-scene title if they split manually; still track
                    prevGroup = scene.GroupTitle;
                }
                else
                    prevGroup = scene.GroupTitle;
            }
        }
    }

    /// <summary>Rename a whole consecutive group (matched by old title + adjacency).</summary>
    public void RenameSceneGroup(string oldTitle, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(oldTitle) || string.IsNullOrWhiteSpace(newTitle)) return;
        var nt = newTitle.Trim();
        foreach (var s in Scenes.Where(s => s.GroupTitle.Equals(oldTitle, StringComparison.OrdinalIgnoreCase)))
            s.GroupTitle = nt;
    }

    public void SetGroupCollapsed(string groupTitle, bool collapsed)
    {
        foreach (var s in Scenes.Where(s => s.GroupTitle.Equals(groupTitle, StringComparison.OrdinalIgnoreCase)))
            s.IsGroupCollapsed = collapsed;
    }
}
