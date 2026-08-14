using System.Text;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>Write Fountain from an index in sequence batches, then stitch.</summary>
public static class BookToIndexWriter
{
    public const string IndexWriteModeSuffix = """


        ================================================================================
        INDEX WRITE MODE (HARD)
        ================================================================================
        You are writing ONE BATCH of a max-master screenplay from a beat sheet.
        Write every listed card. Do not collapse voyages into a montage.
        There is no scene-count maximum. This batch is not the whole film.
        Return Fountain only — no markdown, no JSON, no commentary.
        """;

    public static async Task<string> ConvertAsync(
        string system,
        string title,
        string? author,
        ScreenplayIndex index,
        string? indexFileId,
        IChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct,
        double temperature = 0.2,
        IBookFileSession? bookSession = null)
    {
        var batches = ScreenplayIndexPlanner.PlanBatches(index);
        if (batches.Count == 0)
            throw new InvalidOperationException("Index has no scene cards to write.");

        var expected = ScreenplayIndexParser.EnumerateCards(index).Count();
        onProgress?.Invoke(
            $"Writing from index — {expected} cards in {batches.Count} sequence batch(es)…");

        var writeSystem = system + IndexWriteModeSuffix;
        var parts = new List<string>();
        string? prevTail = null;

        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[i];
            onProgress?.Invoke(
                $"Writing sequence {batch.Number}/{batch.Total} — {batch.Title}…");

            var instruction = BuildInstruction(title, author, batch, prevTail, i == 0, !string.IsNullOrWhiteSpace(indexFileId));
            var call = new IndexWriteCall(
                chat, writeSystem, model, temperature, bookSession, indexFileId, onProgress, ct);
            var part = await WriteBatchAsync(call, instruction, batch)
                .ConfigureAwait(false);
            part = BookToFountainConverter.StripBookPageTags(
                BookToFountainConverter.StripFences(part));
            parts.Add(part);
            prevTail = TailLines(part, 20);
        }

