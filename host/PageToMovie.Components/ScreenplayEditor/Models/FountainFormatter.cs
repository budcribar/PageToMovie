using System.Text;
using PageToMovie.Fountain;

using PageToMovie.Core.Utils;
namespace PageToMovie.ScreenplayEditor.Models;

public static class FountainFormatter
{
    private const string UnspecifiedLocation = "UNSPECIFIED";

    private static readonly string[] EnvPrefixes =
    {
        "INT./EXT.", "INT./EXT", "EXT./INT.", "EXT./INT",
        "INT/EXT.", "INT/EXT", "EXT/INT.", "EXT/INT",
        "I/E.", "I/E",
        "EXT. AND INT.", "EXT. AND INT", "EXT AND INT.", "EXT AND INT",
        "INT. AND EXT.", "INT. AND EXT", "INT AND EXT.", "INT AND EXT",
        "EXT.", "INT.", "EST.", "EXT ", "INT ", "EST ",
    };

    public static ScreenplayModel Parse(string fountainText)
    {
        var model = new ScreenplayModel();
        if (string.IsNullOrWhiteSpace(fountainText))
        {
            return model;
        }

        var parseResult = FountainParser.Parse(fountainText);
        ApplyTitlePageMetadata(model, parseResult.TitlePage);

        var ctx = new ParseContext(model);
        foreach (var element in parseResult.Elements)
        {
            switch (element.Type)
            {
                case FountainParser.ElementType.SceneHeading:
                    HandleSceneHeading(ctx, element);
                    break;
                case FountainParser.ElementType.Action:
                    HandleAction(ctx, element);
                    break;
                case FountainParser.ElementType.Character:
                    HandleCharacter(ctx, element);
                    break;
                case FountainParser.ElementType.Parenthetical:
                    HandleParenthetical(ctx, element);
                    break;
                case FountainParser.ElementType.Dialogue:
                    HandleDialogue(ctx, element);
                    break;
                case FountainParser.ElementType.Transition:
                    HandleTransition(ctx, element);
                    break;
                case FountainParser.ElementType.Note:
                    HandleNote(ctx, element);
                    break;
                case FountainParser.ElementType.Centered:
                    HandleCentered(ctx, element);
                    break;
            }
        }

        return model;
    }

    private static void ApplyTitlePageMetadata(ScreenplayModel model, Dictionary<string, string> titlePage)
    {
        foreach (var kvp in titlePage)
        {
            var key = kvp.Key.Trim().ToLowerInvariant();
            var val = kvp.Value.Trim();
            switch (key)
            {
                case "title":
                    model.Metadata.Title = val;
                    break;
                case "author":
                case "authors":
                    model.Metadata.Author = val;
                    break;
                case "credit":
                    model.Metadata.Credit = val;
                    break;
                case "source":
                    model.Metadata.Source = val;
                    break;
                case "draft date":
                case "draft":
                case "date":
                    model.Metadata.DraftDate = val;
                    break;
                case "contact":
                    model.Metadata.Contact = val;
                    break;
                case "notes":
                    model.Metadata.Notes = val;
                    break;
            }
        }
    }

    private static ScreenplayScene GetOrCreateCurrentScene(ParseContext ctx)
    {
        if (ctx.CurrentScene != null)
            return ctx.CurrentScene;

        ctx.SceneCounter++;
        ctx.CurrentScene = new ScreenplayScene
        {
            SceneNumber = ctx.SceneCounter,
            Environment = "INT.",
            Location = UnspecifiedLocation,
            TimeOfDay = "DAY",
            SceneTitle = "",
            Beats = new List<ScreenplayBeat>()
        };
        ctx.Model.Scenes.Add(ctx.CurrentScene);
        return ctx.CurrentScene;
    }

    private static void HandleSceneHeading(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = null;
        var headingText = element.Text.Trim();
        ParseSceneHeadingParts(headingText, out string env, out string location, out string timeOfDay);

        if (ShouldReuseCurrentScene(ctx.CurrentScene))
            ApplyHeadingToExistingScene(ctx.CurrentScene!, headingText, env, location, timeOfDay, element.Meta);
        else
            ctx.CurrentScene = CreateSceneFromHeading(ctx, headingText, env, location, timeOfDay, element.Meta);
    }

