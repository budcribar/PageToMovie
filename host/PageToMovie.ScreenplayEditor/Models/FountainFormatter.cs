using System.Text;
using PageToMovie.Fountain;

namespace PageToMovie.ScreenplayEditor.Models;

public static class FountainFormatter
{
    public static ScreenplayModel Parse(string fountainText)
    {
        var model = new ScreenplayModel();
        if (string.IsNullOrWhiteSpace(fountainText))
        {
            return model;
        }

        var parseResult = FountainParser.Parse(fountainText);

        // 1. Title Page Metadata
        foreach (var kvp in parseResult.TitlePage)
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

        // 2. Scenes & Beats from Fountain Elements
        ScreenplayScene? currentScene = null;
        ScreenplayBeat? activeDialogueBeat = null;
        int sceneCounter = 0;

        ScreenplayScene GetOrCreateCurrentScene()
        {
            if (currentScene == null)
            {
                sceneCounter++;
                currentScene = new ScreenplayScene
                {
                    SceneNumber = sceneCounter,
                    Environment = "INT.",
                    Location = "UNSPECIFIED",
                    TimeOfDay = "DAY",
                    SceneTitle = "",
                    Beats = new List<ScreenplayBeat>()
                };
                model.Scenes.Add(currentScene);
            }
            return currentScene;
        }

        foreach (var element in parseResult.Elements)
        {
            switch (element.Type)
            {
                case FountainParser.ElementType.SceneHeading:
                    activeDialogueBeat = null;
                    var headingText = element.Text.Trim();
                    ParseSceneHeadingParts(headingText, out string env, out string location, out string timeOfDay);

                    if (currentScene != null && (string.IsNullOrEmpty(currentScene.SceneTitle) || currentScene.Location == "UNSPECIFIED"))
                    {
                        currentScene.Environment = env;
                        currentScene.Location = location;
                        currentScene.TimeOfDay = timeOfDay;
                        currentScene.SceneTitle = headingText;
                        if (!string.IsNullOrEmpty(element.Meta) && int.TryParse(element.Meta, out int pMeta))
                        {
                            currentScene.SceneNumber = pMeta;
                            currentScene.HasExplicitSceneNumber = true;
                        }
                    }
                    else
                    {
                        sceneCounter++;
                        int num = sceneCounter;
                        bool hasExplicit = false;
                        if (!string.IsNullOrEmpty(element.Meta) && int.TryParse(element.Meta, out int parsedMetaNum))
                        {
                            num = parsedMetaNum;
                            hasExplicit = true;
                        }

                        currentScene = new ScreenplayScene
                        {
                            SceneNumber = num,
                            HasExplicitSceneNumber = hasExplicit,
                            Environment = env,
                            Location = location,
                            TimeOfDay = timeOfDay,
                            SceneTitle = headingText,
                            Beats = new List<ScreenplayBeat>()
                        };
                        model.Scenes.Add(currentScene);
                    }
                    break;

                case FountainParser.ElementType.Action:
                    activeDialogueBeat = null;
                    AppendVisualAndSoundBeats(GetOrCreateCurrentScene().Beats, element.Text ?? "");
                    break;

                case FountainParser.ElementType.Character:
                    activeDialogueBeat = new ScreenplayBeat
                    {
                        BeatType = BeatType.Dialogue,
                        Speaker = element.Text,
                        Extension = element.Meta ?? ""
                    };
                    GetOrCreateCurrentScene().Beats.Add(activeDialogueBeat);
                    break;

                case FountainParser.ElementType.Parenthetical:
                    if (activeDialogueBeat != null)
                    {
                        if (string.IsNullOrEmpty(activeDialogueBeat.Parenthetical))
                            activeDialogueBeat.Parenthetical = element.Text;
                        else
                            activeDialogueBeat.Parenthetical += "\n" + element.Text;
                    }
                    else
                    {
                        activeDialogueBeat = new ScreenplayBeat
                        {
                            BeatType = BeatType.Dialogue,
                            Parenthetical = element.Text
                        };
                        GetOrCreateCurrentScene().Beats.Add(activeDialogueBeat);
                    }
                    break;

                case FountainParser.ElementType.Dialogue:
                    if (activeDialogueBeat != null && string.IsNullOrEmpty(activeDialogueBeat.SpokenText))
                    {
                        activeDialogueBeat.SpokenText = element.Text;
                    }
                    else if (activeDialogueBeat != null)
                    {
                        activeDialogueBeat.SpokenText = (activeDialogueBeat.SpokenText + "\n" + element.Text).Trim();
                    }
                    else
                    {
                        activeDialogueBeat = new ScreenplayBeat
                        {
                            BeatType = BeatType.Dialogue,
                            SpokenText = element.Text
                        };
                        GetOrCreateCurrentScene().Beats.Add(activeDialogueBeat);
                    }
                    break;

                case FountainParser.ElementType.Transition:
                    activeDialogueBeat = null;
                    GetOrCreateCurrentScene().Beats.Add(new ScreenplayBeat
                    {
                        BeatType = BeatType.Transition,
                        TransitionText = element.Text
                    });
                    break;

                case FountainParser.ElementType.Note:
                    activeDialogueBeat = null;
                    {
                        var noteText = element.Text ?? "";
                        if (TryParseSequenceNote(noteText, out var seqTitle))
                        {
                            // Apply to current scene (note appears after heading); don't keep as beat.
                            GetOrCreateCurrentScene().GroupTitle = seqTitle;
                        }
                        else
                        {
                            GetOrCreateCurrentScene().Beats.Add(new ScreenplayBeat
                            {
                                BeatType = BeatType.Note,
                                ActionText = noteText
                            });
                        }
                    }
                    break;

                case FountainParser.ElementType.Centered:
                    activeDialogueBeat = null;
                    GetOrCreateCurrentScene().Beats.Add(new ScreenplayBeat
                    {
                        BeatType = BeatType.Centered,
                        ActionText = element.Text
                    });
                    break;
            }
        }

        // Fill missing sequence groups from consecutive locations (no extra classifier).
        model.AutoGroupScenesByLocation(force: false);
        return model;
    }

