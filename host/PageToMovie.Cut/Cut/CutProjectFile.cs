using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageToMovie.Cut.Cut;

/// <summary>Finish file <c>cut.project.json</c> — trims, range-deletes, joins, cards, text clips, music name.</summary>
public static class CutProjectFile
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(
        IReadOnlyList<CutClip> clips,
        string? musicFileName,
        IReadOnlyList<CutTextClip>? textClips = null)
    {
        var dto = new ProjectDto
        {
            SchemaVersion = Version,
            MusicFileName = string.IsNullOrWhiteSpace(musicFileName) ? null : musicFileName,
            Clips = clips.Select(ToDto).ToList(),
            TextClips = textClips is { Count: > 0 } ? textClips.Select(ToTextDto).ToList() : null,
        };
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    public static bool TryApply(IReadOnlyList<CutClip> clips, string? json, out string? musicFileName) =>
        TryApply(clips, json, out musicFileName, out _);

    public static bool TryApply(
        IReadOnlyList<CutClip> clips,
        string? json,
        out string? musicFileName,
        out List<CutTextClip> textClips)
    {
        musicFileName = null;
        textClips = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;
        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto?.Clips is null)
            return false;
        musicFileName = dto.MusicFileName;
        foreach (var row in dto.Clips)
        {
            var clip = clips.FirstOrDefault(c => c.Scene == row.Scene && c.Clip == row.Clip);
            if (clip is null)
                continue;
            ApplyRow(clip, row);
        }

        foreach (var row in dto.TextClips ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Text) && string.IsNullOrWhiteSpace(row.Id))
                continue;
            textClips.Add(new CutTextClip
            {
                Id = string.IsNullOrWhiteSpace(row.Id) ? CutTextClip.NewId() : row.Id.Trim(),
                Text = row.Text ?? "",
                StartSec = Math.Max(0, row.Start),
                Seconds = CutCard.ResolveHold(row.Seconds > 0 ? row.Seconds : CutCard.DefaultHoldSeconds),
            });
        }

        return true;
    }

    private static void ApplyRow(CutClip clip, ClipDto row)
    {
        if (row.MarkOut > 0 || row.MarkIn > 0)
            clip.ApplyInOut(row.MarkIn, row.MarkOut);
        clip.RangeDeletes.Clear();
        foreach (var del in row.RangeDeletes ?? [])
            CutRangeDelete.TryAdd(clip.RangeDeletes, del.Start, del.End, clip.MarkIn, clip.MarkOut, out _);
        clip.JoinOverride = ParseJoinOverride(row.JoinOut);
        if (!string.IsNullOrWhiteSpace(row.FountainTransition))
            clip.FountainTransition = row.FountainTransition;
        if (row.Card is not null)
        {
            clip.Card.Enabled = row.Card.Enabled;
            clip.Card.Text = row.Card.Text ?? "";
            if (row.Card.Seconds > 0)
                clip.Card.Seconds = row.Card.Seconds;
        }
    }

    private static ClipDto ToDto(CutClip clip) => new()
    {
        Scene = clip.Scene,
        Clip = clip.Clip,
        MarkIn = clip.MarkIn,
        MarkOut = clip.MarkOut,
        RangeDeletes = clip.RangeDeletes.Select(r => new SpanDto { Start = r.Start, End = r.End }).ToList(),
        JoinOut = clip.JoinOverride is { } j ? CutTransitionMap.WireName(j) : null,
        FountainTransition = clip.FountainTransition,
        Card = clip.Card.Enabled || !string.IsNullOrWhiteSpace(clip.Card.Text)
            ? new CardDto { Enabled = clip.Card.Enabled, Text = clip.Card.Text, Seconds = clip.Card.HoldSeconds }
            : null,
    };

    private static TextClipDto ToTextDto(CutTextClip title) => new()
    {
        Id = string.IsNullOrWhiteSpace(title.Id) ? CutTextClip.NewId() : title.Id,
        Text = title.Text,
        Start = Math.Max(0, title.StartSec),
        Seconds = title.HoldSeconds,
    };

    private static CutJoinKind? ParseJoinOverride(string? wire) =>
        string.IsNullOrWhiteSpace(wire)
            ? null
            : wire.Trim().ToLowerInvariant() switch
            {
                "cut" => CutJoinKind.Cut,
                "dissolve" => CutJoinKind.Dissolve,
                "dip" or "fadeout" => CutJoinKind.Dip,
                "fadein" => CutJoinKind.FadeIn,
                "fadewhite" => CutJoinKind.FadeWhite,
                "cuttoblack" => CutJoinKind.CutToBlack,
                _ => null,
            };

    private sealed class ProjectDto
    {
        [JsonPropertyName("version")]
        public int SchemaVersion { get; set; }
        public string? MusicFileName { get; set; }
        public List<ClipDto> Clips { get; set; } = [];
        public List<TextClipDto>? TextClips { get; set; }
    }

    private sealed class TextClipDto
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public double Start { get; set; }
        public double Seconds { get; set; }
    }

    private sealed class ClipDto
    {
        public int Scene { get; set; }
        public int Clip { get; set; }
        public double MarkIn { get; set; }
        public double MarkOut { get; set; }
        public List<SpanDto>? RangeDeletes { get; set; }
        public string? JoinOut { get; set; }
        public string? FountainTransition { get; set; }
        public CardDto? Card { get; set; }
    }

    private sealed class SpanDto
    {
        public double Start { get; set; }
        public double End { get; set; }
    }

    private sealed class CardDto
    {
        public bool Enabled { get; set; }
        public string? Text { get; set; }
        public double Seconds { get; set; }
    }
}
