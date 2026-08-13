using System.Text.Json.Serialization;

namespace PageToMovie.Adaptation.Contracts;

/// <summary>Hierarchical beat sheet (max master). Trim later by sequence, not by re-adapting.</summary>
public sealed class ScreenplayIndex
{
    public const string CurrentSchema = "screenplay.index.v1";

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = CurrentSchema;

    [JsonPropertyName("movie_title")]
    public string MovieTitle { get; set; } = "";

    [JsonPropertyName("source_book_title")]
    public string SourceBookTitle { get; set; } = "";

    [JsonPropertyName("acts")]
    public List<ScreenplayIndexAct> Acts { get; set; } = new();

    [JsonIgnore]
    public List<string> Warnings { get; set; } = new();
}

public sealed class ScreenplayIndexAct
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("sequences")]
    public List<ScreenplayIndexSequence> Sequences { get; set; } = new();
}

public sealed class ScreenplayIndexSequence
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("scenes")]
    public List<ScreenplayIndexCard> Scenes { get; set; } = new();
}

public sealed class ScreenplayIndexCard
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("heading")]
    public string Heading { get; set; } = "";

    [JsonPropertyName("location_key")]
    public string LocationKey { get; set; } = "";

    [JsonPropertyName("speaking_cast")]
    public List<string> SpeakingCast { get; set; } = new();

    [JsonPropertyName("beat")]
    public string Beat { get; set; } = "";

    [JsonPropertyName("book_anchor_start")]
    public string BookAnchorStart { get; set; } = "";

    [JsonPropertyName("book_anchor_end")]
    public string BookAnchorEnd { get; set; } = "";

    [JsonPropertyName("approx_minutes")]
    public double? ApproxMinutes { get; set; }
}

public sealed class ScreenplayIndexGate
{
    public bool Ok { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class ScreenplayIndexRollup
{
    public int Acts { get; init; }
    public int Sequences { get; init; }
    public int SceneCards { get; init; }
    public int Locations { get; init; }
    public int SpeakingCast { get; init; }
    public double ApproxMinutes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
