using System.ComponentModel;

namespace ScreenplayEditorApp.Models;

public enum BeatType
{
    Action,
    Dialogue,
    Parenthetical,
    Transition,
    Note,
    Centered
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

    public static string ToDisplayString(this SpeakerExtension ext) => ext switch
    {
        SpeakerExtension.VO => "V.O.",
        SpeakerExtension.OS => "O.S.",
        SpeakerExtension.CONTD => "CONT'D",
        _ => ""
    };

    public static string GetJargonHint(this SpeakerExtension ext) => ext switch
    {
        SpeakerExtension.VO => "V.O. (Voice Over) - Character speaking off-camera or internal monologue",
        SpeakerExtension.OS => "O.S. (Off Screen) - Character physically present in room but not in camera frame",
        SpeakerExtension.CONTD => "CONT'D (Continued) - Character continuing dialogue after an action break",
        _ => "Standard dialogue extension"
    };

    public static SpeakerExtension ParseSpeakerExtension(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SpeakerExtension.None;
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("V.O")) return SpeakerExtension.VO;
        if (upper.Contains("O.S")) return SpeakerExtension.OS;
        if (upper.Contains("CONT")) return SpeakerExtension.CONTD;
        return SpeakerExtension.None;
    }

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

public class ScreenplayCharacter
{
    public string Name { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string VisualDescription { get; set; } = "";
    public string WardrobeAlways { get; set; } = "";
    public int ReferenceImageCount { get; set; } = 1;
}

public class ScreenplayModel
{
    public ScreenplayMetadata Metadata { get; set; } = new();
    public List<ScreenplayScene> Scenes { get; set; } = new();

    public List<string> MasterLocations { get; set; } = new();
    public List<string> MasterCharacters { get; set; } = new();

    public List<ScreenplayCharacter> CharacterProfiles { get; set; } = new();

    public List<string> GetAllLocations()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in MasterLocations)
        {
            if (!string.IsNullOrWhiteSpace(loc)) set.Add(loc.Trim().ToUpperInvariant());
        }
        foreach (var scene in Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.Location)) set.Add(scene.Location.Trim().ToUpperInvariant());
        }
        return set.OrderBy(x => x).ToList();
    }

    public List<string> GetAllCharacters()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chara in MasterCharacters)
        {
            if (!string.IsNullOrWhiteSpace(chara)) set.Add(chara.Trim().ToUpperInvariant());
        }
        foreach (var scene in Scenes)
        {
            foreach (var beat in scene.Beats)
            {
                if (beat.BeatType == BeatType.Dialogue && !string.IsNullOrWhiteSpace(beat.Speaker))
                {
                    set.Add(beat.Speaker.Trim().ToUpperInvariant());
                }
            }
        }
        return set.OrderBy(x => x).ToList();
    }
}