        onProgress?.Invoke("Stitching master…");
        var stitched = BookToFountainConverter.StitchFountainParts(parts);
        WarnHeadingRatio(stitched, expected, onProgress);
        if (!BookToFountainConverter.LooksLikeGoodFountain(stitched))
            throw new InvalidOperationException(
                "Indexed write did not produce a usable Fountain screenplay.");
        return stitched;
    }

    public static bool HeadingCountInRange(int headings, int cards) =>
        cards <= 0 || headings >= Math.Max(1, (int)Math.Ceiling(cards * 0.80));

    private sealed record IndexWriteCall(
        IChatClient Chat,
        string System,
        string Model,
        double Temperature,
        IBookFileSession? BookSession,
        string? IndexFileId,
        Action<string>? OnProgress,
        CancellationToken Ct);

    private static async Task<string> WriteBatchAsync(
        IndexWriteCall call,
        string instruction,
        ScreenplayIndexPlanner.Batch batch)
    {
        var raw = await CompleteBatchAsync(call, instruction).ConfigureAwait(false);
        var cleaned = BookToFountainConverter.StripBookPageTags(
            BookToFountainConverter.StripFences(raw ?? ""));
        if (HeadingCountInRange(BookToFountainConverter.CountSceneHeadings(cleaned), batch.Cards.Count) &&
            BookToFountainConverter.LooksLikeGoodFountain(cleaned, requirePageTags: false))
            return cleaned;

        call.OnProgress?.Invoke(
            $"Sequence {batch.Number} looked thin — rewriting that batch only…");
        var retryInstruction = instruction +
            "\n\nREWRITE: Every listed card must appear as its own scene heading. Do not skip cards.";
        var retry = await CompleteBatchAsync(
                call with { Temperature = Math.Min(call.Temperature, 0.15) }, retryInstruction)
            .ConfigureAwait(false);
        var retryClean = BookToFountainConverter.StripBookPageTags(
            BookToFountainConverter.StripFences(retry ?? ""));
        if (BookToFountainConverter.CountSceneHeadings(retryClean) >=
            BookToFountainConverter.CountSceneHeadings(cleaned) &&
            !string.IsNullOrWhiteSpace(retryClean))
            return retryClean;
        return cleaned;
    }

    private static async Task<string?> CompleteBatchAsync(IndexWriteCall call, string instruction)
    {
        using var heartbeat = Stage1ProgressHeartbeat.Start(call.OnProgress, "Writing sequence");
        if (call.BookSession is { IsAvailable: true })
        {
            var extras = string.IsNullOrWhiteSpace(call.IndexFileId)
                ? Array.Empty<string>()
                : new[] { call.IndexFileId };
            return await call.BookSession.CompleteWithFilesAsync(
                call.System, instruction, extras, call.Model, call.Temperature, call.Ct).ConfigureAwait(false);
        }

        return await call.Chat.CompleteAsync(
            call.System, instruction, call.Model, call.Temperature, call.Ct, ChatCallModes.BookToFountainIndex)
            .ConfigureAwait(false);
    }

    internal static string BuildInstruction(
        string title,
        string? author,
        ScreenplayIndexPlanner.Batch batch,
        string? previousTail,
        bool isFirst,
        bool indexAttached)
    {
        var sb = new StringBuilder();
        sb.AppendLine("INDEX WRITE TASK");
        sb.AppendLine($"Project title: {title}");
        sb.AppendLine($"Author: {author ?? "(unknown)"}");
        sb.AppendLine($"Batch {batch.Number}/{batch.Total} — {batch.Title}");
        sb.AppendLine($"Cards {batch.Cards[0].Order}–{batch.Cards[^1].Order} ({batch.Cards.Count} cards).");
        sb.AppendLine("Write Fountain for THESE cards only. Do not write other sequences.");
        sb.AppendLine("Each card is one INT./EXT. scene (a pair only if the beat requires a cut).");
        sb.AppendLine("Preserve book-faithful dialogue. No markdown fences.");
        if (isFirst)
            sb.AppendLine("This is the first batch: include a Fountain title page (Title, Author, Source, Draft date).");
        else
            sb.AppendLine("Later batch: scenes only — no title page, no FADE OUT / THE END.");
        if (indexAttached)
            sb.AppendLine("The attached files are the full book and the index. Ground dialogue in the book.");
        if (!string.IsNullOrWhiteSpace(previousTail))
        {
            sb.AppendLine();
            sb.AppendLine("Continuity (last lines of the previous batch):");
            sb.AppendLine(previousTail.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("CARDS:");
        foreach (var card in batch.Cards)
        {
            sb.Append("- ").Append(card.Id);
            sb.Append(" | ").Append(card.Heading);
            if (!string.IsNullOrWhiteSpace(card.LocationKey))
                sb.Append(" | ").Append(card.LocationKey);
            if (card.SpeakingCast is { Count: > 0 })
                sb.Append(" | ").Append(string.Join(", ", card.SpeakingCast));
            sb.Append(" | ").Append(card.Beat);
            sb.AppendLine();
        }

        sb.AppendLine("Return the Fountain for this batch only.");
        return sb.ToString();
    }

    private static void WarnHeadingRatio(string fountain, int cards, Action<string>? onProgress)
    {
        var heads = BookToFountainConverter.CountSceneHeadings(fountain);
        if (HeadingCountInRange(heads, cards))
        {
            onProgress?.Invoke($"Master stitched — {heads} scenes from {cards} index cards.");
            return;
        }

        onProgress?.Invoke(
            $"Index write thin — {heads} headings vs {cards} cards (want ≥80%). Using stitched draft.");
    }

    private static string TailLines(string text, int count)
    {
        var coalesced = text ?? "";
        var lines = coalesced.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= count) return coalesced.Trim();
        return string.Join('\n', lines.TakeLast(count)).Trim();
    }
}
