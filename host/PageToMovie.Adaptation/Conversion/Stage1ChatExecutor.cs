using PageToMovie.Core.Abstractions;

namespace PageToMovie.Adaptation.Conversion;

/// <summary>
/// Ambient book file session for Stage‑1 (set by <c>ConvertWithMetadataAsync</c>).
/// Avoids threading optional session through every private helper.
/// </summary>
internal static class Stage1BookSessionScope
{
    private static readonly AsyncLocal<IBookFileSession?> Holder = new();
    public static IBookFileSession? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}

/// <summary>
/// Ambient Fountain file session for Stage‑1 merge / repairs (set by <c>ConvertWithMetadataAsync</c>).
/// </summary>
internal static class Stage1FountainSessionScope
{
    private static readonly AsyncLocal<IFountainFileSession?> Holder = new();
    public static IFountainFileSession? Current
    {
        get => Holder.Value;
        set => Holder.Value = value;
    }
}

/// <summary>
/// Stage‑1 chat loop: primary → optional validation correction → deterministic fallback.
/// Mirrors Engine Stage1FountainLifecycle behavior without ModelExecution dependencies.
/// When an <see cref="IBookFileSession"/> is available, the book rides on file_id /
/// previous_response_id so follow-ups (coverage, correction, merge, repair) do not re-bill
/// full book tokens.
/// </summary>
internal static class Stage1ChatExecutor
{
    internal sealed record Request(
        string SystemPrompt,
        string UserPrompt,
        string Model,
        double Temperature,
        string Mode,
        string PromptVersion,
        string CorrectionInstruction,
        string? ReasoningEffort = null,
        string? DeterministicFallback = null,
        string OperationName = "stage1_book_to_fountain");

    internal sealed record Response(string FountainPackage);

    internal sealed record Result(
        Response? Value,
        Stage1ResultSource Source,
        bool Success);

    internal static async Task<Result> ExecuteAsync(
        IChatClient chat,
        Request request,
        Func<string, IReadOnlyList<Stage1ValidationIssue>> validate,
        CancellationToken ct,
        IBookFileSession? bookSession = null,
        IFountainFileSession? fountainSession = null)
    {
        fountainSession ??= Stage1FountainSessionScope.Current;
        bookSession ??= Stage1BookSessionScope.Current;

        // Fountain file (merge/repairs) wins over book chain — do not paste 60k words
        // into a book follow-up.
        var primaryRaw = await CompletePrimaryRawAsync(
            chat, request, fountainSession, bookSession, ct).ConfigureAwait(false);

        var primaryCleaned = BookToFountainConverter.StripBookPageTags(
            BookToFountainConverter.StripFences(primaryRaw ?? ""));
        var primaryIssues = validate(primaryCleaned)
            .Where(i => !string.IsNullOrWhiteSpace(i.Code))
            .ToList();

        if (IsUsableResponse(primaryCleaned, primaryIssues))
        {
            return new Result(new Response(primaryCleaned), Stage1ResultSource.PrimaryResponse, Success: true);
        }

        var correctiveRaw = await TryCompleteCorrectionAsync(
            chat, request, fountainSession, bookSession, primaryRaw, primaryIssues, ct)
            .ConfigureAwait(false);

        if (correctiveRaw is not null)
        {
            var corrCleaned = BookToFountainConverter.StripBookPageTags(
                BookToFountainConverter.StripFences(correctiveRaw));
            var corrIssues = validate(corrCleaned);
            if (IsUsableResponse(corrCleaned, corrIssues))
            {
                return new Result(new Response(corrCleaned), Stage1ResultSource.CorrectiveResponse, Success: true);
            }
        }

        if (request.DeterministicFallback is not null)
        {
            return new Result(
                new Response(request.DeterministicFallback),
                Stage1ResultSource.DeterministicFallback,
                Success: true);
        }

        return new Result(null, Stage1ResultSource.Failed, Success: false);
    }

    private static bool IsUsableResponse(string cleaned, IReadOnlyList<Stage1ValidationIssue> issues) =>
        !string.IsNullOrWhiteSpace(cleaned) && issues.Count == 0;