    private static void ParseSceneHeadingParts(string headingText, out string env, out string location, out string timeOfDay)
    {
        var normalized = FountainLexer.NormalizeTypographicPunctuation(headingText).Trim();

        if (normalized.StartsWith("INT./EXT.", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("INT/EXT.", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("I/E.", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("INT/EXT", StringComparison.OrdinalIgnoreCase))
        {
            env = "INT./EXT.";
        }
        else if (normalized.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("EXT ", StringComparison.OrdinalIgnoreCase))
        {
            env = "EXT.";
        }
        else
        {
            env = "INT.";
        }

        string rest = normalized;
        int spaceIndex = normalized.IndexOf(' ');
        if (spaceIndex > 0)
        {
            rest = normalized.Substring(spaceIndex).Trim();
        }
        else if (normalized.IndexOf('.') > 0)
        {
            rest = normalized.Substring(normalized.IndexOf('.') + 1).Trim();
        }

        int dashIdx = rest.LastIndexOf('-');
        if (dashIdx >= 0)
        {
            location = rest.Substring(0, dashIdx).Trim();
            timeOfDay = rest.Substring(dashIdx + 1).Trim();
        }
        else
        {
            location = rest;
            timeOfDay = "DAY";
        }
    }

    public static string ToFountain(this ScreenplayModel model)
    {
        var sb = new StringBuilder();

        void AppendMeta(string key, string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var lines = val.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1)
            {
                sb.AppendLine($"{key}: {lines[0].Trim()}");
            }
            else
            {
                sb.AppendLine($"{key}:");
                foreach (var l in lines)
                {
                    sb.AppendLine($"\t{l.Trim()}");
                }
            }
        }

        // 1. Title Page Metadata
        AppendMeta("Title", model.Metadata.Title);
        AppendMeta("Credit", model.Metadata.Credit);
        AppendMeta("Author", model.Metadata.Author);
        AppendMeta("Source", model.Metadata.Source);
        AppendMeta("Draft date", model.Metadata.DraftDate);
        AppendMeta("Contact", model.Metadata.Contact);
        AppendMeta("Notes", model.Metadata.Notes);

        if (sb.Length > 0)
            sb.AppendLine();

        // 2. Scenes
        string? lastGroupWritten = null;
        foreach (var scene in model.Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.SceneTitle) || !string.IsNullOrWhiteSpace(scene.Environment) || !string.IsNullOrWhiteSpace(scene.Location))
            {
                string heading;
                if (!string.IsNullOrWhiteSpace(scene.SceneTitle))
                {
                    heading = scene.SceneTitle;
                    if (!heading.StartsWith("INT.", StringComparison.OrdinalIgnoreCase) &&
                        !heading.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase) &&
                        !heading.StartsWith("INT./EXT.", StringComparison.OrdinalIgnoreCase) &&
                        !heading.StartsWith("INT/EXT.", StringComparison.OrdinalIgnoreCase) &&
                        !heading.StartsWith("I/E.", StringComparison.OrdinalIgnoreCase) &&
                        !heading.StartsWith("."))
                    {
                        heading = "." + heading;
                    }
                }
                else
                {
                    heading = $"{scene.Environment} {scene.Location} - {scene.TimeOfDay}".Trim(' ', '-');
                }

                if (scene.HasExplicitSceneNumber && scene.SceneNumber > 0 && !heading.Contains("#"))
                {
                    heading += $" #{scene.SceneNumber}#";
                }

                sb.AppendLine(heading);
                sb.AppendLine();
            }

            // Persist outline groups without a second classifier — first scene of each group.
            if (!string.IsNullOrWhiteSpace(scene.GroupTitle)
                && !scene.GroupTitle.Equals(lastGroupWritten, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"[[SEQUENCE: {scene.GroupTitle.Trim()}]]");
                sb.AppendLine();
                lastGroupWritten = scene.GroupTitle;
            }

            foreach (var beat in scene.Beats)
            {
                switch (beat.BeatType)
                {
                    case BeatType.Action:
                        if (!string.IsNullOrWhiteSpace(beat.ActionText))
                        {
                            sb.AppendLine(beat.ActionText);
                            sb.AppendLine();
                        }
                        break;

                    case BeatType.Sound:
                        if (!string.IsNullOrWhiteSpace(beat.ActionText))
                        {
                            // Canonical fountain form for audio-only cues (not a character cue).
                            var body = beat.ActionText.Trim().TrimStart('(').TrimEnd(')');
                            if (body.StartsWith("SOUND:", StringComparison.OrdinalIgnoreCase))
                                body = body[6..].Trim();
                            sb.AppendLine($"(SOUND: {body})");
                            sb.AppendLine();
                        }
                        break;

                    case BeatType.Dialogue:
                        if (!string.IsNullOrWhiteSpace(beat.Speaker))
                        {
                            string charLine = beat.Speaker.ToUpperInvariant();
                            if (!string.IsNullOrWhiteSpace(beat.Extension))
                            {
                                var ext = beat.Extension.Trim('(', ')');
                                charLine += $" ({ext})";
                            }
                            sb.AppendLine(charLine);
                        }

                        if (!string.IsNullOrWhiteSpace(beat.Parenthetical))
                        {
                            var parenLines = beat.Parenthetical.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parenLines)
                            {
                                var paren = p.Trim();
                                if (!paren.StartsWith("(")) paren = "(" + paren;
                                if (!paren.EndsWith(")")) paren = paren + ")";
                                sb.AppendLine(paren);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(beat.SpokenText))
                        {
                            sb.AppendLine(beat.SpokenText);
                        }
                        sb.AppendLine();
                        break;

                    case BeatType.Transition:
                        if (!string.IsNullOrWhiteSpace(beat.TransitionText))
                        {
                            var trans = beat.TransitionText.Trim();
                            if (trans.StartsWith(">") || trans.EndsWith("TO:", StringComparison.OrdinalIgnoreCase) || trans.EndsWith("OUT.", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine(trans);
                            else
                                sb.AppendLine($"> {trans}");
                            sb.AppendLine();
                        }
                        break;

                    case BeatType.Note:
                        if (!string.IsNullOrWhiteSpace(beat.ActionText))
                        {
                            sb.AppendLine($"[[{beat.ActionText.Trim('[', ']')}]]");
                            sb.AppendLine();
                        }
                        break;

                    case BeatType.Centered:
                        if (!string.IsNullOrWhiteSpace(beat.ActionText))
                        {
                            sb.AppendLine($"> {beat.ActionText.Trim('>', '<', ' ')} <");
                            sb.AppendLine();
                        }
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd() + "\n";
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
        var embedded = System.Text.RegularExpressions.Regex.Matches(
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
        visual = System.Text.RegularExpressions.Regex.Replace(visual, @"\s{2,}", " ").Trim();
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

    /// <summary>Outline sequence marker: [[SEQUENCE: Title]] or [[GROUP: Title]].</summary>
    public static bool TryParseSequenceNote(string? text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('[').TrimEnd(']').Trim();
        if (t.StartsWith("SEQUENCE:", StringComparison.OrdinalIgnoreCase))
        {
            title = t[9..].Trim();
            return title.Length > 0;
        }
        if (t.StartsWith("GROUP:", StringComparison.OrdinalIgnoreCase))
        {
            title = t[6..].Trim();
            return title.Length > 0;
        }
        return false;
    }
}
