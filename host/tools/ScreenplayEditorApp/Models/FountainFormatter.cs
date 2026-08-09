using System.Text;
using PageToMovie.Fountain;

namespace ScreenplayEditorApp.Models;

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

                    if (currentScene != null && string.IsNullOrEmpty(currentScene.SceneTitle))
                    {
                        currentScene.Environment = env;
                        currentScene.Location = location;
                        currentScene.TimeOfDay = timeOfDay;
                        currentScene.SceneTitle = headingText;
                        if (!string.IsNullOrEmpty(element.Meta) && int.TryParse(element.Meta, out int pMeta))
                        {
                            currentScene.SceneNumber = pMeta;
                        }
                    }
                    else
                    {
                        sceneCounter++;
                        int num = sceneCounter;
                        if (!string.IsNullOrEmpty(element.Meta) && int.TryParse(element.Meta, out int parsedMetaNum))
                        {
                            num = parsedMetaNum;
                        }

                        currentScene = new ScreenplayScene
                        {
                            SceneNumber = num,
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
                    GetOrCreateCurrentScene().Beats.Add(new ScreenplayBeat
                    {
                        BeatType = BeatType.Action,
                        ActionText = element.Text
                    });
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
            }
        }

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
        foreach (var scene in model.Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.SceneTitle) || !string.IsNullOrWhiteSpace(scene.Environment) || !string.IsNullOrWhiteSpace(scene.Location))
            {
                string heading = !string.IsNullOrWhiteSpace(scene.SceneTitle)
                    ? scene.SceneTitle
                    : $"{scene.Environment} {scene.Location} - {scene.TimeOfDay}".Trim(' ', '-');

                if (scene.SceneNumber > 0 && !heading.Contains("#"))
                {
                    heading += $" #{scene.SceneNumber}#";
                }

                sb.AppendLine(heading);
                sb.AppendLine();
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
                            if (trans.StartsWith(">"))
                                sb.AppendLine(trans);
                            else
                                sb.AppendLine($"> {trans}");
                            sb.AppendLine();
                        }
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }
}