    private static string FormatFindings(IReadOnlyList<Stage1ValidationIssue> issues) =>
        string.Join("\n", issues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"));

    private static string BuildShortCorrection(Request request, string findings, string closingLine) =>
        $"CORRECTIVE ATTEMPT ({request.PromptVersion})\n" +
        $"{request.CorrectionInstruction}\n" +
        $"Validation findings:\n{findings}\n" +
        closingLine;

    private static async Task<string> CompletePrimaryRawAsync(
        IChatClient chat,
        Request request,
        IFountainFileSession? fountainSession,
        IBookFileSession? bookSession,
        CancellationToken ct)
    {
        if (fountainSession is { IsAvailable: true })
        {
            return await fountainSession.CompleteAsync(
                request.SystemPrompt, request.UserPrompt, request.Model,
                request.Temperature, ct).ConfigureAwait(false);
        }

        if (bookSession is { IsAvailable: true })
        {
            // Later Stage‑1 ops (chunk 2+, merge, repair) reuse previous_response_id.
            if (!string.IsNullOrWhiteSpace(bookSession.LastResponseId))
            {
                return await bookSession.CompleteFollowUpAsync(
                    request.UserPrompt, request.Model, request.Temperature, ct,
                    request.Mode, request.ReasoningEffort).ConfigureAwait(false);
            }

            return await bookSession.CompletePrimaryAsync(
                request.SystemPrompt, request.UserPrompt, request.Model,
                request.Temperature, ct, request.Mode, request.ReasoningEffort).ConfigureAwait(false);
        }

        return await CompleteWithTransientRetryAsync(
            new ChatCall(chat, request.Model, ct, temperature: request.Temperature, reasoningEffort: request.ReasoningEffort),
            request.SystemPrompt, request.UserPrompt, request.Mode).ConfigureAwait(false);
    }

    private static async Task<string?> TryCompleteCorrectionAsync(
        IChatClient chat,
        Request request,
        IFountainFileSession? fountainSession,
        IBookFileSession? bookSession,
        string? primaryRaw,
        IReadOnlyList<Stage1ValidationIssue> primaryIssues,
        CancellationToken ct)
    {
        var findings = FormatFindings(primaryIssues);
        var correctionUser = request.UserPrompt + $"""


            CORRECTIVE ATTEMPT ({request.PromptVersion})
            {request.CorrectionInstruction}
            Validation findings:
            {findings}
            Return the complete corrected Fountain package only. Do not return a patch list.
            Previous response:
            --- BEGIN PREVIOUS RESPONSE ---
            {primaryRaw}
            --- END PREVIOUS RESPONSE ---
            """;

        var corrTemp = Math.Min(request.Temperature, 0.15);
        try
        {
            if (fountainSession is { IsAvailable: true })
            {
                var shortCorrection = BuildShortCorrection(
                    request, findings,
                    "The Fountain is still attached. Return the complete corrected Fountain only.");
                return await fountainSession.CompleteAsync(
                    request.SystemPrompt, shortCorrection, request.Model, corrTemp, ct)
                    .ConfigureAwait(false);
            }

            if (bookSession is { IsAvailable: true })
            {
                // Follow-up: only correction instructions — no book body re-send.
                var shortCorrection = BuildShortCorrection(
                    request, findings,
                    "Return the complete corrected Fountain package only. Do not return a patch list.");
                return await bookSession.CompleteFollowUpAsync(
                    shortCorrection, request.Model, corrTemp, ct,
                    request.Mode + "_correction", request.ReasoningEffort).ConfigureAwait(false);
            }

            return await CompleteWithTransientRetryAsync(
                new ChatCall(chat, request.Model, ct, temperature: corrTemp, reasoningEffort: request.ReasoningEffort),
                request.SystemPrompt, correctionUser, request.Mode + "_correction").ConfigureAwait(false);
        }
        catch
        {
            /* fall through to deterministic */
            return null;
        }
    }

    private static async Task<string> CompleteWithTransientRetryAsync(
        ChatCall chat,
        string system,
        string user,
        string mode,
        int maxAttempts = 2)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            chat.Ct.ThrowIfCancellationRequested();
            try
            {
                return await chat.Chat.CompleteAsync(
                    system, user, chat.Model, chat.Temperature, chat.Ct,
                    mode: mode, reasoningEffort: chat.ReasoningEffort).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                last = ex;
                await Task.Delay(200 * attempt * attempt, chat.Ct).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("Stage1 chat failed.");
    }

    private static bool IsTransient(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("429", StringComparison.Ordinal) ||
            msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("temporar", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("503", StringComparison.Ordinal) ||
            msg.Contains("502", StringComparison.Ordinal))
            return true;
        return ex is HttpRequestException or TaskCanceledException or TimeoutException;
    }
}
