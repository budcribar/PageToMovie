using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>Book → hierarchical <see cref="ScreenplayIndex"/> (one model call, JSON out).</summary>
public static class BookToIndexConverter
{
    public const int InlineBookMaxChars = 80_000;

    public static async Task<ScreenplayIndex> BuildAsync(
        string title,
        string bookText,
        IChatClient chat,
        string model,
        string? author = null,
        Action<string>? onProgress = null,
        IBookFileSession? bookSession = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Screenplay index");
        bookText = BookToFountainConverter.NormalizeBookText(bookText);
        if (string.IsNullOrWhiteSpace(bookText))
            throw new InvalidOperationException("Book text is empty.");

        var system = await AdaptationPromptPack.LoadBookToIndexSystemPromptAsync(ct).ConfigureAwait(false);
        var useFile = bookSession is { IsAvailable: true };
        if (!useFile && bookText.Length > InlineBookMaxChars)
            throw new InvalidOperationException(
                "Book is too large to index without a file_id. Connect Grok Files or prepare the book first.");

        var instruction = BuildInstruction(title, author, useFile ? null : bookText);
        var prev = Stage1BookSessionScope.Current;
        Stage1BookSessionScope.Current = useFile ? bookSession : null;
        try
        {
            if (useFile)
            {
                await bookSession!.EnsureUploadedAsync(ct).ConfigureAwait(false);
                onProgress?.Invoke("Indexing the book via file_id…");
            }
            else
                onProgress?.Invoke("Indexing the book…");

            using var heartbeat = Stage1ProgressHeartbeat.Start(onProgress, "Indexing the book");
            var raw = await ExecuteStage1IndexAsync(
                    chat, system, instruction, model, bookText, onProgress, ct)
                .ConfigureAwait(false);
            if (!ScreenplayIndexParser.TryParse(raw, out var index, out var err) || index is null)
                throw new InvalidOperationException(err);
            var gate = ScreenplayIndexParser.Evaluate(index, bookText);
            index.Warnings = gate.Warnings.ToList();
            if (!gate.Ok)
                throw new InvalidOperationException(
                    "Index failed validation: " + string.Join("; ", gate.Failures.Take(8)));
            foreach (var w in gate.Warnings)
                onProgress?.Invoke("Index note: " + w);
            var rollup = ScreenplayIndexParser.Rollup(index);
            onProgress?.Invoke(
                $"Index ready — {rollup.SceneCards} scenes, {rollup.Sequences} sequences, " +
                $"{rollup.Locations} locations, {rollup.SpeakingCast} speaking.");
            return index;
        }
        finally
        {
            Stage1BookSessionScope.Current = prev;
        }
    }

    private static string BuildInstruction(string title, string? author, string? bookInline)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("INDEX TASK");
        sb.AppendLine("Title hint: " + (string.IsNullOrWhiteSpace(title) ? "(unknown)" : title.Trim()));
        sb.AppendLine("Author hint: " + (string.IsNullOrWhiteSpace(author) ? "(unknown)" : author.Trim()));
        sb.AppendLine("Return screenplay.index.v1 JSON only. Cover the whole book. No scene cap.");
        if (bookInline is not null)
        {
            sb.AppendLine();
            sb.AppendLine("--- BEGIN BOOK ---");
            sb.AppendLine(bookInline);
            sb.AppendLine("--- END BOOK ---");
        }
        else
        {
            sb.AppendLine("The attached file is the full book.");
        }
        return sb.ToString();
    }

    private static async Task<string> ExecuteStage1IndexAsync(
        IChatClient chat,
        string system,
        string user,
        string model,
        string bookText,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var result = await Stage1ChatExecutor.ExecuteAsync(
            chat,
            new Stage1ChatExecutor.Request(
                system, user, model, 0.2,
                ChatCallModes.BookToIndex,
                "stage1-book-to-index-v1",
                "Return valid screenplay.index.v1 JSON covering the entire book. Every card needs heading, beat, and both book anchors. No scene cap.",
                ReasoningEffort: null,
                DeterministicFallback: null,
                OperationName: "stage1_book_to_index"),
            raw => ValidateIndex(raw, bookText),
            ct,
            Stage1BookSessionScope.Current).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Value?.FountainPackage))
        {
            onProgress?.Invoke("Index failed validation.");
            throw new InvalidOperationException("The index call did not produce a valid beat sheet.");
        }
        if (result.Source == Stage1ResultSource.CorrectiveResponse)
            onProgress?.Invoke("Index corrected after validation.");
        return result.Value.FountainPackage;
    }

    private static IReadOnlyList<Stage1ValidationIssue> ValidateIndex(string raw, string bookText)
    {
        if (!ScreenplayIndexParser.TryParse(raw, out var index, out var err) || index is null)
            return [new Stage1ValidationIssue("index_parse", err)];
        var gate = ScreenplayIndexParser.Evaluate(index, bookText);
        if (gate.Ok)
            return Array.Empty<Stage1ValidationIssue>();
        return gate.Failures
            .Select(f => new Stage1ValidationIssue("index_gate", f))
            .ToArray();
    }
}