    private static bool ShouldReuseCurrentScene(ScreenplayScene? currentScene)
    {
        return currentScene != null
            && (string.IsNullOrEmpty(currentScene.SceneTitle) || currentScene.Location == UnspecifiedLocation);
    }

    private static void ApplyHeadingToExistingScene(
        ScreenplayScene scene, string headingText, string env, string location, string timeOfDay, string? meta)
    {
        scene.Environment = env;
        scene.Location = location;
        scene.TimeOfDay = timeOfDay;
        scene.SceneTitle = headingText;
        if (TryParseExplicitSceneNumber(meta, out int pMeta))
        {
            scene.SceneNumber = pMeta;
            scene.HasExplicitSceneNumber = true;
        }
    }

    private static ScreenplayScene CreateSceneFromHeading(
        ParseContext ctx, string headingText, string env, string location, string timeOfDay, string? meta)
    {
        ctx.SceneCounter++;
        int num = ctx.SceneCounter;
        bool hasExplicit = false;
        if (TryParseExplicitSceneNumber(meta, out int parsedMetaNum))
        {
            num = parsedMetaNum;
            hasExplicit = true;
        }

        var currentScene = new ScreenplayScene
        {
            SceneNumber = num,
            HasExplicitSceneNumber = hasExplicit,
            Environment = env,
            Location = location,
            TimeOfDay = timeOfDay,
            SceneTitle = headingText,
            Beats = new List<ScreenplayBeat>()
        };
        ctx.Model.Scenes.Add(currentScene);
        return currentScene;
    }

    private static bool TryParseExplicitSceneNumber(string? meta, out int number)
    {
        number = 0;
        return !string.IsNullOrEmpty(meta) && int.TryParse(meta, out number);
    }

    private static void HandleAction(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = null;
        AppendVisualAndSoundBeats(GetOrCreateCurrentScene(ctx).Beats, element.Text ?? "");
    }

