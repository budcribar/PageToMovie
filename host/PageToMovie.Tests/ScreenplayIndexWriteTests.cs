using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ScreenplayIndexWriteTests
{
    [Fact]
    public void PlanBatches_packs_small_sequences_and_splits_huge_ones()
    {
        var index = new ScreenplayIndex
        {
            Acts =
            [
                new ScreenplayIndexAct
                {
                    Id = "a1",
                    Title = "A",
                    Sequences =
                    [
                        Seq("s1", "Ithaca", 4),
                        Seq("s2", "Sparta", 5),
                        Seq("s3", "Cyclops", 40),
                    ],
                },
            ],
        };

        var batches = ScreenplayIndexPlanner.PlanBatches(index, maxCardsPerBatch: 15);
        Assert.Equal(4, batches.Count);
        Assert.Equal(9, batches[0].Cards.Count);
        Assert.Equal("Ithaca / Sparta", batches[0].Title);
        Assert.Equal(15, batches[1].Cards.Count);
        Assert.Equal("Cyclops", batches[1].Title);
        Assert.Equal(15, batches[2].Cards.Count);
        Assert.Equal(10, batches[3].Cards.Count);
        Assert.DoesNotContain(batches, b => b.Cards.Count > 15);
    }

    [Fact]
    public void HeadingCountInRange_allows_err_long_requires_80_percent()
    {
        Assert.True(BookToIndexWriter.HeadingCountInRange(12, 10));
        Assert.True(BookToIndexWriter.HeadingCountInRange(8, 10));
        Assert.False(BookToIndexWriter.HeadingCountInRange(7, 10));
    }

    [Fact]
    public async Task Convert_from_index_stitches_one_heading_per_card()
    {
        var index = new ScreenplayIndex
        {
            MovieTitle = "T",
            Acts =
            [
                new ScreenplayIndexAct
                {
                    Id = "a1",
                    Title = "A",
                    Sequences = [Seq("s1", "Hall", 3), Seq("s2", "Sea", 3)],
                },
            ],
        };
        var book = new string('x', 61_000);
        Assert.True(BookToFountainConverter.ShouldWriteFromIndex(book, "grok-4.6", index));

        var chat = new PerCardChat();
        var fountain = await BookToIndexWriter.ConvertAsync(
            "You are a screenwriter.",
            "The Odyssey",
            "Homer",
            index,
            indexFileId: null,
            new ChatCall(chat, "grok-4.6"));

        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(fountain));
        Assert.Equal(6, BookToFountainConverter.CountSceneHeadings(fountain));
        Assert.Contains("Title:", fountain, StringComparison.OrdinalIgnoreCase);
    }

    private static ScreenplayIndexSequence Seq(string id, string title, int n)
    {
        var scenes = new List<ScreenplayIndexCard>();
        for (var i = 1; i <= n; i++)
        {
            scenes.Add(new ScreenplayIndexCard
            {
                Id = $"{id}.{i}",
                Order = i,
                Heading = $"INT. {title.ToUpperInvariant()} {i} - DAY",
                LocationKey = "Loc_" + title,
                SpeakingCast = ["HERO"],
                Beat = $"Beat {title} {i}.",
                BookAnchorStart = "Start",
                BookAnchorEnd = "End",
            });
        }

        return new ScreenplayIndexSequence { Id = id, Title = title, Scenes = scenes };
    }

    private sealed class PerCardChat : IChatClient
    {
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null,
            string? reasoningEffort = null)
        {
            var sb = new System.Text.StringBuilder();
            if (userPrompt.Contains("first batch", StringComparison.OrdinalIgnoreCase))
                sb.Append("Title: The Odyssey\nAuthor: Homer\n\n");
            foreach (var line in userPrompt.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("- ", StringComparison.Ordinal)) continue;
                var parts = t[2..].Split('|', 3);
                var heading = parts.Length > 1 ? parts[1].Trim() : "INT. ROOM - DAY";
                sb.Append(heading).Append("\n\nHERO\nWe go on.\n\n");
            }

            return Task.FromResult(sb.ToString());
        }
    }
}
