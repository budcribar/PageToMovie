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

        var packed = new List<(string Title, List<string> SeqIds, List<ScreenplayIndexCard> Cards)>();
        List<ScreenplayIndexCard>? curCards = null;
        List<string>? curSeqs = null;
        var curTitle = "";

        foreach (var seq in index.Acts.SelectMany(a => a.Sequences))
        {
            var scenes = (seq.Scenes ?? []).Where(s => s is not null).ToList();
            if (scenes.Count == 0) continue;

            if (scenes.Count > maxCardsPerBatch)
            {
                Flush();
                for (var i = 0; i < scenes.Count; i += maxCardsPerBatch)
                {
                    var slice = scenes.Skip(i).Take(maxCardsPerBatch).ToList();
                    packed.Add((seq.Title, [seq.Id], slice));
                }
                continue;
            }

            if (curCards is null)
            {
                Start(seq.Title, seq.Id, scenes);
                continue;
            }

            if (curCards.Count + scenes.Count > maxCardsPerBatch)
            {
                Flush();
                Start(seq.Title, seq.Id, scenes);
            }
            else
            {
                curCards.AddRange(scenes);
                if (!string.IsNullOrWhiteSpace(seq.Id))
                    curSeqs!.Add(seq.Id);
                if (!string.IsNullOrWhiteSpace(seq.Title) &&
                    !curTitle.Contains(seq.Title, StringComparison.OrdinalIgnoreCase))
                    curTitle = string.IsNullOrWhiteSpace(curTitle) ? seq.Title : curTitle + " / " + seq.Title;
            }
        }

        Flush();

        var total = packed.Count;
        return packed.Select((g, i) => new Batch
        {
            Number = i + 1,
            Total = total,
            Title = string.IsNullOrWhiteSpace(g.Title) ? $"Batch {i + 1}" : g.Title,
            SequenceIds = g.SeqIds,
            Cards = g.Cards,
        }).ToList();

        void Start(string title, string id, List<ScreenplayIndexCard> scenes)
        {
            curTitle = title ?? "";
            curSeqs = string.IsNullOrWhiteSpace(id) ? [] : [id];
            curCards = [.. scenes];
        }

        void Flush()
        {
            if (curCards is { Count: > 0 })
                packed.Add((curTitle, curSeqs ?? [], curCards));
            curCards = null;
            curSeqs = null;
            curTitle = "";
        }
    }
}