    private static void HandleCharacter(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            Speaker = element.Text,
            Extension = element.Meta ?? ""
        };
        GetOrCreateCurrentScene(ctx).Beats.Add(ctx.ActiveDialogueBeat);
    }

    private static void HandleParenthetical(ParseContext ctx, FountainParser.Element element)
    {
        if (ctx.ActiveDialogueBeat != null)
        {
            if (string.IsNullOrEmpty(ctx.ActiveDialogueBeat.Parenthetical))
                ctx.ActiveDialogueBeat.Parenthetical = element.Text;
            else
                ctx.ActiveDialogueBeat.Parenthetical = string.Concat(ctx.ActiveDialogueBeat.Parenthetical, "\n", element.Text);
            return;
        }

        ctx.ActiveDialogueBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            Parenthetical = element.Text
        };
        GetOrCreateCurrentScene(ctx).Beats.Add(ctx.ActiveDialogueBeat);
    }

    private static void HandleDialogue(ParseContext ctx, FountainParser.Element element)
    {
        if (ctx.ActiveDialogueBeat != null && string.IsNullOrEmpty(ctx.ActiveDialogueBeat.SpokenText))
        {
            ctx.ActiveDialogueBeat.SpokenText = element.Text;
            return;
        }

        if (ctx.ActiveDialogueBeat != null)
        {
            ctx.ActiveDialogueBeat.SpokenText = (ctx.ActiveDialogueBeat.SpokenText + "\n" + element.Text).Trim();
            return;
        }

        ctx.ActiveDialogueBeat = new ScreenplayBeat
        {
            BeatType = BeatType.Dialogue,
            SpokenText = element.Text
        };
        GetOrCreateCurrentScene(ctx).Beats.Add(ctx.ActiveDialogueBeat);
    }

    private static void HandleTransition(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = null;
        GetOrCreateCurrentScene(ctx).Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Transition,
            TransitionText = element.Text
        });
    }

    private static void HandleNote(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = null;
        GetOrCreateCurrentScene(ctx).Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Note,
            ActionText = element.Text
        });
    }

    private static void HandleCentered(ParseContext ctx, FountainParser.Element element)
    {
        ctx.ActiveDialogueBeat = null;
        GetOrCreateCurrentScene(ctx).Beats.Add(new ScreenplayBeat
        {
            BeatType = BeatType.Centered,
            ActionText = element.Text
        });
    }

    private static void ParseSceneHeadingParts(string headingText, out string env, out string location, out string timeOfDay)
    {
        var normalized = FountainLexer.NormalizeTypographicPunctuation(headingText).Trim();
        var u = normalized.ToUpperInvariant();

        env = DetectEnvironment(u);
        var rest = StripEnvPrefixes(normalized);
        rest = StripLeftoverAndIntExt(rest);
        SplitLocationAndTime(rest, out location, out timeOfDay);
        location = SanitizeLocation(location);
        if (string.IsNullOrWhiteSpace(timeOfDay))
            timeOfDay = "DAY";
    }

    private static string DetectEnvironment(string u)
    {
        if (IsCompoundIntExt(u))
            return "INT./EXT.";
        if (IsExterior(u))
            return "EXT.";
        return "INT.";
    }

    private static bool IsCompoundIntExt(string u)
    {
        return u.StartsWith("INT./EXT", StringComparison.Ordinal)
            || u.StartsWith("INT/EXT", StringComparison.Ordinal)
            || u.StartsWith("EXT./INT", StringComparison.Ordinal)
            || u.StartsWith("EXT/INT", StringComparison.Ordinal)
            || u.StartsWith("I/E", StringComparison.Ordinal)
            || u.StartsWith("EXT. AND INT", StringComparison.Ordinal)
            || u.StartsWith("EXT AND INT", StringComparison.Ordinal)
            || u.StartsWith("INT. AND EXT", StringComparison.Ordinal)
            || u.StartsWith("INT AND EXT", StringComparison.Ordinal);
    }

    private static bool IsExterior(string u)
    {
        return u.StartsWith("EXT.", StringComparison.Ordinal)
            || u.StartsWith("EXT ", StringComparison.Ordinal)
            || u.StartsWith("EST.", StringComparison.Ordinal);
    }

    private static string StripEnvPrefixes(string rest)
    {
        var matchedPrefix = EnvPrefixes.FirstOrDefault(p => rest.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (matchedPrefix is not null)
            rest = rest[matchedPrefix.Length..].Trim();
        return rest;
    }

    private static string StripLeftoverAndIntExt(string rest)
    {
        if (!StartsWithAndIntOrExt(rest))
            return rest;

        var sp = rest.IndexOf(' ');
        if (sp <= 0)
            return rest;

        var next = rest.IndexOf(' ', sp + 1);
        if (next > 0)
            rest = rest[(next + 1)..].Trim();
        else
            rest = rest[(sp + 1)..].Trim();

        return StripLeadingIntOrExtToken(rest);
    }

    private static bool StartsWithAndIntOrExt(string rest)
    {
        return rest.StartsWith("AND INT.", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("AND INT ", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("AND EXT.", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("AND EXT ", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripLeadingIntOrExtToken(string rest)
    {
        if (!rest.StartsWith("INT.", StringComparison.OrdinalIgnoreCase)
            && !rest.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase))
        {
            return rest;
        }

        var d = rest.IndexOf(' ');
        return d > 0 ? rest[(d + 1)..].Trim() : "";
    }

    private static void SplitLocationAndTime(string rest, out string location, out string timeOfDay)
    {
        int dashIdx = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx < 0)
            dashIdx = rest.LastIndexOf(" – ", StringComparison.Ordinal);

        if (dashIdx >= 0)
        {
            location = rest[..dashIdx].Trim();
            timeOfDay = rest[(dashIdx + 3)..].Trim();
            return;
        }

        var singleDash = rest.LastIndexOf('-');
        if (singleDash > 0)
        {
            location = rest[..singleDash].Trim();
            timeOfDay = rest[(singleDash + 1)..].Trim();
            return;
        }

        location = rest;
        timeOfDay = "DAY";
    }

    private static string SanitizeLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            location = UnspecifiedLocation;

        if (location.StartsWith("AND ", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("INT.", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase))
        {
            location = CommonRegex.Replace(
                location,
                @"^(AND\s+)?(INT\.?|EXT\.?)\s+",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(location))
                location = UnspecifiedLocation;
        }

        return location;
    }

    public static string ToFountain(this ScreenplayModel model)
    {
        var sb = new StringBuilder();

        AppendTitlePageMetadata(sb, model.Metadata);

        if (sb.Length > 0)
            sb.AppendLine();

        foreach (var scene in model.Scenes)
        {
            AppendSceneHeading(sb, scene);
            foreach (var beat in scene.Beats)
                AppendBeat(sb, beat);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendTitlePageMetadata(StringBuilder sb, ScreenplayMetadata metadata)
    {
        AppendMeta(sb, "Title", metadata.Title);
        AppendMeta(sb, "Credit", metadata.Credit);
        AppendMeta(sb, "Author", metadata.Author);
        AppendMeta(sb, "Source", metadata.Source);
        AppendMeta(sb, "Draft date", metadata.DraftDate);
        AppendMeta(sb, "Contact", metadata.Contact);
        AppendMeta(sb, "Notes", metadata.Notes);
    }

    private static void AppendMeta(StringBuilder sb, string key, string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return;
        var lines = val.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 1)
        {
            sb.AppendLine($"{key}: {lines[0].Trim()}");
            return;
        }

        sb.AppendLine($"{key}:");
        foreach (var l in lines)
        {
            sb.AppendLine($"\t{l.Trim()}");
        }
    }

    private static void AppendSceneHeading(StringBuilder sb, ScreenplayScene scene)
    {
        if (!HasSceneHeading(scene))
            return;

        sb.AppendLine(FormatSceneHeading(scene));
        sb.AppendLine();
    }

    private static bool HasSceneHeading(ScreenplayScene scene)
    {
        return !string.IsNullOrWhiteSpace(scene.SceneTitle)
            || !string.IsNullOrWhiteSpace(scene.Environment)
            || !string.IsNullOrWhiteSpace(scene.Location);
    }

    private static string FormatSceneHeading(ScreenplayScene scene)
    {
        string heading;
        if (!string.IsNullOrWhiteSpace(scene.SceneTitle))
        {
            heading = scene.SceneTitle;
            if (NeedsForcedScenePrefix(heading))
                heading = "." + heading;
        }
        else
        {
            heading = $"{scene.Environment} {scene.Location} - {scene.TimeOfDay}".Trim(' ', '-');
        }

        if (scene.HasExplicitSceneNumber && scene.SceneNumber > 0 && !heading.Contains("#"))
            heading += $" #{scene.SceneNumber}#";

        return heading;
    }

    private static bool NeedsForcedScenePrefix(string heading)
    {
        return !heading.StartsWith("INT.", StringComparison.OrdinalIgnoreCase)
            && !heading.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase)
            && !heading.StartsWith("INT./EXT.", StringComparison.OrdinalIgnoreCase)
            && !heading.StartsWith("INT/EXT.", StringComparison.OrdinalIgnoreCase)
            && !heading.StartsWith("I/E.", StringComparison.OrdinalIgnoreCase)
            && !heading.StartsWith('.');
    }

    private static void AppendBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        switch (beat.BeatType)
        {
            case BeatType.Action:
                AppendActionBeat(sb, beat);
                break;
            case BeatType.Sound:
                AppendSoundBeat(sb, beat);
                break;
            case BeatType.Dialogue:
                AppendDialogueBeat(sb, beat);
                break;
            case BeatType.Transition:
                AppendTransitionBeat(sb, beat);
                break;
            case BeatType.Note:
                AppendNoteBeat(sb, beat);
                break;
            case BeatType.Centered:
                AppendCenteredBeat(sb, beat);
                break;
        }
    }

    private static void AppendActionBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (string.IsNullOrWhiteSpace(beat.ActionText))
            return;
        sb.AppendLine(beat.ActionText);
        sb.AppendLine();
    }

    private static void AppendSoundBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (string.IsNullOrWhiteSpace(beat.ActionText))
            return;

        var body = beat.ActionText.Trim().TrimStart('(').TrimEnd(')');
        if (body.StartsWith("SOUND:", StringComparison.OrdinalIgnoreCase))
            body = body[6..].Trim();
        sb.AppendLine($"(SOUND: {body})");
        sb.AppendLine();
    }

    private static void AppendDialogueBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (!string.IsNullOrWhiteSpace(beat.Speaker))
            AppendCharacterCue(sb, beat);

        if (!string.IsNullOrWhiteSpace(beat.Parenthetical))
            AppendParentheticalLines(sb, beat.Parenthetical);

        if (!string.IsNullOrWhiteSpace(beat.SpokenText))
            sb.AppendLine(beat.SpokenText);

        sb.AppendLine();
    }

    private static void AppendCharacterCue(StringBuilder sb, ScreenplayBeat beat)
    {
        string charLine = beat.Speaker.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(beat.Extension))
        {
            var ext = beat.Extension.Trim('(', ')');
            charLine += $" ({ext})";
        }
        sb.AppendLine(charLine);
    }

    private static void AppendParentheticalLines(StringBuilder sb, string parenthetical)
    {
        var parenLines = parenthetical.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parenLines)
        {
            var paren = p.Trim();
            if (!paren.StartsWith('(')) paren = "(" + paren;
            if (!paren.EndsWith(')')) paren = paren + ")";
            sb.AppendLine(paren);
        }
    }

    private static void AppendTransitionBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (string.IsNullOrWhiteSpace(beat.TransitionText))
            return;

        var trans = beat.TransitionText.Trim();
        if (trans.StartsWith('>') || trans.EndsWith("TO:", StringComparison.OrdinalIgnoreCase) || trans.EndsWith("OUT.", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine(trans);
        else
            sb.AppendLine($"> {trans}");
        sb.AppendLine();
    }

    private static void AppendNoteBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (string.IsNullOrWhiteSpace(beat.ActionText))
            return;
        sb.AppendLine($"[[{beat.ActionText.Trim('[', ']')}]]");
        sb.AppendLine();
    }

    private static void AppendCenteredBeat(StringBuilder sb, ScreenplayBeat beat)
    {
        if (string.IsNullOrWhiteSpace(beat.ActionText))
            return;
        sb.AppendLine($"> {beat.ActionText.Trim('>', '<', ' ')} <");
        sb.AppendLine();
    }

    /// <summary>
    /// Split a fountain action element into separate Visual and Sound beats.
    /// Pure sound lines become Sound only; mixed lines like
    /// "BUSTER enters. (SOUND: door slam)" become Visual + Sound.
    /// </summary>
    public static void AppendVisualAndSoundBeats(List<ScreenplayBeat> beats, string? text)
    {
        if (beats is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            beats.Add(new ScreenplayBeat { BeatType = BeatType.Action, ActionText = "" });
            return;
        }

        var raw = text.Trim();
        if (TryParseSoundAction(raw, out var pureSound))
        {
            beats.Add(new ScreenplayBeat { BeatType = BeatType.Sound, ActionText = pureSound });
            return;
        }

        // Embedded cues: (SOUND: …) / (SFX: …) anywhere in the line
        var embedded = CommonRegex.Matches(
            raw,
            @"\(\s*(?:SOUND|SOUNDS|SFX)\s*:\s*([^)]+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (embedded.Count == 0)
        {
            beats.Add(new ScreenplayBeat { BeatType = BeatType.Action, ActionText = raw });
            return;
        }

        var visual = raw;
        var sounds = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in embedded)
        {
            var body = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
                sounds.Add(body);
            visual = visual.Replace(m.Value, " ");
        }
        visual = CommonRegex.Replace(visual, @"\s{2,}", " ").Trim();
        visual = visual.TrimEnd(' ', ',', ';', '-');

        if (!string.IsNullOrWhiteSpace(visual))
            beats.Add(new ScreenplayBeat { BeatType = BeatType.Action, ActionText = visual });
        foreach (var s in sounds)
            beats.Add(new ScreenplayBeat { BeatType = BeatType.Sound, ActionText = s });
    }

    /// <summary>
    /// True when the action line is an audio-only cue, e.g. "(SOUND: applause)" or "SOUND: rain".
    /// Returns the sound description without the SOUND: prefix / wrapping parens.
    /// </summary>
    public static bool TryParseSoundAction(string? text, out string body)
    {
        body = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // Strip outer parens: (SOUND: …)
        if (t.StartsWith('(') && t.EndsWith(')') && t.Length > 2)
            t = t[1..^1].Trim();

        if (t.StartsWith("SOUND:", StringComparison.OrdinalIgnoreCase))
        {
            body = t[6..].Trim();
            return true;
        }
        if (t.StartsWith("SOUNDS:", StringComparison.OrdinalIgnoreCase))
        {
            body = t[7..].Trim();
            return true;
        }
        if (t.StartsWith("SFX:", StringComparison.OrdinalIgnoreCase))
        {
            body = t[4..].Trim();
            return true;
        }
        return false;
    }

    private sealed class ParseContext
    {
        public ParseContext(ScreenplayModel model) => Model = model;

        public ScreenplayModel Model { get; }
        public ScreenplayScene? CurrentScene;
        public ScreenplayBeat? ActiveDialogueBeat;
        public int SceneCounter;
    }
}
