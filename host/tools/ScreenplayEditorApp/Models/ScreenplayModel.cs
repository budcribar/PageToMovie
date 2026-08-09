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
    public string Parenthetical { get; set; } = "";
    public string Text { get; set; } = "";

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
    public string Environment { get; set; } = "INT.";
    public string Location { get; set; } = "NEW LOCATION";
    public string TimeOfDay { get; set; } = "DAY";
    public string SceneTitle { get; set; } = "";
    public List<ScreenplayBeat> Beats { get; set; } = new();

    public string HeaderText => $"{Environment} {Location} - {TimeOfDay}".Trim();

    public ScreenplayScene Clone()
    {
        return new ScreenplayScene
        {
            Id = Guid.NewGuid().ToString("N"),
            SceneNumber = SceneNumber,
            Environment = Environment,
            Location = Location,
            TimeOfDay = TimeOfDay,
            SceneTitle = SceneTitle,
            Beats = Beats.Select(b => b.Clone()).ToList()
        };
    }
}

public class ScreenplayDocument
{
    public ScreenplayMetadata Metadata { get; set; } = new();
    public List<ScreenplayScene> Scenes { get; set; } = new();

    public string ToFountain() => FountainFormatter.ToFountain((ScreenplayModel)(object)this);
}

public class ScreenplayModel : ScreenplayDocument
{
}
