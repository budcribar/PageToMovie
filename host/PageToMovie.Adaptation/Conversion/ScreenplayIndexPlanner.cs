using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>Pack index sequences into write batches (8–15 cards). Never split on raw book chars.</summary>
public static class ScreenplayIndexPlanner
{
    public const int DefaultMaxCardsPerBatch = 15;

    public sealed class Batch
    {
        public int Number { get; init; }
        public int Total { get; init; }
        public string Title { get; init; } = "";
        public IReadOnlyList<string> SequenceIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ScreenplayIndexCard> Cards { get; init; } = Array.Empty<ScreenplayIndexCard>();
    }

    public static IReadOnlyList<Batch> PlanBatches(
        ScreenplayIndex index, int maxCardsPerBatch = DefaultMaxCardsPerBatch)
    {
        ArgumentNullException.ThrowIfNull(index);
        maxCardsPerBatch = Math.Clamp(maxCardsPerBatch, 4, 24);

        var packed = new List<PackedGroup>();
        var current = new OpenBatch();
        foreach (var seq in index.Acts.SelectMany(a => a.Sequences))
            PackSequence(packed, current, seq, maxCardsPerBatch);

        current.FlushInto(packed);
        return ToBatches(packed);
    }

    private static void PackSequence(
        List<PackedGroup> packed,
        OpenBatch current,
        ScreenplayIndexSequence seq,
        int maxCardsPerBatch)
    {
        var scenes = UsableScenes(seq);
        if (scenes.Count == 0)
            return;

        if (scenes.Count > maxCardsPerBatch)
        {
            current.FlushInto(packed);
            AddOverflowSlices(packed, seq, scenes, maxCardsPerBatch);
            return;
        }

        if (current.IsEmpty)
        {
            current.Start(seq, scenes);
            return;
        }

        if (current.CardCount + scenes.Count > maxCardsPerBatch)
        {
            current.FlushInto(packed);
            current.Start(seq, scenes);
            return;
        }

        current.Append(seq, scenes);
    }

    private static List<ScreenplayIndexCard> UsableScenes(ScreenplayIndexSequence seq) =>
        (seq.Scenes ?? []).Where(s => s is not null).ToList();

    private static void AddOverflowSlices(
        List<PackedGroup> packed,
        ScreenplayIndexSequence seq,
        List<ScreenplayIndexCard> scenes,
        int maxCardsPerBatch)
    {
        for (var i = 0; i < scenes.Count; i += maxCardsPerBatch)
        {
            packed.Add(new PackedGroup(
                seq.Title,
                [seq.Id],
                scenes.Skip(i).Take(maxCardsPerBatch).ToList()));
        }
    }

    private static IReadOnlyList<Batch> ToBatches(List<PackedGroup> packed)
    {
        var total = packed.Count;
        return packed.Select((g, i) => new Batch
        {
            Number = i + 1,
            Total = total,
            Title = BatchTitle(g.Title, i + 1),
            SequenceIds = g.SeqIds,
            Cards = g.Cards,
        }).ToList();
    }

    private static string BatchTitle(string title, int number) =>
        string.IsNullOrWhiteSpace(title) ? $"Batch {number}" : title;

    private static string CombineTitle(string current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return current;
        if (current.Contains(incoming, StringComparison.OrdinalIgnoreCase))
            return current;
        return string.IsNullOrWhiteSpace(current) ? incoming : current + " / " + incoming;
    }

    private readonly record struct PackedGroup(
        string Title,
        List<string> SeqIds,
        List<ScreenplayIndexCard> Cards);

    private sealed class OpenBatch
    {
        private string _title = "";
        private List<string> _seqIds = [];
        private List<ScreenplayIndexCard>? _cards;

        public bool IsEmpty => _cards is null;

        public int CardCount => _cards?.Count ?? 0;

        public void Start(ScreenplayIndexSequence seq, List<ScreenplayIndexCard> scenes)
        {
            _title = seq.Title ?? "";
            _seqIds = string.IsNullOrWhiteSpace(seq.Id) ? [] : [seq.Id];
            _cards = [.. scenes];
        }

        public void Append(ScreenplayIndexSequence seq, List<ScreenplayIndexCard> scenes)
        {
            if (_cards is null)
                return;

            _cards.AddRange(scenes);
            if (!string.IsNullOrWhiteSpace(seq.Id))
                _seqIds.Add(seq.Id);
            _title = CombineTitle(_title, seq.Title);
        }

        public void FlushInto(List<PackedGroup> packed)
        {
            if (_cards is { Count: > 0 })
                packed.Add(new PackedGroup(_title, _seqIds, _cards));
            _cards = null;
            _seqIds = [];
            _title = "";
        }
    }
}
